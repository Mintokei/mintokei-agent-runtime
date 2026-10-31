namespace Mintokei.Memory;

/// <summary>The host executes one structured reasoning operation using its configured agent.</summary>
public interface IMemoryReasoningRunner
{
    Task<MemoryReasoningResult> RunAsync(MemoryReasoningRequest request, CancellationToken cancellationToken);
}
