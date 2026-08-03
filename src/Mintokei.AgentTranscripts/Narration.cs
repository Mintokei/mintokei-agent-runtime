using System.Text;

using Mintokei.AgentEngine.Contracts;

namespace Mintokei.AgentTranscripts;

/// <summary>
/// The sentence a message never had.
///
/// Every writer falls back on <c>AgentMessage.Content</c> for a kind it has no wire form for. That
/// works while the message has prose to fall back on — and a <see cref="MessageType.FileChange"/>
/// straight from a parser has none, because the path and the diff <em>are</em> the message. The
/// edit was then dropped whole: not the path, not the diff, not a line saying a file was touched.
/// A moved conversation showed the request and no answer, which reads as work never done.
///
/// So the narration is built from whatever the message actually carries. Prose rather than a
/// reconstructed tool call, deliberately: a diff does not contain the <c>old_string</c> /
/// <c>new_string</c> an edit tool wants, and a fabricated call is a patch the next agent believes
/// it can apply. Saying plainly what changed is honest; guessing is not.
/// </summary>
public static class TranscriptNarration
{
    /// <summary>
    /// A description of <paramref name="message"/> for a store with no wire form for its kind, or
    /// null when there is genuinely nothing to say.
    /// </summary>
    public static string? Describe(AgentMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.Content))
            return message.Content;

        var sb = new StringBuilder();

        foreach (var change in message.FileChanges)
        {
            var verb = change.ChangeKind switch
            {
                FileChangeKind.Add => "Created",
                FileChangeKind.Delete => "Deleted",
                _ => "Edited",
            };
            if (sb.Length > 0)
                sb.AppendLine();
            sb.Append(verb).Append(' ').Append(change.Path);
            if (!string.IsNullOrWhiteSpace(change.Diff))
                sb.AppendLine().AppendLine().Append(change.Diff.TrimEnd());
        }

        if (sb.Length > 0)
            return sb.ToString();

        if (message.UserInteraction is { } interaction)
        {
            var asked = FirstNonEmpty(interaction.Questions, interaction.Reason, interaction.ToolName);
            var answered = FirstNonEmpty(interaction.DecisionData, interaction.Decision);
            if (asked is not null || answered is not null)
            {
                sb.Append("Asked: ").Append(asked ?? "(question not recorded)");
                if (answered is not null)
                    sb.AppendLine().Append("Answered: ").Append(answered);
                return sb.ToString();
            }
        }

        if (message.CompactBoundary is not null)
            return "(the conversation was compacted here — earlier turns were summarised away)";

        return null;
    }

    /// <summary>
    /// The same, marked when the kind would otherwise be mistaken for something the agent said.
    ///
    /// Thinking has no wire form anywhere, so it is written as assistant prose and comes back
    /// indistinguishable from speech — a private "maybe the rounding is wrong, I should check"
    /// arriving as an assertion, next to the answer that it is fine. Marking it keeps the
    /// consideration visible without letting it read as a claim.
    /// </summary>
    public static string? DescribeForProse(AgentMessage message)
    {
        var text = Describe(message);
        if (text is null)
            return null;

        return message.Type switch
        {
            MessageType.Reasoning => $"(thinking) {text}",
            MessageType.Plan => $"(plan) {text}",
            _ => text,
        };
    }

    /// <summary>
    /// The tool name to write, in Claude Code's <c>mcp__server__tool</c> convention when the call
    /// came from an MCP server. Otherwise the server is lost, and the call arrives looking like a
    /// native tool the target simply does not have — with no hint as to why.
    /// </summary>
    public static string QualifiedToolName(ToolCallData tool) =>
        string.IsNullOrWhiteSpace(tool.ServerName) || tool.ToolName.StartsWith("mcp__", StringComparison.Ordinal)
            ? tool.ToolName
            : $"mcp__{tool.ServerName}__{tool.ToolName}";

    /// <summary>
    /// Command output with the exit status appended when it failed and does not already say so.
    ///
    /// No format has a field for it — Claude carries a boolean, Codex and Copilot recover a number
    /// by matching <c>exited with code N</c> in the output their own tools print. The readers
    /// already look for that line; the writers never wrote one, so a failure crossed as text that
    /// happened to contain the word "Failed" and nothing a consumer could check.
    /// </summary>
    public static string? WithExitStatus(CommandExecutionData command)
    {
        var output = command.Output;
        if (command.ExitCode is null or 0)
            return output;
        if (output is not null && output.Contains("exited with code", StringComparison.OrdinalIgnoreCase))
            return output;

        var suffix = $"exited with code {command.ExitCode}";
        return string.IsNullOrEmpty(output) ? suffix : $"{output.TrimEnd()}\n\n{suffix}";
    }

    /// <summary>
    /// The server a qualified name came from, or null. The name itself is left whole — that is the
    /// convention the Codex store already reads, and shortening it would change what an existing
    /// consumer sees for sessions Codex wrote itself.
    /// </summary>
    public static (string ToolName, string? ServerName) SplitToolName(string name)
    {
        if (!name.StartsWith("mcp__", StringComparison.Ordinal))
            return (name, null);

        var rest = name["mcp__".Length..];
        var split = rest.IndexOf("__", StringComparison.Ordinal);
        if (split <= 0 || split + 2 >= rest.Length)
            return (name, null);       // malformed; better the odd name than an invented server

        return (name, rest[..split]);
    }

    private static string? FirstNonEmpty(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
}
