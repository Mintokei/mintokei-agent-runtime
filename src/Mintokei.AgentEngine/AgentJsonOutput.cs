using System.Text.Json;
using Mintokei.AgentEngine.Contracts;

namespace Mintokei.AgentEngine;

/// <summary>Collect a bounded final JSON answer from a session already configured for inference only.
/// Permissions fail closed. A single enclosing JSON code fence is removed; tool output,
/// surrounding prose and reasoning never enter the result.</summary>
public static class AgentJsonOutput
{
    public static async Task<string> CollectAsync(IAgentSession session, int maxCharacters = 128000, CancellationToken ct = default)
    {
        if (maxCharacters <= 0) throw new ArgumentOutOfRangeException(nameof(maxCharacters));
        string? answer = null;
        await foreach (var output in session.Output.WithCancellation(ct))
        {
            switch (output)
            {
                case InteractionRequested request:
                    await session.RespondAsync(request.RequestId, new(Decision: "deny", Message: "This request permits inference only.", AnswersJson: null), ct);
                    throw new AgentInferenceException("The inference session requested a tool or interaction.");
                case MessageOutput { Message: { Type: MessageType.Error } error }:
                    throw new AgentInferenceException("The inference provider reported an error.", error.FailureKind);
                case MessageOutput { Message: { Role: MessageRole.Assistant, Type: MessageType.AgentMessage, ParentToolUseId: null, Content: { } content } }:
                    if (content.Length > maxCharacters) throw new AgentInferenceException("The inference result exceeds the output limit.");
                    answer = content;
                    break;
                case TurnEnded turn:
                    if (turn.IsInterrupted || turn.Failure != null)
                        throw new AgentInferenceException("The inference turn did not complete successfully.", turn.Failure?.Kind);
                    if (string.IsNullOrWhiteSpace(answer)) throw new AgentInferenceException("The inference turn returned no JSON answer.");
                    answer = UnwrapJsonFence(answer);
                    try { using var json = JsonDocument.Parse(answer, new() { MaxDepth = 64 }); }
                    catch (JsonException) { throw new AgentInferenceException("The inference turn returned invalid JSON."); }
                    return answer;
            }
        }
        throw new AgentInferenceException("The inference stream ended before its turn completed.");
    }

    // Claude can wrap an otherwise valid answer despite a JSON-only prompt. Accept only one
    // complete, explicitly JSON (or unlabelled) fence around the entire answer. Never search
    // prose for a JSON-looking substring or repair malformed/truncated JSON.
    private static string UnwrapJsonFence(string answer)
    {
        var text = answer.Trim();
        var firstLine = text.IndexOf('\n');
        var lastLine = text.LastIndexOf('\n');
        if (firstLine < 0 || lastLine <= firstLine) return answer;
        var opening = text[..firstLine].TrimEnd('\r');
        if (opening != "```" && !opening.Equals("```json", StringComparison.OrdinalIgnoreCase)) return answer;
        if (text[(lastLine + 1)..] != "```") return answer;
        return text[(firstLine + 1)..lastLine].Trim();
    }
}

public sealed class AgentInferenceException(string message, TurnFailureKind? failureKind = null) : Exception(message)
{
    public TurnFailureKind? FailureKind { get; } = failureKind;
}
