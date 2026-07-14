using System.Text.Json;
using Mintokei.AgentEngine.AgentTools.Claude;

using Mintokei.AgentEngine.Contracts;

namespace Mintokei.AgentEngine.Claude;

/// <summary>
/// Builds the Claude Code <c>control_response</c> JSON that answers a
/// <c>control_request</c> (a permission prompt or an <c>AskUserQuestion</c>)
/// from the user's decision.
///
/// Extracted so the two callers that must produce a byte-identical response
/// share one implementation:
/// <list type="bullet">
///   <item>the live turn (<c>ClaudeCodeExecutionService</c>), which
///   writes it to the process the moment the in-memory TCS completes; and</item>
///   <item>the durable recovery path
///   (<c>RespondToUserInteractionHandler</c>), which rebuilds it from the
///   persisted interaction and delivers it over the WriteStdin outbox when the
///   in-memory turn no longer exists — e.g. the API restarted between the
///   question being asked and the user answering, which previously dropped the
///   answer silently and hung the task forever.</item>
/// </list>
/// </summary>
public static class ClaudeControlResponseBuilder
{
    /// <param name="requestId">The control_request id being answered.</param>
    /// <param name="isAskUser">True for AskUserQuestion, false for a permission prompt.</param>
    /// <param name="questionsJson">The AskUserQuestion <c>questions</c> array JSON (null for permission prompts).</param>
    /// <param name="toolInputRaw">The original tool input JSON (used to echo <c>updatedInput</c> for permission allows).</param>
    /// <param name="decision">The user's decision.</param>
    public static string Build(
        string requestId,
        bool isAskUser,
        string? questionsJson,
        string? toolInputRaw,
        UserInteractionResponse decision)
    {
        object responseBehavior;
        if (isAskUser)
        {
            if (decision.Decision is "deny" or "reject")
            {
                responseBehavior = new { behavior = "deny", message = decision.Message ?? "Rejected by user" };
            }
            else
            {
                var answersDict = new Dictionary<string, string>();
                if (!string.IsNullOrEmpty(decision.AnswersJson))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(decision.AnswersJson);
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            if (prop.Value.TryGetProperty("answers", out var answers)
                                && answers.GetArrayLength() > 0)
                            {
                                var answer = answers[0].GetString() ?? "";
                                var questionText = FindQuestionTextById(questionsJson, prop.Name) ?? prop.Name;
                                answersDict[questionText] = answer;
                            }
                        }
                    }
                    catch { }
                }

                if (answersDict.Count == 0 && !string.IsNullOrEmpty(decision.Message))
                {
                    var key = FirstQuestionText(questionsJson) ?? "question";
                    answersDict[key] = decision.Message;
                }

                object? originalQuestions = null;
                if (questionsJson is not null)
                {
                    try
                    {
                        using var qDoc = JsonDocument.Parse(questionsJson);
                        originalQuestions = qDoc.RootElement.Clone();
                    }
                    catch { }
                }

                responseBehavior = new
                {
                    behavior = "allow",
                    updatedInput = new
                    {
                        questions = originalQuestions ?? (object)Array.Empty<object>(),
                        answers = answersDict,
                    },
                };
            }
        }
        else
        {
            if (decision.Decision == "allow")
            {
                object? originalInput = null;
                if (toolInputRaw is not null)
                {
                    using var doc = JsonDocument.Parse(toolInputRaw);
                    originalInput = doc.RootElement.Clone();
                }

                object? updatedPermissions = null;
                if (!string.IsNullOrEmpty(decision.UpdatedPermissionsJson))
                {
                    using var permDoc = JsonDocument.Parse(decision.UpdatedPermissionsJson);
                    updatedPermissions = permDoc.RootElement.Clone();
                }

                if (updatedPermissions is not null)
                {
                    responseBehavior = new
                    {
                        behavior = "allow",
                        updatedInput = originalInput ?? new object(),
                        updatedPermissions,
                    };
                }
                else
                {
                    responseBehavior = new { behavior = "allow", updatedInput = originalInput ?? new object() };
                }
            }
            else
            {
                if (decision.Interrupt)
                {
                    responseBehavior = new { behavior = "deny", message = decision.Message ?? "Denied by user", interrupt = true };
                }
                else
                {
                    responseBehavior = new { behavior = "deny", message = decision.Message ?? "Denied by user" };
                }
            }
        }

        return JsonSerializer.Serialize(new
        {
            type = "control_response",
            response = new
            {
                subtype = "success",
                request_id = requestId,
                response = responseBehavior,
            },
        }, ClaudeCodeHelper.JsonOptions);
    }

    private static string? FirstQuestionText(string? questionsJson)
    {
        if (string.IsNullOrEmpty(questionsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(questionsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Array
                && doc.RootElement.GetArrayLength() > 0
                && doc.RootElement[0].TryGetProperty("question", out var q))
            {
                return q.GetString();
            }
        }
        catch { }
        return null;
    }

    private static string? FindQuestionTextById(string? questionsJson, string questionId)
    {
        if (string.IsNullOrEmpty(questionsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(questionsJson);
            var arr = doc.RootElement;

            foreach (var q in arr.EnumerateArray())
            {
                var id = q.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                if (id == questionId && q.TryGetProperty("question", out var text))
                    return text.GetString();
            }

            if (questionId.StartsWith("q_", StringComparison.Ordinal)
                && int.TryParse(questionId.AsSpan(2), out var idx)
                && idx >= 0 && idx < arr.GetArrayLength()
                && arr[idx].TryGetProperty("question", out var qText))
            {
                return qText.GetString();
            }
        }
        catch { }
        return null;
    }
}
