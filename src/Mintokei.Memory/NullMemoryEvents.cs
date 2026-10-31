namespace Mintokei.Memory;

public sealed class NullMemoryEvents : IMemoryEvents
{
    public Task Send(Guid conversation, string type, object? data, CancellationToken ct = default) => Task.CompletedTask;
}
