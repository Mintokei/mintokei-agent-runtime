namespace Mintokei.AgentEngine.AgentTools;

public interface IModelDiscoveryProvider
{
    AgentToolKey AgentToolKey { get; }
    Task<AgentToolModelList> DiscoverModelsAsync(CancellationToken ct = default);
}
