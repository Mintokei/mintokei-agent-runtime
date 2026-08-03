using System.Text;
using System.Text.Json;

using Mintokei.AgentEngine.Contracts;

namespace Mintokei.AgentTranscripts;

/// <summary>Limits and switches for <see cref="TranscriptSummarising.Summarise"/>.</summary>
public sealed record SummaryOptions
{
    /// <summary>How many user requests to list, oldest first. Older ones beyond this are counted, not printed.</summary>
    public int MaxRequests { get; init; } = 20;

    /// <summary>How many touched file paths to list.</summary>
    public int MaxFiles { get; init; } = 40;

    /// <summary>How many recent shell commands to list.</summary>
    public int MaxCommands { get; init; } = 15;

    /// <summary>How much of the final assistant message to carry verbatim.</summary>
    public int MaxClosingChars { get; init; } = 4000;

    /// <summary>Include the files-touched and commands-run sections.</summary>
    public bool IncludeToolActivity { get; init; } = true;

    /// <summary>
    /// A briefing written by something that read the conversation — a model, usually — placed where
    /// the previous agent's closing message would otherwise go.
    ///
    /// It is additive rather than a replacement: the extracted sections stay unless
    /// <see cref="IncludeFacts"/> says otherwise. Whoever wrote this can be wrong about what they
    /// read; a list of the files the transcript records cannot be.
    /// </summary>
    public string? Narrative { get; init; }

    /// <summary>
    /// Include the extracted sections — volume, requests, files, commands. False leaves the
    /// metadata and the narrative alone, for a caller who wants prose and nothing else.
    /// </summary>
    public bool IncludeFacts { get; init; } = true;

    /// <summary>Replaces the opening line. Null uses the default handover sentence.</summary>
    public string? Header { get; init; }

    /// <summary>
    /// The reply attributed to the agent after the briefing. A transcript that ends on a user turn
    /// looks like an unanswered question — to the next CLI, and to
    /// <see cref="TranscriptTrimming.TrimIncompleteTail"/>.
    /// </summary>
    public string Acknowledgement { get; init; } =
        "Understood — I have the context above and I'm ready to carry on.";
}

/// <summary>
/// Compresses a whole conversation into a single briefing exchange.
///
/// Moving a session between CLIs re-ingests the entire transcript, so a long one can overflow the
/// target's context window — and the cost is paid on every hop. Summarising trades the turn-by-turn
/// record for a description of it: what was asked, what was touched, and where the previous agent
/// got to.
///
/// This is lossy on purpose and is the wrong default. Prefer moving the real transcript, and reach
/// for this when the alternative is not fitting at all.
/// </summary>
public static class TranscriptSummarising
{
    /// <summary>
    /// Returns a transcript holding one user briefing and one assistant acknowledgement, in place
    /// of the original conversation. Metadata (tool, cwd, model, source path) is preserved.
    /// </summary>
    public static StoredTranscript Summarise(this StoredTranscript transcript, SummaryOptions? options = null)
    {
        options ??= new SummaryOptions();
        var briefing = BuildBriefing(transcript, options);

        var user = new AgentMessage
        {
            Id = TranscriptIds.Derive(transcript.SessionId, "summary", "user"),
            Role = MessageRole.User,
            Type = MessageType.UserMessage,
            Content = briefing,
            CreatedAt = transcript.CreatedAt,
        };
        var assistant = new AgentMessage
        {
            Id = TranscriptIds.Derive(transcript.SessionId, "summary", "assistant"),
            Role = MessageRole.Assistant,
            Type = MessageType.AgentMessage,
            Content = options.Acknowledgement,
            CreatedAt = transcript.CreatedAt,
        };

        return transcript with { Messages = [user, assistant] };
    }

    /// <summary>
    /// Summarises only when the transcript is longer than <paramref name="maxMessages"/>, and
    /// returns it untouched otherwise — so a caller can guard against overflowing the target's
    /// context without giving up fidelity on the sessions that would have fitted.
    /// </summary>
    public static StoredTranscript SummariseIfLonger(
        this StoredTranscript transcript, int maxMessages, SummaryOptions? options = null)
        => transcript.Messages.Count > maxMessages ? transcript.Summarise(options) : transcript;

    private static string BuildBriefing(StoredTranscript t, SummaryOptions o)
    {
        var requests = t.Messages
            .Where(m => m.Type == MessageType.UserMessage && !string.IsNullOrWhiteSpace(m.Content))
            .Select(m => m.Content!)
            .ToList();
        var replies = t.Messages.Count(m => m.Type == MessageType.AgentMessage);
        var calls = t.Messages.Count(m => m.ToolCall is not null || m.CommandExecution is not null);

        var sb = new StringBuilder();
        sb.AppendLine(o.Header ?? $"[handover] Summary of a conversation from {Describe(t)}. "
            + "The turn-by-turn history is not included — this is a description of it.");
        sb.AppendLine();
        sb.AppendLine($"- Original session: {t.SessionId}");
        if (!string.IsNullOrWhiteSpace(t.SourcePath))
            sb.AppendLine($"- Full transcript: {t.SourcePath}");
        if (!string.IsNullOrWhiteSpace(t.Cwd))
            sb.AppendLine($"- Working directory: {t.Cwd}");
        if (!string.IsNullOrWhiteSpace(t.Model))
            sb.AppendLine($"- Model: {t.Model}");
        if (o.IncludeFacts)
            sb.AppendLine($"- Volume: {requests.Count} request(s), {replies} reply(ies), {calls} tool call(s)");

        if (o.IncludeFacts && requests.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## What was asked, in order");
            sb.AppendLine();
            var shown = requests.Count <= o.MaxRequests ? requests : requests[^o.MaxRequests..];
            if (requests.Count > shown.Count)
                sb.AppendLine($"({requests.Count - shown.Count} earlier request(s) omitted)");
            for (var i = 0; i < shown.Count; i++)
                sb.AppendLine($"{i + 1}. {OneLine(shown[i], 400)}");
        }

        if (o.IncludeFacts && o.IncludeToolActivity)
        {
            var files = TouchedFiles(t).Take(o.MaxFiles + 1).ToList();
            if (files.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Files touched");
                sb.AppendLine();
                foreach (var f in files.Take(o.MaxFiles))
                    sb.AppendLine($"- {f}");
                if (files.Count > o.MaxFiles)
                    sb.AppendLine("- … and more");
            }

            var commands = t.Messages
                .Where(m => m.CommandExecution is not null)
                .Select(m => m.CommandExecution!.Command)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .ToList();
            if (commands.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Recent commands");
                sb.AppendLine();
                foreach (var c in commands.TakeLast(o.MaxCommands))
                    sb.AppendLine($"- {OneLine(c, 160)}");
            }
        }

        if (!string.IsNullOrWhiteSpace(o.Narrative))
        {
            // Attributed, because a reader who cannot tell an extracted fact from a second agent's
            // reading of the conversation will treat a mistaken summary as something that happened.
            sb.AppendLine();
            sb.AppendLine("## Handover briefing (written by an agent that read the transcript)");
            sb.AppendLine();
            sb.AppendLine(o.Narrative.Trim());
        }
        else
        {
            var closing = t.Messages.LastOrDefault(m =>
                m.Type == MessageType.AgentMessage && !string.IsNullOrWhiteSpace(m.Content))?.Content;
            if (!string.IsNullOrWhiteSpace(closing))
            {
                sb.AppendLine();
                sb.AppendLine("## Where the previous agent left off");
                sb.AppendLine();
                sb.AppendLine(Truncate(closing.Trim(), o.MaxClosingChars));
            }
        }

        sb.AppendLine();
        sb.AppendLine("Carry on from here. Check the workspace rather than assuming anything above "
            + "is still true, and ask instead of redoing work that looks done.");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Paths the conversation touched, in first-seen order. <see cref="AgentMessage.FileChanges"/>
    /// is the reliable source but not every backend fills it, so tool arguments are also scanned
    /// for the usual path keys.
    /// </summary>
    private static IEnumerable<string> TouchedFiles(StoredTranscript t)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in t.Messages)
        {
            foreach (var change in m.FileChanges)
            {
                if (!string.IsNullOrWhiteSpace(change.Path) && seen.Add(change.Path))
                    yield return change.Path;
            }

            if (m.ToolCall?.Arguments is not { Length: > 0 } args)
                continue;
            foreach (var path in PathsIn(args))
            {
                if (seen.Add(path))
                    yield return path;
            }
        }
    }

    private static IEnumerable<string> PathsIn(string argumentsJson)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                yield break;
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            yield break;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
                continue;
            var name = property.Name;
            var looksLikePath =
                name.Equals("path", StringComparison.OrdinalIgnoreCase)
                || name.Equals("file_path", StringComparison.OrdinalIgnoreCase)
                || name.Equals("filePath", StringComparison.OrdinalIgnoreCase)
                || name.Equals("notebook_path", StringComparison.OrdinalIgnoreCase);
            if (looksLikePath && property.Value.GetString() is { Length: > 0 } value)
                yield return value;
        }
    }

    private static string Describe(StoredTranscript t) => t.Tool switch
    {
        Mintokei.AgentEngine.AgentTools.AgentToolKey.ClaudeCodeCli => "Claude Code",
        Mintokei.AgentEngine.AgentTools.AgentToolKey.CodexCli => "Codex",
        Mintokei.AgentEngine.AgentTools.AgentToolKey.GithubCopilotCli => "GitHub Copilot CLI",
        Mintokei.AgentEngine.AgentTools.AgentToolKey.OpenCodeCli => "OpenCode",
        Mintokei.AgentEngine.AgentTools.AgentToolKey.GeminiCli => "Gemini CLI",
        _ => t.Tool.ToString(),
    };

    private static string OneLine(string value, int max)
    {
        var flat = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return Truncate(flat, max);
    }

    private static string Truncate(string value, int max) =>
        max > 0 && value.Length > max ? value[..max] + " …" : value;
}
