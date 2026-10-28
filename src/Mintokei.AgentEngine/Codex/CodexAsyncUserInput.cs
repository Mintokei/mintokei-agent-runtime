using System.Text;
using System.Text.Json;
using Mintokei.AgentEngine.Contracts;
using Mintokei.AgentEngine.AgentTools.Codex;

namespace Mintokei.AgentEngine.Codex;

/// <summary>
/// Questions attached to an async agentMessage are answered with user input, not a JSON-RPC
/// response. Keep that distinction durable so answering also works after a session is resumed.
/// </summary>
public static class CodexAsyncUserInput
{
    public const string ReplyContext = "{\"kind\":\"asyncUserInput\"}";
    public const string ToolName = "send_message_to_user_async";

    internal static UserInteractionData? Parse(JsonElement item)
    {
        if (!item.TryGetProperty("questions", out var questions) || questions.ValueKind != JsonValueKind.Array)
            return null;

        var normalized = new List<object>();
        foreach (var question in questions.EnumerateArray())
        {
            if (question.ValueKind != JsonValueKind.Object
                || !question.TryGetProperty("title", out var title)
                || title.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(title.GetString()))
                continue;

            var options = new List<object>();
            if (question.TryGetProperty("options", out var choices) && choices.ValueKind == JsonValueKind.Array)
                foreach (var choice in choices.EnumerateArray())
                    if (choice.ValueKind == JsonValueKind.String)
                        options.Add(new { label = choice.GetString() });

            normalized.Add(new { id = $"q_{normalized.Count}", question = title.GetString(), options });
        }

        if (normalized.Count == 0)
            return null;

        return new UserInteractionData
        {
            Id = Guid.NewGuid(),
            RequestId = item.GetProperty("id").GetString()!,
            ToolName = ToolName,
            Questions = JsonSerializer.Serialize(normalized, CodexJsonRpcHelper.JsonOptions),
            ReplyContext = ReplyContext,
        };
    }

    /// <summary>Include the original item and question text so a late answer is unambiguous.</summary>
    public static string? BuildAnswerMessage(UserInteractionData interaction, UserInteractionResponse response)
    {
        var questions = interaction.QuestionList;
        if (questions.Count == 0 || string.IsNullOrWhiteSpace(response.AnswersJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(response.AnswersJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var text = new StringBuilder($"Reply to your questions (message {interaction.RequestId}):");
            for (var i = 0; i < questions.Count; i++)
            {
                if (!doc.RootElement.TryGetProperty($"q_{i}", out var entry)
                    || entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("answers", out var answers)
                    || answers.ValueKind != JsonValueKind.Array)
                    return null;

                var values = answers.EnumerateArray()
                    .Where(a => a.ValueKind == JsonValueKind.String)
                    .Select(a => a.GetString()?.Trim())
                    .Where(a => !string.IsNullOrEmpty(a)).ToArray();
                if (values.Length == 0)
                    return null;

                text.Append("\n\nQuestion: ").Append(questions[i].Question)
                    .Append("\nAnswer: ").AppendJoin(", ", values);
            }
            return text.ToString();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
