using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.Claude;
using Mintokei.AgentEngine.Contracts;

namespace Mintokei.AgentTranscripts.Claude;

/// <summary>
/// Claude Code's session store: one JSON-lines transcript per session under
/// <c>&lt;home&gt;/projects/&lt;cwd-slug&gt;/&lt;session-id&gt;.jsonl</c>, where <c>home</c> is
/// <c>CLAUDE_CONFIG_DIR</c> or <c>~/.claude</c>.
///
/// There is no separate index to maintain: the file IS the session, and <c>claude --resume &lt;id&gt;</c>
/// finds it by scanning. That makes Claude the simplest of the stores to write.
///
/// Reading reuses <see cref="ClaudeCodeOutputParser"/> — the same code that parses live
/// stream-json — because the transcript's <c>user</c>/<c>assistant</c> lines carry the identical
/// <c>message</c> envelope the stream does. The file adds line types the stream never emits
/// (<c>attachment</c>, <c>ai-title</c>, <c>file-history-snapshot</c>, …); those are skipped here
/// rather than pushed through the parser.
/// </summary>
public sealed partial class ClaudeTranscriptStore : ITranscriptStore
{
    private readonly string _home;
    private readonly ILogger? _logger;

    /// <param name="home">Claude config directory. Null resolves <c>CLAUDE_CONFIG_DIR</c>, then <c>~/.claude</c>.</param>
    /// <param name="logger">Optional; the reused parser logs frames it cannot make sense of.</param>
    public ClaudeTranscriptStore(string? home = null, ILogger? logger = null)
    {
        _home = home
            ?? Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        _logger = logger;
    }

    public AgentToolKey Tool => AgentToolKey.ClaudeCodeCli;

    private string ProjectsRoot => Path.Combine(_home, "projects");

    /// <summary>
    /// Claude Code flattens the working directory into a single directory name by replacing every
    /// non-alphanumeric character with '-'. <c>/tmp/my.app</c> becomes <c>-tmp-my-app</c>.
    /// </summary>
    public static string SlugFor(string cwd) => NonAlphanumeric().Replace(cwd, "-");

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NonAlphanumeric();

    /// <summary>
    /// Claude Code writes a synthetic user turn when a turn is cut short — a Ctrl-C, a SIGTERM, a
    /// crash. It is the CLI narrating its own interruption, not something a human asked for, so
    /// reading it as a request makes a handoff say "Outstanding request: [Request interrupted by
    /// user]". Matched narrowly, because a genuine message may well start with '['.
    /// </summary>
    [GeneratedRegex(@"^\[Request interrupted[^\]]*\]$", RegexOptions.IgnoreCase)]
    private static partial Regex InterruptMarker();

    // ── read ──────────────────────────────────────────────────────────────

    public async IAsyncEnumerable<StoredTranscriptInfo> ListAsync(
        string? cwd = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!Directory.Exists(ProjectsRoot))
            yield break;

        // The project directory name IS the flattened cwd, so a cwd filter can go straight to the
        // one directory instead of reading a header out of every transcript on the machine and
        // discarding all but a handful. Worth the special case: an interactive picker asks this
        // question on every keystroke-free startup, and users accumulate hundreds of sessions.
        var root = cwd is null ? ProjectsRoot : Path.Combine(ProjectsRoot, SlugFor(cwd));
        if (!Directory.Exists(root))
            yield break;

        var files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
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

    /// <summary>Reads only as far as the first user turn — a long transcript is megabytes.</summary>
    private async Task<StoredTranscriptInfo?> ReadHeaderAsync(FileInfo file, CancellationToken ct)
    {
        string? cwd = null, title = null, firstUser = null;
        var scanned = 0;

        using var reader = new StreamReader(file.FullName);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (++scanned > 200)
                break;
            if (!TryParseLine(line, out var root))
                continue;

            var type = GetString(root, "type");
            cwd ??= GetString(root, "cwd");
            if (type == "ai-title")
                title ??= GetString(root, "aiTitle");

            if (firstUser is null && type == "user"
                && !GetBool(root, "isMeta") && !GetBool(root, "isSidechain")
                && root.TryGetProperty("message", out var msg)
                && msg.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String)
            {
                firstUser = content.GetString();
            }

            if (cwd is not null && firstUser is not null && title is not null)
                break;
        }

        if (cwd is null)
            return null;   // not a Claude transcript

        return new StoredTranscriptInfo
        {
            Tool = Tool,
            SessionId = Path.GetFileNameWithoutExtension(file.Name),
            Cwd = cwd,
            UpdatedAt = file.LastWriteTimeUtc,
            Title = title,
            FirstUserMessage = firstUser,
        };
    }

    public async Task<StoredTranscript?> ReadAsync(string sessionId, CancellationToken ct = default)
    {
        var file = FindSessionFile(sessionId);
        if (file is null)
            return null;

        // The parser correlates tool_use blocks with the tool_result that follows them, so the
        // registry has to live across the whole file rather than per line.
        var registry = new Dictionary<string, ClaudeCodeOutputParser.ToolUseInfo>();
        var messages = new List<AgentMessage>();
        var sessionScopedId = TranscriptIds.Derive(nameof(AgentToolKey.ClaudeCodeCli), sessionId);

        string? cwd = null, version = null, branch = null, title = null, model = null;
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

            var type = GetString(root, "type");
            if (type == "ai-title")
            {
                title ??= GetString(root, "aiTitle");
                continue;
            }
            if (type is not ("user" or "assistant"))
                continue;                                  // file-only line kinds
            if (GetBool(root, "isSidechain"))
                continue;                                  // sub-agent transcript, not this conversation

            cwd ??= GetString(root, "cwd");
            version ??= GetString(root, "version");
            branch ??= GetString(root, "gitBranch");
            if (created == default && GetString(root, "timestamp") is { } ts
                && DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var parsed))
            {
                created = parsed;
            }

            if (type == "assistant")
            {
                if (root.TryGetProperty("message", out var m) && GetString(m, "model") is { } mo)
                    model ??= mo;
                messages.AddRange(
                    ClaudeCodeOutputParser.ParseAssistantEvent(sessionScopedId, root, registry, _logger));
            }
            else
            {
                if (GetBool(root, "isMeta"))
                    continue;                              // harness-injected, not the user

                // ClaudeCodeOutputParser.ParseUserEvent only reads tool_result blocks, because in
                // a LIVE stream the host already knows the user's turn — it sent it — so the CLI
                // never echoes it back. A transcript is the other way round: the human turns are
                // the whole point. So the store handles those itself and delegates the rest.
                if (TryReadUserText(root, out var userText))
                    messages.Add(UserMessage(sessionScopedId, root, userText));
                else
                    messages.AddRange(
                        ClaudeCodeOutputParser.ParseUserEvent(sessionScopedId, root, registry, _logger));
            }
        }

        // A tool call arrives as two frames — tool_use (InProgress) then tool_result (Completed) —
        // which the live sink upserts into one row by ExternalId. Reading a file has no sink, so
        // collapse here or every tool call shows up twice.
        messages = CollapseByExternalId(messages);
        // Claude records failure as a boolean, so a numeric exit status only exists if the tool
        // printed one. Recovering it here matches what the Codex and Copilot stores do, and is
        // what makes a failed command survive a crossing as something a consumer can check rather
        // than a word buried in the output.
        foreach (var m in messages)
        {
            // Likewise the MCP server. Claude names those tools mcp__<server>__<tool>, and the
            // stream parser has no reason to split it — the live host already knows which server
            // it wired up. A transcript reader does not.
            if (m.ToolCall is { ServerName: null } tool)
                tool.ServerName = TranscriptNarration.SplitToolName(tool.ToolName).ServerName;

            if (m.CommandExecution is { ExitCode: null, Output: { } text } cmd
                && System.Text.RegularExpressions.Regex.Match(text, @"exited with code (-?\d+)") is { Success: true } hit
                && int.TryParse(hit.Groups[1].Value, out var code))
            {
                cmd.ExitCode = code;
            }
        }

        if (cwd is null)
            throw new TranscriptStoreException($"{file}: no cwd on any line — not a Claude Code transcript.");

        return new StoredTranscript
        {
            Tool = Tool,
            SessionId = sessionId,
            Cwd = cwd,
            CreatedAt = created == default ? DateTimeOffset.UtcNow : created,
            Model = model,
            Title = title,
            CliVersion = version,
            GitBranch = branch,
            SourcePath = file,
            Messages = messages,
        };
    }

    /// <summary>
    /// True when this <c>user</c> line is a human turn rather than a tool-result carrier.
    /// Claude writes plain turns as a bare string and, for attachments, as an array of
    /// <c>text</c> blocks; tool results are an array of <c>tool_result</c> blocks.
    /// </summary>
    private static bool TryReadUserText(JsonElement root, out string text)
    {
        text = string.Empty;
        if (!root.TryGetProperty("message", out var msg)
            || !msg.TryGetProperty("content", out var content))
        {
            return false;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            text = content.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(text) && !InterruptMarker().IsMatch(text.Trim());
        }

        if (content.ValueKind != JsonValueKind.Array)
            return false;

        var parts = new List<string>();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object)
                continue;
            if (GetString(block, "type") == "tool_result")
                return false;                              // let the engine parser own these
            if (GetString(block, "type") == "text" && GetString(block, "text") is { } t)
                parts.Add(t);
        }

        text = string.Join('\n', parts);
        return !string.IsNullOrWhiteSpace(text) && !InterruptMarker().IsMatch(text.Trim());
    }

    private static AgentMessage UserMessage(Guid sessionScopedId, JsonElement root, string text)
    {
        var externalId = GetString(root, "uuid");
        return new AgentMessage
        {
            Id = TranscriptIds.Derive(sessionScopedId.ToString(), externalId ?? text),
            AgentTaskId = sessionScopedId,
            ExternalId = externalId,
            Role = MessageRole.User,
            Type = MessageType.UserMessage,
            Content = text,
            CreatedAt = GetString(root, "timestamp") is { } ts
                && DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var at)
                ? at
                : DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Keeps the last version of each externally-correlated message, in first-appearance order.
    /// Messages without an <c>ExternalId</c> are never merged — two assistant paragraphs are two
    /// messages, not one overwritten twice.
    /// </summary>
    private static List<AgentMessage> CollapseByExternalId(List<AgentMessage> messages)
    {
        var slotOf = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<AgentMessage>(messages.Count);

        foreach (var m in messages)
        {
            if (string.IsNullOrEmpty(m.ExternalId))
            {
                result.Add(m);
                continue;
            }
            if (slotOf.TryGetValue(m.ExternalId, out var slot))
                result[slot] = m;                          // the later frame is the settled one
            else
            {
                slotOf[m.ExternalId] = result.Count;
                result.Add(m);
            }
        }

        return result;
    }

    private string? FindSessionFile(string sessionId)
    {
        if (!Directory.Exists(ProjectsRoot))
            return null;
        // The session could be filed under any project directory, and the caller may not know
        // which cwd it belonged to, so search rather than compute the path.
        return Directory
            .EnumerateFiles(ProjectsRoot, $"{sessionId}.jsonl", SearchOption.AllDirectories)
            .FirstOrDefault();
    }

    // ── write ─────────────────────────────────────────────────────────────

    public async Task<string> WriteAsync(
        StoredTranscript session, TranscriptWriteOptions? options = null, CancellationToken ct = default)
    {
        options ??= new TranscriptWriteOptions();
        var sessionId = options.SessionId ?? Guid.NewGuid().ToString();
        var cwd = options.Cwd ?? session.Cwd;
        if (string.IsNullOrWhiteSpace(cwd))
            throw new TranscriptStoreException(
                "Claude Code files sessions by working directory, so a non-empty cwd is required.");

        var dir = Path.Combine(ProjectsRoot, SlugFor(cwd));
        var path = Path.Combine(dir, $"{sessionId}.jsonl");
        // Only inherit the source's model/version when the transcript came from THIS store. A
        // transcript converted from another CLI carries that CLI's model name, and stamping e.g.
        // `claude-opus-5` into a Codex rollout makes the resumed session fail outright rather than
        // quietly pick a default.
        var sameStore = session.Tool == Tool;
        var model = options.Model ?? (sameStore ? session.Model : null) ?? "claude-sonnet-4-5";
        var version = options.CliVersion ?? (sameStore ? session.CliVersion : null) ?? "2.1.0";

        var lines = new List<JsonObject>();
        string? parentUuid = null;

        JsonObject Envelope(string type, string uuid)
        {
            var o = new JsonObject
            {
                ["parentUuid"] = parentUuid,
                ["isSidechain"] = false,
                ["userType"] = "external",
                ["cwd"] = cwd,
                ["sessionId"] = sessionId,
                ["version"] = version,
                ["gitBranch"] = session.GitBranch ?? string.Empty,
                ["type"] = type,
                ["uuid"] = uuid,
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("o"),
            };
            parentUuid = uuid;
            return o;
        }

        void AddUserText(string text)
        {
            var o = Envelope("user", Guid.NewGuid().ToString());
            o["message"] = new JsonObject { ["role"] = "user", ["content"] = text };
            lines.Add(o);
        }

        void AddAssistant(JsonArray content, string stopReason)
        {
            var o = Envelope("assistant", Guid.NewGuid().ToString());
            o["requestId"] = $"req_xfer_{Guid.NewGuid():N}"[..24];
            o["message"] = new JsonObject
            {
                ["model"] = model,
                ["id"] = $"msg_xfer_{Guid.NewGuid():N}"[..24],
                ["type"] = "message",
                ["role"] = "assistant",
                ["content"] = content,
                ["stop_reason"] = stopReason,
                ["stop_sequence"] = null,
                ["usage"] = new JsonObject
                {
                    ["input_tokens"] = 1,
                    ["output_tokens"] = 1,
                    ["cache_creation_input_tokens"] = 0,
                    ["cache_read_input_tokens"] = 0,
                },
            };
            lines.Add(o);
        }

        void AddToolExchange(string toolName, string? argumentsJson, string? result, bool isError)
        {
            var toolUseId = $"toolu_{Guid.NewGuid():N}"[..28];
            JsonNode input;
            try
            {
                input = argumentsJson is null ? new JsonObject() : JsonNode.Parse(argumentsJson) ?? new JsonObject();
            }
            catch (JsonException)
            {
                input = new JsonObject { ["raw"] = argumentsJson };
            }

            AddAssistant([new JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = toolUseId,
                ["name"] = toolName,
                ["input"] = input,
            }], "tool_use");

            var u = Envelope("user", Guid.NewGuid().ToString());
            u["message"] = new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = toolUseId,
                    ["is_error"] = isError,
                    ["content"] = result ?? string.Empty,
                }),
            };
            lines.Add(u);
        }

        foreach (var m in session.Messages)
        {
            ct.ThrowIfCancellationRequested();
            switch (m.Type)
            {
                case MessageType.UserMessage:
                    if (!string.IsNullOrWhiteSpace(m.Content))
                        AddUserText(m.Content);
                    break;

                case MessageType.ToolCall when m.ToolCall is { } tc:
                    AddToolExchange(TranscriptNarration.QualifiedToolName(tc), tc.Arguments,
                        tc.Result ?? tc.Error, isError: !string.IsNullOrEmpty(tc.Error));
                    break;

                case MessageType.CommandExecution when m.CommandExecution is { } cmd:
                    AddToolExchange(
                        "Bash",
                        new JsonObject { ["command"] = cmd.Command }.ToJsonString(),
                        // Claude's format has an is_error flag and no exit code, so the number
                        // rides in the output the way a shell tool prints it — the same line the
                        // Codex and Copilot readers already recover a status from.
                        TranscriptNarration.WithExitStatus(cmd),
                        isError: cmd.ExitCode is not (null or 0));
                    break;

                default:
                    // Reasoning, Plan, FileChange, CompactBoundary and friends have no faithful
                    // Claude wire form, so they cross as assistant prose. Built from the payload
                    // when the message has no Content of its own — otherwise a file edit, whose
                    // path and diff ARE the message, was dropped without a word.
                    if (TranscriptNarration.DescribeForProse(m) is { } narration)
                        AddAssistant([new JsonObject { ["type"] = "text", ["text"] = narration }], "end_turn");
                    break;
            }
        }

        if (lines.Count == 0)
            throw new TranscriptStoreException("Nothing to write — the session has no transferable messages.");

        Directory.CreateDirectory(dir);
        await using var writer = new StreamWriter(path, append: false);
        foreach (var line in lines)
            await writer.WriteLineAsync(line.ToJsonString().AsMemory(), ct);

        _logger?.LogInformation(
            "Wrote Claude session {SessionId} ({Lines} lines) to {Path}", sessionId, lines.Count, path);
        return sessionId;
    }

    // ── json helpers ──────────────────────────────────────────────────────

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

    private static string? GetString(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private static bool GetBool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;
}
