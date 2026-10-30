using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Mintokei.Runner;

/// <summary>
/// Runner-side SQLite-backed outbox for durable output delivery.
/// Uses per-operation connections (pooled by the driver) for thread safety,
/// with WAL mode and proper transactions.
/// </summary>
public sealed class LocalOutbox
{
    private const string DbFileName = "runner-outbox.db";
    private readonly string _connectionString;

    public LocalOutbox(IOptions<RunnerOptions> options)
    {
        var dataDir = options.Value.DataDir ?? RunnerPaths.ResolveDataDirectory(null);
        _connectionString = $"Data Source={Path.Combine(dataDir, DbFileName)}";
    }

    public async Task InitializeAsync()
    {
        await using var conn = await OpenAsync();

        // Enable WAL mode for better concurrent read/write performance
        await ExecuteAsync(conn, "PRAGMA journal_mode=WAL");

        await ExecuteAsync(conn, """
            CREATE TABLE IF NOT EXISTS OutboxMessage (
                Id TEXT PRIMARY KEY,
                SequenceNumber INTEGER NOT NULL,
                MessageType TEXT NOT NULL,
                Status TEXT NOT NULL DEFAULT 'Pending',
                PayloadJson TEXT NOT NULL,
                CorrelationId TEXT,
                CreatedAt TEXT NOT NULL,
                SentAt TEXT,
                AckedAt TEXT
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_OutboxMessage_Seq ON OutboxMessage(SequenceNumber);
            CREATE INDEX IF NOT EXISTS IX_OutboxMessage_Correlation_Seq
                ON OutboxMessage(CorrelationId, SequenceNumber);
            CREATE INDEX IF NOT EXISTS IX_OutboxMessage_Pending_Seq
                ON OutboxMessage(SequenceNumber) WHERE Status = 'Pending';
            CREATE INDEX IF NOT EXISTS IX_OutboxMessage_Pending_Correlation_Seq
                ON OutboxMessage(CorrelationId, SequenceNumber) WHERE Status = 'Pending';

            -- Track only streams touched by this version. Do not replay the historical
            -- pre-ack backlog from old installations when upgrading.
            CREATE TABLE IF NOT EXISTS OutboxStream (
                CorrelationId TEXT PRIMARY KEY,
                CompletionSequence INTEGER
            );

            CREATE TABLE IF NOT EXISTS RunnerConfig (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );
            """);
    }

    public async Task<long> InsertAsync(string messageType, string payloadJson, Guid? correlationId)
    {
        await using var conn = await OpenAsync();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();

        var seq = await GetNextSequenceInternalAsync(conn);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO OutboxMessage (Id, SequenceNumber, MessageType, Status, PayloadJson, CorrelationId, CreatedAt)
            VALUES (@id, @seq, @type, 'Pending', @payload, @corr, @created)
            """;
        cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("@seq", seq);
        cmd.Parameters.AddWithValue("@type", messageType);
        cmd.Parameters.AddWithValue("@payload", payloadJson);
        cmd.Parameters.AddWithValue("@corr", correlationId?.ToString() ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@created", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync();

        if (correlationId is not null && messageType is "ReportProcessOutput" or "ReportProcessCompleted")
        {
            cmd.CommandText = """
                INSERT INTO OutboxStream (CorrelationId, CompletionSequence) VALUES (@corr, @completed)
                ON CONFLICT(CorrelationId) DO UPDATE SET
                    CompletionSequence = COALESCE(excluded.CompletionSequence, CompletionSequence);
                """;
            cmd.Parameters.AddWithValue("@completed", messageType == "ReportProcessCompleted" ? seq : DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        return seq;
    }

    public async Task<long> GetNextSequenceAsync()
    {
        await using var conn = await OpenAsync();
        return await GetNextSequenceInternalAsync(conn);
    }

    public async Task SetSequenceFloorAsync(long floor)
    {
        await using var conn = await OpenAsync();
        await UpsertConfigAsync(conn, "SequenceFloor", floor.ToString());
    }

    /// <summary>
    /// Returns the oldest <paramref name="limit"/> Pending rows.
    /// When <paramref name="correlationFilter"/> is non-null, rows are limited to
    /// those whose CorrelationId is in the set (or NULL — heartbeat-style messages
    /// without a correlation always drain). This filtering is what keeps a stale
    /// pre-restart correlation (whose per-task gRPC stream will never reopen)
    /// from holding the head of the queue forever — the drain previously
    /// returned the same 200 dead rows on every sweep, starving every live
    /// correlation behind them.
    /// </summary>
    public async Task<List<(long Seq, string Type, string Payload, Guid? CorrelationId)>> GetPendingAsync(
        int limit = 100,
        IReadOnlyCollection<Guid>? correlationFilter = null)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();

        if (correlationFilter is null)
        {
            cmd.CommandText = """
                SELECT SequenceNumber, MessageType, PayloadJson, CorrelationId
                FROM OutboxMessage INDEXED BY IX_OutboxMessage_Pending_Seq
                WHERE Status = 'Pending'
                ORDER BY SequenceNumber
                LIMIT @limit
                """;
        }
        else if (correlationFilter.Count == 0)
        {
            // Empty filter ⇒ no live correlations ⇒ only NULL-correlation rows
            // are eligible. Avoids building an `IN ()` clause which is invalid
            // SQL.
            cmd.CommandText = """
                SELECT SequenceNumber, MessageType, PayloadJson, CorrelationId
                FROM OutboxMessage INDEXED BY IX_OutboxMessage_Pending_Correlation_Seq
                WHERE Status = 'Pending' AND CorrelationId IS NULL
                ORDER BY SequenceNumber
                LIMIT @limit
                """;
        }
        else
        {
            var paramNames = new List<string>(correlationFilter.Count);
            var i = 0;
            foreach (var corr in correlationFilter)
            {
                var name = $"@c{i++}";
                paramNames.Add(name);
                cmd.Parameters.AddWithValue(name, corr.ToString());
            }
            cmd.CommandText = $"""
                SELECT SequenceNumber, MessageType, PayloadJson, CorrelationId
                FROM OutboxMessage INDEXED BY IX_OutboxMessage_Pending_Correlation_Seq
                WHERE Status = 'Pending'
                  AND CorrelationId IS NULL
                UNION ALL
                SELECT SequenceNumber, MessageType, PayloadJson, CorrelationId
                FROM OutboxMessage INDEXED BY IX_OutboxMessage_Pending_Correlation_Seq
                WHERE Status = 'Pending'
                  AND CorrelationId IN ({string.Join(",", paramNames)})
                ORDER BY SequenceNumber
                LIMIT @limit
                """;
        }

        cmd.Parameters.AddWithValue("@limit", limit);
        return await ReadMessagesAsync(cmd);
    }

    public async Task MarkSentAsync(long sequenceNumber)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE OutboxMessage SET Status = 'Sent', SentAt = @now
            WHERE SequenceNumber = @seq AND Status = 'Pending'
            """;
        cmd.Parameters.AddWithValue("@seq", sequenceNumber);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task MarkAckedUpToAsync(long sequence)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE OutboxMessage SET Status = 'Acked', AckedAt = @now
            WHERE SequenceNumber <= @seq AND Status != 'Acked'
            """;
        cmd.Parameters.AddWithValue("@seq", sequence);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Retires only this stream's acknowledged output. Sequences are allocated per machine, but
    /// task streams arrive independently: an ack for B must never delete A's unsent output.
    /// Keep the sequence floor in the same transaction so removing the highest row cannot reuse
    /// an id the server has already seen. Duplicate/late acks are harmless.
    /// </summary>
    public async Task<bool> AcknowledgeAsync(Guid correlationId, long sequence)
    {
        if (sequence <= 0) return false;
        await using var conn = await OpenAsync();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO RunnerConfig (Key, Value) VALUES ('SequenceFloor', @seq)
            ON CONFLICT(Key) DO UPDATE SET Value = MAX(CAST(Value AS INTEGER), @seq);
            DELETE FROM OutboxMessage
            WHERE CorrelationId = @corr AND SequenceNumber <= @seq;
            """;
        cmd.Parameters.AddWithValue("@seq", sequence);
        cmd.Parameters.AddWithValue("@corr", correlationId.ToString());
        await cmd.ExecuteNonQueryAsync();
        cmd.CommandText = "DELETE FROM OutboxStream WHERE CorrelationId = @corr AND CompletionSequence <= @seq";
        var completed = await cmd.ExecuteNonQueryAsync() > 0;
        await tx.CommitAsync();
        return completed;
    }

    /// <summary>Recover this stream only, including legacy Sent rows the API never received.</summary>
    public async Task<bool> ResumeStreamAsync(Guid correlationId, long receivedSequence)
    {
        var completed = await AcknowledgeAsync(correlationId, receivedSequence);
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE OutboxMessage SET Status = 'Pending', SentAt = NULL
            WHERE CorrelationId = @corr AND SequenceNumber > @seq AND Status = 'Sent';
            """;
        cmd.Parameters.AddWithValue("@corr", correlationId.ToString());
        cmd.Parameters.AddWithValue("@seq", receivedSequence);
        await cmd.ExecuteNonQueryAsync();
        return completed;
    }

    /// <summary>Includes exited processes until their completion has been acknowledged.</summary>
    public async Task<IReadOnlyCollection<Guid>> GetRecoverableCorrelationsAsync()
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT CorrelationId FROM OutboxStream";
        await using var reader = await cmd.ExecuteReaderAsync();
        var result = new List<Guid>();
        while (await reader.ReadAsync()) result.Add(Guid.Parse(reader.GetString(0)));
        return result;
    }

    public async Task<int> ResetSentToPendingAboveAsync(long sequence)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE OutboxMessage SET Status = 'Pending', SentAt = NULL
            WHERE SequenceNumber > @seq AND Status = 'Sent'
            """;
        cmd.Parameters.AddWithValue("@seq", sequence);
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task CleanupAsync()
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM OutboxMessage WHERE Status = 'Acked'";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<long> GetLastAckedBackendSequenceAsync()
    {
        await using var conn = await OpenAsync();
        return await GetConfigLongAsync(conn, "LastAckedBackendSequence");
    }

    public async Task SetLastAckedBackendSequenceAsync(long sequence)
    {
        await using var conn = await OpenAsync();
        await UpsertConfigAsync(conn, "LastAckedBackendSequence", sequence.ToString());
    }

    // --- private helpers ---

    private async Task<SqliteConnection> OpenAsync()
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await ExecuteAsync(conn, "PRAGMA busy_timeout=5000");
        return conn;
    }

    private static async Task<long> GetNextSequenceInternalAsync(SqliteConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT MAX(
                COALESCE((SELECT MAX(SequenceNumber) FROM OutboxMessage), 0),
                COALESCE((SELECT CAST(Value AS INTEGER) FROM RunnerConfig WHERE Key = 'SequenceFloor'), 0)
            ) + 1
            """;
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(result);
    }

    private static async Task<long> GetConfigLongAsync(SqliteConnection conn, string key)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Value FROM RunnerConfig WHERE Key = @key";
        cmd.Parameters.AddWithValue("@key", key);
        var result = await cmd.ExecuteScalarAsync();
        return result is not null ? Convert.ToInt64(result) : 0;
    }

    private static async Task UpsertConfigAsync(SqliteConnection conn, string key, string value)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO RunnerConfig (Key, Value) VALUES (@key, @val)
            ON CONFLICT(Key) DO UPDATE SET Value = @val
            """;
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@val", value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(SqliteConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<List<(long, string, string, Guid?)>> ReadMessagesAsync(SqliteCommand cmd)
    {
        var results = new List<(long, string, string, Guid?)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            Guid? correlationId = reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3));
            results.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), correlationId));
        }
        return results;
    }
}
