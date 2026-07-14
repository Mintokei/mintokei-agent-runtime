namespace Mintokei.AgentEngine.AgentTools.Gemini;

public sealed class GeminiModelDiscoveryProvider : IModelDiscoveryProvider
{
    public AgentToolKey AgentToolKey => AgentToolKey.GeminiCli;

    public Task<AgentToolModelList> DiscoverModelsAsync(CancellationToken ct = default)
    {
        var result = new AgentToolModelList
        {
            AgentToolKey = AgentToolKey.GeminiCli,
            Models =
            [
                new() { Id = "gemini-2.5-pro", DisplayName = "Gemini 2.5 Pro", IsDefault = true },
                new() { Id = "gemini-2.5-flash", DisplayName = "Gemini 2.5 Flash" },
            ],
            Source = "curated",
            FetchedAt = DateTimeOffset.UtcNow,
        };

        return Task.FromResult(result);
    }
}
