namespace Mintokei.Memory;
/// <summary>Optional application callback after a committed memory job (for example, advancing a replay).</summary>
public interface IMemoryJobObserver
{
    Task CompletedAsync(Job job, CancellationToken ct);
}
