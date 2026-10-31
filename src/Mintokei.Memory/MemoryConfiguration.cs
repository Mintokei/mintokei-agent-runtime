namespace Mintokei.Memory;

public class MemoryConfiguration
{
    public MemorySearchScope SearchScope { get; set; } = MemorySearchScope.AllAuthorizedHistory;
    public Dictionary<string, OperationConfig> Operations { get; set; } = new()
    {
        ["extraction"] = new(PromptVersion: "v2"),
        ["reference-detection"] = new(PromptVersion: "v2"),
        ["reference-resolution"] = new(PromptVersion: "v2"),
        ["reconciliation"] = new(),
        ["state-update"] = new(PromptVersion: "v2"),
        ["summarization"] = new()
    };
    public int RecentMessages { get; set; } = 10;
    public int RecentTokens { get; set; } = 3000;
    public int TopK { get; set; } = 8;
    public string ExtractionMode { get; set; } = "combined";
    public string MemoryProvider { get; set; } = "postgres";
    public bool CapturePayloads { get; set; }
    public bool AllowSensitiveMemory { get; set; }
    public int ConsolidateEveryTurns { get; set; } = 30;
    public int MaxSearchRounds { get; set; } = 1;
}
