namespace Mintokei.Memory;
/// <summary>Optional host notifications. The durable trace remains in the memory store.</summary>
public interface IMemoryEvents
{
    Task Send(Guid conversation, string type, object? data, CancellationToken ct = default);
}
