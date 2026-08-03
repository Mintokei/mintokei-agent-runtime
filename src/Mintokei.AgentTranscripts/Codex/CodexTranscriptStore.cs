using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Microsoft.Data.Sqlite;

using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.Contracts;

namespace Mintokei.AgentTranscripts.Codex;

/// <summary>
/// Codex's session store: a "rollout" JSON-lines transcript per session under
/// <c>&lt;home&gt;/sessions/YYYY/MM/DD/rollout-&lt;timestamp&gt;-&lt;id&gt;.jsonl</c>, where
/// <c>home</c> is <c>CODEX_HOME</c> or <c>~/.codex</c>, plus a row in the <c>threads</c> table of
/// <c>&lt;home&gt;/state_*.sqlite</c>.
///
/// The split matters when writing: <c>codex exec resume &lt;id&gt;</c> finds a session from the
/// transcript alone, but the interactive picker lists what is in <c>threads</c>. Writing only the
/// file produces a session that resumes fine and appears to have vanished.
///
/// Unlike Claude, none of the engine's Codex parsing is reusable here.
/// <see cref="Mintokei.AgentEngine.Codex.CodexStreamParser"/> dispatches on the JSON-RPC
/// <c>method</c> of the <c>codex app-server</c> protocol (<c>item/completed</c>,
/// <c>turn/completed</c>); rollout files carry no <c>method</c> at all, using
/// <c>response_item</c> / <c>event_msg</c> / <c>session_meta</c> / <c>turn_context</c> instead.
/// Two different wire formats for the same conversation, so this is a separate reader.
/// </summary>
public sealed class CodexTranscriptStore : ITranscriptStore
{
    private const string ExecCommandTool = "exec_command";

    private readonly string _home;
    private readonly ILogger? _logger;

    /// <param name="home">Codex home directory. Null resolves <c>CODEX_HOME</c>, then <c>~/.codex</c>.</param>
    /// <param name="logger">Optional diagnostics.</param>
    public CodexTranscriptStore(string? home = null, ILogger? logger = null)
    {
        _home = home
            ?? Environment.GetEnvironmentVariable("CODEX_HOME")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        _logger = logger;
    }

    public AgentToolKey Tool => AgentToolKey.CodexCli;

    private string SessionsRoot => Path.Combine(_home, "sessions");

    /// <summary>
    /// Newest <c>state_N.sqlite</c>. Codex bumps N when it migrates the schema and leaves the old
    /// file behind, so the highest N — not the first match — is the live index.
    /// </summary>
    internal string? StateDatabase()
    {
        if (!Directory.Exists(_home))
            return null;
        return Directory.EnumerateFiles(_home, "state_*.sqlite")
            .Select(p => (Path: p, N: int.TryParse(
                Regex.Match(Path.GetFileNameWithoutExtension(p), @"\d+$").Value, out var n) ? n : -1))
            .OrderByDescending(x => x.N)
            .Select(x => x.Path)
            .FirstOrDefault();
    }

    // ── read ──────────────────────────────────────────────────────────────

    public async IAsyncEnumerable<StoredTranscriptInfo> ListAsync(
        string? cwd = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Codex maintains an index for its own picker, with the title and cwd already extracted.
        // Reading it beats opening every rollout: it is indexed on cwd, and it is the only place a
        // title exists at all — the transcript itself has none.
        var indexed = ListFromIndex(cwd);
        if (indexed is not null)
        {
            foreach (var info in indexed)
            {
                ct.ThrowIfCancellationRequested();
                yield return info;
            }
            yield break;
        }

        if (!Directory.Exists(SessionsRoot))
            yield break;

        // No index (a fresh CODEX_HOME, a schema this version does not know): fall back to reading
        // headers, which always works and is merely slower.
        var files = Directory.EnumerateFiles(SessionsRoot, "rollout-*.jsonl", SearchOption.AllDirectories)
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.LastWriteTimeUtc);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var info = await ReadHeaderAsync(file, ct);
            if (info is null)
                continue;
            if (cwd is not null && !string.Equals(info.Cwd, cwd, StringComparison.Ordinal))
                continue;
            yield return info;
        }
    }

    /// <summary>
    /// Reads the <c>threads</c> index, or null when there is nothing usable to read — a missing
    /// database, no such table, or a schema without the columns this needs. Null means "fall back",
    /// never "no sessions", because reporting an empty list would look like the user has none.
    /// </summary>
    private List<StoredTranscriptInfo>? ListFromIndex(string? cwd)
    {
        var db = StateDatabase();
        if (db is null)
            return null;

        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = db,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString());
            conn.Open();

            var columns = new HashSet<string>(StringComparer.Ordinal);
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA table_info(threads)";
                using var r = pragma.ExecuteReader();
                while (r.Read())
                    columns.Add(r.GetString(1));
            }
            if (!columns.Contains("id") || !columns.Contains("cwd"))
                return null;

            var title = columns.Contains("title") ? "title" : "''";
            var first = columns.Contains("first_user_message") ? "first_user_message" : "''";
            var updated = columns.Contains("updated_at") ? "updated_at" : "0";
            var archived = columns.Contains("archived") ? "archived = 0" : "1 = 1";

            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"SELECT id, cwd, {title}, {first}, {updated} FROM threads "
                + $"WHERE {archived}" + (cwd is null ? "" : " AND cwd = $cwd")
                + $" ORDER BY {updated} DESC";
            if (cwd is not null)
                cmd.Parameters.AddWithValue("$cwd", cwd);

            var results = new List<StoredTranscriptInfo>();
            using var rows = cmd.ExecuteReader();
            while (rows.Read())
            {
                var seconds = rows.IsDBNull(4) ? 0 : rows.GetInt64(4);
                results.Add(new StoredTranscriptInfo
                {
                    Tool = Tool,
                    SessionId = rows.GetString(0),
                    Cwd = rows.IsDBNull(1) ? string.Empty : rows.GetString(1),
                    Title = Blank(rows, 2),
                    FirstUserMessage = Blank(rows, 3),
                    UpdatedAt = seconds > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                        : default,
                });
            }
            return results;
        }
        catch (SqliteException ex)
        {
            _logger?.LogDebug(ex, "Could not read the Codex thread index; falling back to file scan");
            return null;
        }
    }

    private static string? Blank(SqliteDataReader r, int i)
    {
        if (r.IsDBNull(i))
            return null;
        var value = r.GetString(i);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private async Task<StoredTranscriptInfo?> ReadHeaderAsync(FileInfo file, CancellationToken ct)
    {
        string? id = null, cwd = null, firstUser = null;
        var scanned = 0;

        using var reader = new StreamReader(file.FullName);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (++scanned > 200)
                break;
            if (!TryParseLine(line, out var root) || !root.TryGetProperty("payload", out var payload))
                continue;

            switch (GetString(root, "type"))
            {
                case "session_meta":
                    id ??= GetString(payload, "id");
                    cwd ??= GetString(payload, "cwd");
                    break;
                case "response_item" when firstUser is null
                    && GetString(payload, "type") == "message"
                    && GetString(payload, "role") == "user":
                    var text = ReadContentText(payload);
                    if (IsRealUserTurn(text))
                        firstUser = text;
                    break;
            }

            if (id is not null && cwd is not null && firstUser is not null)
                break;
        }

        if (id is null)
            return null;

        return new StoredTranscriptInfo
        {
            Tool = Tool,
            SessionId = id,
            Cwd = cwd ?? string.Empty,
            UpdatedAt = file.LastWriteTimeUtc,
            FirstUserMessage = firstUser,
        };
    }

    public async Task<StoredTranscript?> ReadAsync(string sessionId, CancellationToken ct = default)
    {
        var file = FindTranscript(sessionId);
        if (file is null)
            return null;

        var sessionScopedId = TranscriptIds.Derive(nameof(AgentToolKey.CodexCli), sessionId);
        var messages = new List<AgentMessage>();
        // function_call and its function_call_output are separate lines. Rather than emit two
        // messages and collapse them afterwards, keep the call by its id and fill the result in
        // when it arrives — the transcript is ordered, so the call always precedes its output.
        var pending = new Dictionary<string, AgentMessage>(StringComparer.Ordinal);

        string? cwd = null, version = null, model = null, branch = null;
        DateTimeOffset created = default;
        var lineNo = 0;

        using var reader = new StreamReader(file);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (!TryParseLine(line, out var root))
                throw new TranscriptStoreException(
                    $"{file}: line {lineNo} is not valid JSON — the transcript is truncated or corrupt.");
            if (!root.TryGetProperty("payload", out var payload))
                continue;

            switch (GetString(root, "type"))
            {
                case "session_meta":
                    cwd ??= GetString(payload, "cwd");
                    version ??= GetString(payload, "cli_version");
                    if (payload.TryGetProperty("git", out var git) && git.ValueKind == JsonValueKind.Object)
                        branch ??= GetString(git, "branch");
                    if (created == default && TryReadTimestamp(payload, "timestamp", out var metaAt))
                        created = metaAt;
                    break;

                case "turn_context":
                    model ??= GetString(payload, "model");
                    cwd ??= GetString(payload, "cwd");
                    break;

                // event_msg mirrors response_item for the UI's benefit; reading both would double
                // every message.
                case "response_item":
                    var msg = ReadResponseItem(sessionScopedId, payload, root, pending);
                    if (msg is not null)
                        messages.Add(msg);
                    break;
            }
        }

        if (cwd is null)
            throw new TranscriptStoreException(
                $"{file}: no session_meta or turn_context line — not a Codex rollout.");

        return new StoredTranscript
        {
            Tool = Tool,
            SessionId = sessionId,
            Cwd = cwd,
            CreatedAt = created == default ? DateTimeOffset.UtcNow : created,
            Model = model,
            CliVersion = version,
            GitBranch = branch,
            SourcePath = file,
            Messages = messages,
        };
    }

    private AgentMessage? ReadResponseItem(
        Guid sessionScopedId, JsonElement payload, JsonElement root,
        Dictionary<string, AgentMessage> pending)
    {
        var at = TryReadTimestamp(root, "timestamp", out var ts) ? ts : DateTimeOffset.UtcNow;

        switch (GetString(payload, "type"))
        {
            case "message":
            {
                var role = GetString(payload, "role");
                // `developer` and `system` carry the harness's own instructions, not the
                // conversation; they are regenerated by whichever CLI runs next.
                if (role is not ("user" or "assistant"))
                    return null;
                var text = ReadContentText(payload);
                if (string.IsNullOrWhiteSpace(text))
                    return null;
                if (role == "user" && !IsRealUserTurn(text))
                    return null;

                return new AgentMessage
                {
                    Id = TranscriptIds.Derive(sessionScopedId.ToString(), role, text),
                    AgentTaskId = sessionScopedId,
                    Role = role == "user" ? MessageRole.User : MessageRole.Assistant,
                    Type = role == "user" ? MessageType.UserMessage : MessageType.AgentMessage,
                    Content = text,
                    CreatedAt = at,
                };
            }

            case "reasoning":
            {
                // encrypted_content is provider-signed and cannot travel; only a plaintext
                // summary — when the model produced one — survives.
                var summary = ReadArrayText(payload, "summary");
                if (string.IsNullOrWhiteSpace(summary))
                    return null;
                return new AgentMessage
                {
                    Id = TranscriptIds.Derive(sessionScopedId.ToString(), "reasoning", summary),
                    AgentTaskId = sessionScopedId,
                    Role = MessageRole.Assistant,
                    Type = MessageType.Reasoning,
                    Content = summary,
                    CreatedAt = at,
                };
            }

            case "function_call":
            {
                var callId = GetString(payload, "call_id");
                if (callId is null)
                    return null;
                var name = GetString(payload, "name") ?? "unknown";
                var argsJson = GetString(payload, "arguments");
                var message = BuildCallMessage(sessionScopedId, callId, name, argsJson, at);
                pending[callId] = message;
                return message;
            }

            case "function_call_output":
            {
                var callId = GetString(payload, "call_id");
                if (callId is null || !pending.TryGetValue(callId, out var call))
                    return null;          // output with no call — nothing to attach it to
                ApplyCallOutput(call, ReadOutputText(payload));
                return null;              // already in the list; mutated in place
            }

            default:
                return null;
        }
    }

    private static AgentMessage BuildCallMessage(
        Guid sessionScopedId, string callId, string name, string? argsJson, DateTimeOffset at)
    {
        var message = new AgentMessage
        {
            Id = TranscriptIds.Derive(sessionScopedId.ToString(), callId),
            AgentTaskId = sessionScopedId,
            ExternalId = callId,
            Role = MessageRole.Assistant,
            Status = MessageStatus.InProgress,
            CreatedAt = at,
        };

        if (name is ExecCommandTool or "shell" or "local_shell")
        {
            var args = TryParseObject(argsJson);
            var command = args is null ? string.Empty
                : (GetString(args.Value, "cmd") ?? GetString(args.Value, "command") ?? string.Empty);
            message.Type = MessageType.CommandExecution;
            message.CommandExecution = new CommandExecutionData
            {
                Id = TranscriptIds.Derive(callId, "cmd"),
                Command = command,
                Cwd = (args is null ? null : GetString(args.Value, "workdir")) ?? string.Empty,
            };
            return message;
        }

        message.Type = MessageType.ToolCall;
        message.ToolCall = new ToolCallData
        {
            Id = TranscriptIds.Derive(callId, "tool"),
            ToolName = name,
            Arguments = argsJson,
            // Codex names MCP tools mcp__<server>__<tool>; splitting it out means an embedder can
            // tell "the MCP server did this" from "the CLI did this" without re-parsing.
            ServerName = McpServerOf(name),
        };
        return message;
    }

    private static void ApplyCallOutput(AgentMessage call, string output)
    {
        call.Status = MessageStatus.Completed;

        if (call.CommandExecution is { } cmd)
        {
            cmd.Output = output;
            // exec_command wraps stdout in a small header that includes the exit status.
            var m = Regex.Match(output, @"exited with code (-?\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var code))
            {
                cmd.ExitCode = code;
                if (code != 0)
                    call.Status = MessageStatus.Failed;
            }
            return;
        }

        if (call.ToolCall is { } tool)
            tool.Result = output;
    }

    internal static string? McpServerOf(string toolName)
    {
        var parts = toolName.Split("__", StringSplitOptions.None);
        return parts.Length >= 3 && parts[0] == "mcp" ? parts[1] : null;
    }

    /// <summary>
    /// Codex injects synthetic <c>user</c> items carrying environment and permission preambles.
    /// They are regenerated on every launch, so carrying them into another CLI would paste one
    /// agent's sandbox rules into a different agent's conversation.
    /// </summary>
    private static bool IsRealUserTurn(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && !Regex.IsMatch(text.TrimStart(), @"^<[a-z_]+(\s+instructions)?>", RegexOptions.IgnoreCase);

    private string? FindTranscript(string sessionId)
    {
        if (!Directory.Exists(SessionsRoot))
            return null;
        return Directory
            .EnumerateFiles(SessionsRoot, $"rollout-*{sessionId}.jsonl", SearchOption.AllDirectories)
            .FirstOrDefault();
    }

    // ── write ─────────────────────────────────────────────────────────────

    public async Task<string> WriteAsync(
        StoredTranscript transcript, TranscriptWriteOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new TranscriptWriteOptions();
        var now = DateTimeOffset.UtcNow;
        var sessionId = options.SessionId ?? TranscriptIds.NewV7(now).ToString();
        var cwd = options.Cwd ?? transcript.Cwd;
        if (string.IsNullOrWhiteSpace(cwd))
            throw new TranscriptStoreException("A non-empty cwd is required to write a Codex rollout.");

        // Only inherit the source's model/version when the transcript came from THIS store. A
        // transcript converted from another CLI carries that CLI's model name, and stamping e.g.
        // `claude-opus-5` into a Codex rollout makes the resumed session fail outright rather than
        // quietly pick a default.
        var sameStore = transcript.Tool == Tool;
        var model = options.Model ?? (sameStore ? transcript.Model : null) ?? "gpt-5.5";
        var version = options.CliVersion ?? (sameStore ? transcript.CliVersion : null) ?? "0.144.0";
        var stamp = now.UtcDateTime.ToString("yyyy-MM-ddTHH-mm-ss", CultureInfo.InvariantCulture);
        var dir = Path.Combine(SessionsRoot, $"{now:yyyy}", $"{now:MM}", $"{now:dd}");
        var path = Path.Combine(dir, $"rollout-{stamp}-{sessionId}.jsonl");

        var lines = new List<JsonObject>
        {
            Envelope(now, "session_meta", new JsonObject
            {
                ["id"] = sessionId,
                ["timestamp"] = now.ToString("o"),
                ["cwd"] = cwd,
                ["originator"] = "mintokei_agent_transcripts",
                ["cli_version"] = version,
                ["source"] = "exec",
                ["model_provider"] = "openai",
            }),
            Envelope(now, "turn_context", new JsonObject
            {
                ["cwd"] = cwd,
                ["current_date"] = now.ToString("yyyy-MM-dd"),
                ["timezone"] = "Etc/UTC",
                ["approval_policy"] = "on-request",
                ["sandbox_policy"] = new JsonObject { ["type"] = "workspace-write" },
                ["model"] = model,
                ["effort"] = "medium",
                ["summary"] = "none",
            }),
        };

        void Item(JsonObject payload) => lines.Add(Envelope(DateTimeOffset.UtcNow, "response_item", payload));

        void Event(JsonObject payload) => lines.Add(Envelope(DateTimeOffset.UtcNow, "event_msg", payload));

        void Message(string role, string text)
        {
            Item(new JsonObject
            {
                ["type"] = "message",
                ["role"] = role,
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = role == "user" ? "input_text" : "output_text",
                    ["text"] = text,
                }),
            });

            // The same turn again, in Codex's presentation vocabulary. A rollout carries two
            // parallel channels: `response_item` is what the model is given, `event_msg` is what
            // the interface replays. Writing only the first produces a session the agent remembers
            // perfectly and the TUI shows as empty — which reads, to whoever resumes it, exactly
            // like the move having failed.
            //
            // Reading still skips event_msg (it mirrors response_item, so parsing both doubles
            // every message), so a written transcript still round-trips.
            Event(role == "user"
                ? new JsonObject
                {
                    ["type"] = "user_message",
                    ["message"] = text,
                    ["images"] = new JsonArray(),
                    ["local_images"] = new JsonArray(),
                    ["text_elements"] = new JsonArray(),
                }
                : new JsonObject
                {
                    ["type"] = "agent_message",
                    ["message"] = text,
                    ["phase"] = "final_answer",
                    ["memory_citation"] = null,
                });
        }

        void Call(string name, string? argumentsJson, string? output)
        {
            var callId = $"call_{Guid.NewGuid():N}";
            Item(new JsonObject
            {
                ["type"] = "function_call",
                ["name"] = name,
                ["arguments"] = argumentsJson ?? "{}",
                ["call_id"] = callId,
            });
            Item(new JsonObject
            {
                ["type"] = "function_call_output",
                ["call_id"] = callId,
                ["output"] = output ?? string.Empty,
            });
        }

        foreach (var m in transcript.Messages)
        {
            ct.ThrowIfCancellationRequested();
            switch (m.Type)
            {
                case MessageType.UserMessage when !string.IsNullOrWhiteSpace(m.Content):
                    Message("user", m.Content);
                    break;

                case MessageType.CommandExecution when m.CommandExecution is { } cmd:
                    var execArgs = new JsonObject { ["cmd"] = cmd.Command };
                    if (!string.IsNullOrEmpty(cmd.Cwd))
                        execArgs["workdir"] = cmd.Cwd;
                    Call(ExecCommandTool, execArgs.ToJsonString(), TranscriptNarration.WithExitStatus(cmd));
                    break;

                // A question the user answered and a plan are recorded as tool calls too, so the
                // payload decides rather than the kind. Switching on the kind alone sent them to
                // the prose fallback and lost the question.
                case MessageType.ToolCall or MessageType.UserQuestion or MessageType.Plan
                    when m.ToolCall is { } tool:
                    Call(TranscriptNarration.QualifiedToolName(tool), tool.Arguments,
                        tool.Result ?? tool.Error);
                    break;

                default:
                    // Plan, FileChange and friends have no faithful Codex wire form, so they cross
                    // as assistant prose — built from the payload when there is no Content, or a
                    // file edit carrying only a path and a diff went unrecorded entirely.
                    if (TranscriptNarration.DescribeForProse(m) is { } narration)
                        Message("assistant", narration);
                    break;
            }
        }

        if (lines.Count <= 2)
            throw new TranscriptStoreException("Nothing to write — the transcript has no transferable messages.");

        Directory.CreateDirectory(dir);
        await using (var writer = new StreamWriter(path, append: false))
        {
            foreach (var line in lines)
                await writer.WriteLineAsync(line.ToJsonString().AsMemory(), ct);
        }

        if (options.RegisterInIndex)
            RegisterThread(sessionId, path, cwd, transcript, model, version, now);

        _logger?.LogInformation(
            "Wrote Codex rollout {SessionId} ({Lines} lines) to {Path}", sessionId, lines.Count, path);
        return sessionId;
    }

    private static JsonObject Envelope(DateTimeOffset at, string type, JsonObject payload) => new()
    {
        ["timestamp"] = at.ToString("o"),
        ["type"] = type,
        ["payload"] = payload,
    };

    /// <summary>
    /// Adds the session to the <c>threads</c> index so <c>codex resume</c>'s picker lists it.
    /// Best-effort by design: resume-by-id works from the file alone, so a locked or migrated
    /// database must not fail a write that otherwise succeeded.
    /// </summary>
    private void RegisterThread(
        string sessionId, string path, string cwd, StoredTranscript transcript,
        string model, string version, DateTimeOffset now)
    {
        var db = StateDatabase();
        if (db is null)
        {
            _logger?.LogWarning("No state_*.sqlite under {Home}; skipping the thread index", _home);
            return;
        }

        var firstUser = transcript.Messages
            .FirstOrDefault(m => m.Type == MessageType.UserMessage)?.Content ?? string.Empty;
        var title = transcript.Title ?? Shorten(firstUser, 60);

        var row = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = sessionId,
            ["rollout_path"] = path,
            ["created_at"] = now.ToUnixTimeSeconds(),
            ["updated_at"] = now.ToUnixTimeSeconds(),
            ["source"] = "exec",
            ["model_provider"] = "openai",
            ["cwd"] = cwd,
            ["title"] = title,
            ["sandbox_policy"] = """{"type":"workspace-write"}""",
            ["approval_mode"] = "on-request",
            ["tokens_used"] = 0,
            ["has_user_event"] = 1,
            ["archived"] = 0,
            ["cli_version"] = version,
            ["first_user_message"] = Shorten(firstUser, 2000),
            ["preview"] = Shorten(firstUser, 120),
            ["model"] = model,
            ["memory_mode"] = "enabled",
            ["thread_source"] = "user",
            ["created_at_ms"] = now.ToUnixTimeMilliseconds(),
            ["updated_at_ms"] = now.ToUnixTimeMilliseconds(),
            ["recency_at"] = now.ToUnixTimeSeconds(),
            ["recency_at_ms"] = now.ToUnixTimeMilliseconds(),
            ["history_mode"] = "legacy",
        };

        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = db,
                Mode = SqliteOpenMode.ReadWrite,
            }.ToString());
            conn.Open();

            // Codex adds columns as it evolves; write only the ones this database actually has,
            // so a newer or older CLI does not turn an index update into a hard failure.
            var present = new HashSet<string>(StringComparer.Ordinal);
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA table_info(threads)";
                using var r = pragma.ExecuteReader();
                while (r.Read())
                    present.Add(r.GetString(1));
            }
            if (present.Count == 0)
            {
                _logger?.LogWarning("{Db} has no threads table; skipping the thread index", db);
                return;
            }

            var cols = row.Keys.Where(present.Contains).ToList();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"INSERT OR REPLACE INTO threads ({string.Join(",", cols)}) " +
                $"VALUES ({string.Join(",", cols.Select(c => "$" + c))})";
            foreach (var c in cols)
                cmd.Parameters.AddWithValue("$" + c, row[c] ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            _logger?.LogWarning(ex,
                "Could not index Codex thread {SessionId}; resume-by-id still works", sessionId);
        }
    }

    private static string Shorten(string value, int max) =>
        value.Length <= max ? value : value[..max];

    // ── json helpers ──────────────────────────────────────────────────────

    private static string ReadContentText(JsonElement payload)
    {
        if (!payload.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return string.Empty;
        var parts = new List<string>();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object && GetString(block, "text") is { } t)
                parts.Add(t);
        }
        return string.Join('\n', parts).Trim();
    }

    private static string ReadArrayText(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return string.Empty;
        var parts = new List<string>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                parts.Add(item.GetString() ?? string.Empty);
            else if (item.ValueKind == JsonValueKind.Object && GetString(item, "text") is { } t)
                parts.Add(t);
        }
        return string.Join('\n', parts).Trim();
    }

    private static string ReadOutputText(JsonElement payload)
    {
        if (!payload.TryGetProperty("output", out var output))
            return string.Empty;
        return output.ValueKind switch
        {
            JsonValueKind.String => output.GetString() ?? string.Empty,
            JsonValueKind.Object => GetString(output, "output") ?? output.GetRawText(),
            _ => output.GetRawText(),
        };
    }

    private static JsonElement? TryParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryParseLine(string line, out JsonElement root)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            root = doc.RootElement.Clone();
            return root.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            root = default;
            return false;
        }
    }

    private static bool TryReadTimestamp(JsonElement e, string name, out DateTimeOffset value)
    {
        value = default;
        return GetString(e, name) is { } s
            && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out value);
    }

    private static string? GetString(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;
}
