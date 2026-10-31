namespace Mintokei.Memory;

public static class MemoryTurnValidation
{
    public static bool IsValid(CompletedMemoryTurn? input, string expectedSystem) => input is not null && input.SourceSystem == expectedSystem && input.TaskId != Guid.Empty && input.TurnId != Guid.Empty && input.Title is { Length: > 0 and <= 500 } && input.Status is ("completed" or "interrupted" or "failed") && input.Messages is { Count: > 0 and <= 1000 } && input.Messages.All(m => m != null && m.Id != Guid.Empty && m.Content != null && m.Role is ("user" or "assistant" or "tool" or "system") && m.Sender is ("user" or "agent" or "system") && m.CreatedAt != default) && input.Messages.Select(m => m.Id).Distinct().Count() == input.Messages.Count && input.Messages.Sum(m => (long)m.Content.Length) <= 4000000;
}
