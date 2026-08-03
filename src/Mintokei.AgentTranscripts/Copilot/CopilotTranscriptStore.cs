using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Data.Sqlite;

using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.Contracts;

namespace Mintokei.AgentTranscripts.Copilot;

/// <summary>
/// GitHub Copilot CLI's session store: an event-sourced transcript per session at
/// <c>&lt;home&gt;/session-state/&lt;id&gt;/events.jsonl</c>, alongside a <c>workspace.yaml</c> and
/// working directories the CLI expects to exist, plus rows in <c>&lt;home&gt;/session-store.db</c>.
/// <c>home</c> is <c>COPILOT_HOME</c> or <c>~/.copilot</c>.
///
/// The strictest of the stores to write. Copilot validates every event envelope on load and refuses
/// the whole session on the first bad one — <c>id</c> must be a UUID, turn events must carry
/// <c>turnId</c> — so this writes complete envelopes rather than the minimum that looks right.
///
/// None of the engine's Copilot parsing is reusable here: Mintokei drives Copilot over ACP, so
/// <c>AcpSessionUpdateParser</c> speaks <c>session/update</c> notifications, while the store speaks
/// Copilot's own <c>assistant.message</c> / <c>tool.execution_*</c> vocabulary.
/// </summary>
public sealed class CopilotTranscriptStore : ITranscriptStore
{
    private const string EventsFile = "events.jsonl";
    private const string WorkspaceFile = "workspace.yaml";
    private const string IndexFile = "session-store.db";

    private readonly string _home;
    private readonly ILogger? _logger;

    /// <param name="home">Copilot home directory. Null resolves <c>COPILOT_HOME</c>, then <c>~/.copilot</c>.</param>
    /// <param name="logger">Optional diagnostics.</param>
    public CopilotTranscriptStore(string? home = null, ILogger? logger = null)
    {
        _home = home
            ?? Environment.GetEnvironmentVariable("COPILOT_HOME")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot");
        _logger = logger;
    }

    public AgentToolKey Tool => AgentToolKey.GithubCopilotCli;

    private string SessionStateRoot => Path.Combine(_home, "session-state");
    private string IndexPath => Path.Combine(_home, IndexFile);
    private string EventsPathFor(string sessionId) =>
        Path.Combine(SessionStateRoot, sessionId, EventsFile);

    // ── read ──────────────────────────────────────────────────────────────

    public async IAsyncEnumerable<StoredTranscriptInfo> ListAsync(
        string? cwd = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
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

        if (!Directory.Exists(SessionStateRoot))
            yield break;

        foreach (var dir in Directory.EnumerateDirectories(SessionStateRoot)
                     .OrderByDescending(d => new DirectoryInfo(d).LastWriteTimeUtc))
        {
            ct.ThrowIfCancellationRequested();
            var events = Path.Combine(dir, EventsFile);
            if (!File.Exists(events))
                continue;      // a session that never completed a turn has no transcript at all

            var info = await ReadHeaderAsync(new DirectoryInfo(dir), ct);
            if (info is null)
                continue;
            if (cwd is not null && !string.Equals(info.Cwd, cwd, StringComparison.Ordinal))
                continue;
            yield return info;
        }
    }

    private async Task<StoredTranscriptInfo?> ReadHeaderAsync(DirectoryInfo dir, CancellationToken ct)
    {
        string? cwd = null, firstUser = null;
        var scanned = 0;

        using var reader = new StreamReader(Path.Combine(dir.FullName, EventsFile));
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (++scanned > 200)
                break;
            if (!TryParseLine(line, out var root))
                continue;
            var data = root.TryGetProperty("data", out var d) ? d : default;

            switch (GetString(root, "type"))
            {
                case "session.start" or "session.resume":
                    if (data.ValueKind == JsonValueKind.Object
                        && data.TryGetProperty("context", out var context))
                    {
                        cwd ??= GetString(context, "cwd");
                    }
                    break;
                case "user.message" when firstUser is null:
                    firstUser = GetString(data, "content");
                    break;
            }

            if (cwd is not null && firstUser is not null)
                break;
        }

        return new StoredTranscriptInfo
        {
            Tool = Tool,
            SessionId = dir.Name,
            Cwd = cwd ?? string.Empty,
            UpdatedAt = dir.LastWriteTimeUtc,
            FirstUserMessage = firstUser,
        };
    }

    /// <summary>
    /// Reads the <c>sessions</c> index, or null when there is nothing usable to read. Null means
    /// "fall back to scanning", never "no sessions".
    /// </summary>
    private List<StoredTranscriptInfo>? ListFromIndex(string? cwd)
    {
        if (!File.Exists(IndexPath))
            return null;

        try
        {
            using var conn = OpenIndex(SqliteOpenMode.ReadOnly);
            var columns = ColumnsOf(conn, "sessions");
            if (!columns.Contains("id"))
                return null;

            var summary = columns.Contains("summary") ? "summary" : "''";
            var updated = columns.Contains("updated_at") ? "updated_at" : "''";

            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"SELECT s.id, s.cwd, {summary}, {updated}, "
                + "(SELECT t.user_message FROM turns t WHERE t.session_id = s.id "
                + " ORDER BY t.turn_index LIMIT 1) "
                + "FROM sessions s" + (cwd is null ? "" : " WHERE s.cwd = $cwd")
                + $" ORDER BY {updated} DESC";
            if (cwd is not null)
                cmd.Parameters.AddWithValue("$cwd", cwd);

            var results = new List<StoredTranscriptInfo>();
            using var rows = cmd.ExecuteReader();
            while (rows.Read())
            {
                var id = rows.GetString(0);
                // The index outlives the transcript: Copilot records a session before it has any
                // turns. Listing one with no events.jsonl would offer the user a session that
                // cannot be read.
                if (!File.Exists(EventsPathFor(id)))
                    continue;

                results.Add(new StoredTranscriptInfo
                {
                    Tool = Tool,
                    SessionId = id,
                    Cwd = Text(rows, 1) ?? string.Empty,
                    Title = Text(rows, 2),
                    UpdatedAt = ParseTimestamp(Text(rows, 3)),
                    FirstUserMessage = Text(rows, 4),
                });
            }
            return results;
        }
        catch (SqliteException ex)
        {
            _logger?.LogDebug(ex, "Could not read the Copilot session index; falling back to a scan");
            return null;
        }
    }

    public async Task<StoredTranscript?> ReadAsync(string sessionId, CancellationToken ct = default)
    {
        var path = EventsPathFor(sessionId);
        if (!File.Exists(path))
            return null;

        var sessionScopedId = TranscriptIds.Derive(nameof(AgentToolKey.GithubCopilotCli), sessionId);
        var messages = new List<AgentMessage>();
        // tool.execution_start and tool.execution_complete arrive as separate events, so the call is
        // held by its id and its result filled in when it lands.
        var pending = new Dictionary<string, AgentMessage>(StringComparer.Ordinal);

        string? cwd = null, version = null, model = null;
        DateTimeOffset created = default;
        var lineNo = 0;

        using var reader = new StreamReader(path);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (!TryParseLine(line, out var root))
                throw new TranscriptStoreException(
                    $"{path}: line {lineNo} is not valid JSON — the transcript is truncated or corrupt.");

            var data = root.TryGetProperty("data", out var d) ? d : default;
            var at = TryReadTimestamp(root, "timestamp", out var ts) ? ts : DateTimeOffset.UtcNow;

            switch (GetString(root, "type"))
            {
                case "session.start" or "session.resume":
                    if (data.ValueKind == JsonValueKind.Object
                        && data.TryGetProperty("context", out var context))
                    {
                        cwd ??= GetString(context, "cwd");
                    }
                    version ??= GetString(data, "copilotVersion");
                    if (created == default)
                        created = TryReadTimestamp(data, "startTime", out var start) ? start : at;
                    break;

                case "session.model_change":
                    model = GetString(data, "newModel") ?? model;
                    break;

                case "session.auto_mode_resolved":
                    model = GetString(data, "chosenModel") ?? model;
                    break;

                // system.message is the harness's own prompt, regenerated on every launch.
                case "user.message":
                {
                    // `content` is what the human typed; `transformedContent` is the same text with
                    // datestamps and system reminders wrapped around it for the model.
                    var text = GetString(data, "content");
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        messages.Add(new AgentMessage
                        {
                            Id = TranscriptIds.Derive(sessionScopedId.ToString(), GetString(root, "id")),
                            AgentTaskId = sessionScopedId,
                            ExternalId = GetString(root, "id"),
                            Role = MessageRole.User,
                            Type = MessageType.UserMessage,
                            Content = text,
                            CreatedAt = at,
                        });
                    }
                    break;
                }

                case "assistant.message":
                {
                    model ??= GetString(data, "model");
                    var text = GetString(data, "content");
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        messages.Add(new AgentMessage
                        {
                            Id = TranscriptIds.Derive(sessionScopedId.ToString(), GetString(data, "messageId")),
                            AgentTaskId = sessionScopedId,
                            ExternalId = GetString(data, "messageId"),
                            Role = MessageRole.Assistant,
                            Type = MessageType.AgentMessage,
                            Content = text,
                            CreatedAt = at,
                        });
                    }
                    // reasoningOpaque / encryptedContent are provider-signed and cannot travel.

                    if (data.ValueKind == JsonValueKind.Object
                        && data.TryGetProperty("toolRequests", out var requests)
                        && requests.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var request in requests.EnumerateArray())
                        {
                            var call = BuildCall(sessionScopedId, request, at);
                            if (call is null)
                                continue;
                            messages.Add(call);
                            if (call.ExternalId is { } id)
                                pending[id] = call;
                        }
                    }
                    break;
                }

                case "tool.execution_start":
                {
                    // Belt and braces: a transcript whose assistant.message did not carry the
                    // request (older Copilot builds) still yields the call.
                    var callId = GetString(data, "toolCallId");
                    if (callId is null || pending.ContainsKey(callId))
                        break;
                    var call = BuildCall(sessionScopedId, data, at, nameKey: "toolName");
                    if (call is null)
                        break;
                    messages.Add(call);
                    pending[callId] = call;
                    break;
                }

                case "tool.execution_complete":
                {
                    var callId = GetString(data, "toolCallId");
                    if (callId is null || !pending.TryGetValue(callId, out var call))
                        break;
                    var success = !data.TryGetProperty("success", out var ok)
                        || ok.ValueKind != JsonValueKind.False;
                    ApplyResult(call, ReadResultContent(data), success);
                    break;
                }
            }
        }

        return new StoredTranscript
        {
            Tool = Tool,
            SessionId = sessionId,
            Cwd = cwd ?? string.Empty,
            CreatedAt = created == default ? DateTimeOffset.UtcNow : created,
            Model = model,
            CliVersion = version,
            SourcePath = path,
            Messages = messages,
        };
    }

    private static AgentMessage? BuildCall(
        Guid sessionScopedId, JsonElement source, DateTimeOffset at, string nameKey = "name")
    {
        var callId = GetString(source, "toolCallId");
        if (callId is null)
            return null;
        var name = GetString(source, nameKey) ?? "unknown";
        var argumentsJson = source.ValueKind == JsonValueKind.Object
            && source.TryGetProperty("arguments", out var args)
                ? args.GetRawText()
                : null;

        var message = new AgentMessage
        {
            Id = TranscriptIds.Derive(sessionScopedId.ToString(), callId),
            AgentTaskId = sessionScopedId,
            ExternalId = callId,
            Role = MessageRole.Assistant,
            Status = MessageStatus.InProgress,
            CreatedAt = at,
        };

        if (name is "bash" or "shell")
        {
            var parsed = TryParseObject(argumentsJson);
            message.Type = MessageType.CommandExecution;
            message.CommandExecution = new CommandExecutionData
            {
                Id = TranscriptIds.Derive(callId, "cmd"),
                Command = parsed is null ? string.Empty : GetString(parsed.Value, "command") ?? string.Empty,
                Cwd = string.Empty,
            };
            return message;
        }

        message.Type = MessageType.ToolCall;
        message.ToolCall = new ToolCallData
        {
            Id = TranscriptIds.Derive(callId, "tool"),
            ToolName = name,
            Arguments = argumentsJson,
            // Same convention the Codex store reads: mcp__<server>__<tool>. Splitting the server
            // out means a consumer can tell "an MCP server did this" from "the CLI did this".
            ServerName = TranscriptNarration.SplitToolName(name).ServerName,
        };
        return message;
    }

    private static void ApplyResult(AgentMessage call, string? output, bool success)
    {
        call.Status = success ? MessageStatus.Completed : MessageStatus.Failed;

        if (call.CommandExecution is { } cmd)
        {
            cmd.Output = output;
            // Copilot has no exit-code field; its bash tool prints one into the output header.
            // Where that header is absent, `success` is the only signal there is.
            var m = System.Text.RegularExpressions.Regex.Match(output ?? "", @"exited with code (-?\d+)");
            cmd.ExitCode = m.Success && int.TryParse(m.Groups[1].Value, out var code)
                ? code
                : success ? 0 : 1;
            return;
        }

        if (call.ToolCall is not { } tool)
            return;
        if (success)
            tool.Result = output;
        else
            tool.Error = output;
    }

    /// <summary>
    /// The result is a JSON <em>string</em> holding an object with <c>content</c> and often a much
    /// larger <c>detailedContent</c>. Only <c>content</c> is carried: the detailed form is a full
    /// diff or file dump, which would dominate a converted transcript.
    /// </summary>
    private static string? ReadResultContent(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("result", out var result))
            return null;

        if (result.ValueKind == JsonValueKind.Object)
            return GetString(result, "content") ?? result.GetRawText();

        if (result.ValueKind != JsonValueKind.String)
            return result.GetRawText();

        var raw = result.GetString();
        if (string.IsNullOrWhiteSpace(raw))
            return raw;
        var parsed = TryParseObject(raw);
        return parsed is null ? raw : GetString(parsed.Value, "content") ?? raw;
    }

    // ── write ─────────────────────────────────────────────────────────────

    public async Task<string> WriteAsync(
        StoredTranscript transcript, TranscriptWriteOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new TranscriptWriteOptions();
        var sessionId = options.SessionId ?? Guid.NewGuid().ToString();
        var cwd = options.Cwd ?? transcript.Cwd;
        if (string.IsNullOrWhiteSpace(cwd))
            throw new TranscriptStoreException("A non-empty cwd is required to write a Copilot session.");

        var sameStore = transcript.Tool == Tool;
        var model = options.Model ?? (sameStore ? transcript.Model : null) ?? "claude-sonnet-4-5";
        var version = options.CliVersion ?? (sameStore ? transcript.CliVersion : null) ?? "1.0.0";
        var now = DateTimeOffset.UtcNow;

        var events = new List<JsonObject>();
        string? parentId = null;

        string Emit(string type, JsonObject data)
        {
            var id = Guid.NewGuid().ToString();
            events.Add(new JsonObject
            {
                ["type"] = type,
                ["data"] = data,
                // Copilot rejects the whole session if an envelope id is not a UUID, so these are
                // never synthesised from anything else.
                ["id"] = id,
                ["timestamp"] = Stamp(now),
                ["parentId"] = parentId,
            });
            parentId = id;
            return id;
        }

        Emit("session.start", new JsonObject
        {
            ["sessionId"] = sessionId,
            ["version"] = 1,
            ["producer"] = "copilot-agent",
            ["copilotVersion"] = version,
            ["startTime"] = Stamp(now),
            ["contextTier"] = null,
            ["context"] = new JsonObject { ["cwd"] = cwd },
            ["alreadyInUse"] = false,
            ["remoteSteerable"] = false,
        });

        var turnIndex = 0;
        var dbTurns = new List<(int Index, string User, string Assistant)>();
        var openTurn = false;
        string interactionId = Guid.NewGuid().ToString();
        var userText = string.Empty;
        var assistantParts = new List<string>();

        void CloseTurn()
        {
            // A user turn the agent never answered still belongs in the index — otherwise the last
            // thing the human said is missing from every session listing, which is exactly the line
            // a picker shows.
            if (!openTurn && string.IsNullOrEmpty(userText))
                return;

            if (openTurn)
            {
                Emit("assistant.turn_end", new JsonObject
                {
                    ["turnId"] = turnIndex.ToString(CultureInfo.InvariantCulture),
                    ["model"] = model,
                });
            }
            dbTurns.Add((turnIndex, userText, string.Join("\n\n", assistantParts)));
            assistantParts.Clear();
            userText = string.Empty;
            turnIndex++;
            openTurn = false;
        }

        void OpenTurn()
        {
            if (openTurn)
                return;
            interactionId = Guid.NewGuid().ToString();
            Emit("assistant.turn_start", new JsonObject
            {
                ["turnId"] = turnIndex.ToString(CultureInfo.InvariantCulture),
                ["model"] = model,
                ["interactionId"] = interactionId,
            });
            openTurn = true;
        }

        void EmitToolExchange(string toolName, string? argumentsJson, string? output, bool success)
        {
            OpenTurn();
            var callId = $"call_{Guid.NewGuid():N}";
            JsonNode arguments;
            try
            {
                arguments = argumentsJson is null ? new JsonObject() : JsonNode.Parse(argumentsJson) ?? new JsonObject();
            }
            catch (JsonException)
            {
                arguments = new JsonObject { ["raw"] = argumentsJson };
            }

            Emit("assistant.message", new JsonObject
            {
                ["messageId"] = Guid.NewGuid().ToString(),
                ["model"] = model,
                ["content"] = string.Empty,
                ["toolRequests"] = new JsonArray(new JsonObject
                {
                    ["toolCallId"] = callId,
                    ["name"] = toolName,
                    ["arguments"] = arguments.DeepClone(),
                    ["type"] = "function",
                    ["intentionSummary"] = toolName,
                }),
                ["interactionId"] = interactionId,
                ["turnId"] = turnIndex.ToString(CultureInfo.InvariantCulture),
            });
            Emit("tool.execution_start", new JsonObject
            {
                ["toolCallId"] = callId,
                ["toolName"] = toolName,
                ["arguments"] = arguments.DeepClone(),
                ["model"] = model,
                ["turnId"] = turnIndex.ToString(CultureInfo.InvariantCulture),
            });
            Emit("tool.execution_complete", new JsonObject
            {
                ["toolCallId"] = callId,
                ["model"] = model,
                ["interactionId"] = interactionId,
                ["turnId"] = turnIndex.ToString(CultureInfo.InvariantCulture),
                ["success"] = success,
                // An object, not a JSON string. Copilot deserialises this into a struct and rejects
                // the whole session when it is the wrong shape.
                ["result"] = new JsonObject { ["content"] = output ?? string.Empty },
            });
        }

        void EmitAssistantText(string text)
        {
            OpenTurn();
            assistantParts.Add(text);
            Emit("assistant.message", new JsonObject
            {
                ["messageId"] = Guid.NewGuid().ToString(),
                ["model"] = model,
                ["content"] = text,
                ["toolRequests"] = new JsonArray(),
                ["interactionId"] = interactionId,
                ["turnId"] = turnIndex.ToString(CultureInfo.InvariantCulture),
            });
        }

        foreach (var m in transcript.Messages)
        {
            ct.ThrowIfCancellationRequested();
            switch (m.Type)
            {
                case MessageType.UserMessage when !string.IsNullOrWhiteSpace(m.Content):
                    CloseTurn();
                    userText = m.Content;
                    Emit("user.message", new JsonObject
                    {
                        ["content"] = m.Content,
                        ["transformedContent"] = m.Content,
                        ["attachments"] = new JsonArray(),
                        ["supportedNativeDocumentMimeTypes"] = new JsonArray(),
                    });
                    break;

                case MessageType.CommandExecution when m.CommandExecution is { } cmd:
                    EmitToolExchange(
                        "bash",
                        new JsonObject { ["command"] = cmd.Command, ["description"] = string.Empty }.ToJsonString(),
                        TranscriptNarration.WithExitStatus(cmd),
                        cmd.ExitCode is null or 0);
                    break;

                // A question the user answered and a plan are recorded as tool calls too, so the
                // payload decides rather than the kind. Switching on the kind alone sent them to
                // the prose fallback and lost the question.
                case MessageType.ToolCall or MessageType.UserQuestion or MessageType.Plan
                    when m.ToolCall is { } tool:
                    EmitToolExchange(
                        TranscriptNarration.QualifiedToolName(tool), tool.Arguments,
                        tool.Result ?? tool.Error, string.IsNullOrEmpty(tool.Error));
                    break;

                default:
                    // Built from the payload when there is no Content: a file edit carries its path
                    // and diff and no prose, and was previously dropped without a word.
                    if (TranscriptNarration.DescribeForProse(m) is { } narration)
                        EmitAssistantText(narration);
                    break;
            }
        }

        CloseTurn();

        if (events.Count <= 1)
            throw new TranscriptStoreException("Nothing to write — the transcript has no transferable messages.");

        var dir = Path.Combine(SessionStateRoot, sessionId);
        // Copilot expects these beside the transcript; it creates them itself on a fresh session.
        foreach (var sub in new[] { "checkpoints", "files", "research" })
            Directory.CreateDirectory(Path.Combine(dir, sub));

        await File.WriteAllTextAsync(
            Path.Combine(dir, WorkspaceFile),
            $"id: {sessionId}\ncwd: {cwd}\nclient_name: cli\nuser_named: false\n"
            + $"summary_count: 0\ncreated_at: {Stamp(now)}\nupdated_at: {Stamp(now)}\n",
            ct);

        await using (var writer = new StreamWriter(Path.Combine(dir, EventsFile), append: false))
        {
            foreach (var e in events)
                await writer.WriteLineAsync(e.ToJsonString().AsMemory(), ct);
        }

        if (options.RegisterInIndex)
            RegisterSession(sessionId, cwd, transcript, dbTurns, now);

        _logger?.LogInformation(
            "Wrote Copilot session {SessionId} ({Events} events) to {Path}", sessionId, events.Count, dir);
        return sessionId;
    }

    /// <summary>
    /// Adds rows to <c>session-store.db</c>. Best-effort: <c>copilot --resume &lt;id&gt;</c> reads the
    /// transcript, so a locked or migrated database must not fail a write that already succeeded.
    /// </summary>
    private void RegisterSession(
        string sessionId, string cwd, StoredTranscript transcript,
        List<(int Index, string User, string Assistant)> turns, DateTimeOffset now)
    {
        if (!File.Exists(IndexPath))
        {
            _logger?.LogWarning("{Path} does not exist; skipping the session index", IndexPath);
            return;
        }

        try
        {
            using var conn = OpenIndex(SqliteOpenMode.ReadWrite);
            var sessionColumns = ColumnsOf(conn, "sessions");
            if (sessionColumns.Count == 0)
            {
                _logger?.LogWarning("{Path} has no sessions table; skipping the index", IndexPath);
                return;
            }

            var stamp = now.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            var row = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = sessionId,
                ["cwd"] = cwd,
                ["summary"] = transcript.Title ?? Shorten(FirstUserText(transcript), 120),
                ["created_at"] = stamp,
                ["updated_at"] = stamp,
            };
            var cols = row.Keys.Where(sessionColumns.Contains).ToList();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    $"INSERT OR REPLACE INTO sessions ({string.Join(",", cols)}) "
                    + $"VALUES ({string.Join(",", cols.Select(c => "$" + c))})";
                foreach (var c in cols)
                    cmd.Parameters.AddWithValue("$" + c, row[c] ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }

            if (ColumnsOf(conn, "turns").Count == 0)
                return;

            foreach (var (index, user, assistant) in turns)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "INSERT OR REPLACE INTO turns (session_id, turn_index, user_message, assistant_response, timestamp) "
                    + "VALUES ($s, $i, $u, $a, $t)";
                cmd.Parameters.AddWithValue("$s", sessionId);
                cmd.Parameters.AddWithValue("$i", index);
                cmd.Parameters.AddWithValue("$u", (object?)user ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$a", (object?)assistant ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$t", stamp);
                cmd.ExecuteNonQuery();
            }
        }
        catch (SqliteException ex)
        {
            _logger?.LogWarning(ex,
                "Could not index Copilot session {SessionId}; the transcript is still resumable", sessionId);
        }
    }

    private static string FirstUserText(StoredTranscript t) =>
        t.Messages.FirstOrDefault(m => m.Type == MessageType.UserMessage)?.Content ?? string.Empty;

    private static string Shorten(string value, int max) =>
        value.Length <= max ? value : value[..max];

    // ── helpers ───────────────────────────────────────────────────────────

    private SqliteConnection OpenIndex(SqliteOpenMode mode)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = IndexPath,
            Mode = mode,
        }.ToString());
        conn.Open();
        return conn;
    }

    private static HashSet<string> ColumnsOf(SqliteConnection conn, string table)
    {
        var columns = new HashSet<string>(StringComparer.Ordinal);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            columns.Add(reader.GetString(1));
        return columns;
    }

    /// <summary>
    /// Copilot writes milliseconds and a literal Z (<c>2026-07-27T06:56:49.522Z</c>). Round-trip
    /// "o" formatting produces seven fractional digits and a numeric offset, which its YAML loader
    /// rejects outright — and it fails silently, logging to file rather than stderr.
    /// </summary>
    private static string Stamp(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : default;

    private static string? Text(SqliteDataReader r, int i)
    {
        if (r.IsDBNull(i))
            return null;
        var value = r.GetString(i);
        return string.IsNullOrWhiteSpace(value) ? null : value;
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
