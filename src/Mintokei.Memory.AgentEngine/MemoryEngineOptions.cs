namespace Mintokei.Memory.AgentEngine;

/// <summary>Local reasoning settings. The host's existing runner factory owns process creation.</summary>
public sealed class MemoryEngineOptions
{
    public string CodexLoginHome { get; set; } = Environment.GetEnvironmentVariable("CODEX_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    public string RuntimeHome { get; set; } = Path.Combine(AppContext.BaseDirectory, "memory-data", "codex");
}
