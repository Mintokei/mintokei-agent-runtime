namespace Mintokei.Memory;

public sealed class NullMemoryJobObserver : IMemoryJobObserver
{
    public Task CompletedAsync(Job job, CancellationToken ct) => Task.CompletedTask;
}
