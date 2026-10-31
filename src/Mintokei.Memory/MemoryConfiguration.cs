using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mintokei.Memory;

public class MemoryConfiguration
{
    public MemorySearchScope SearchScope { get; set; } = MemorySearchScope.AllAuthorizedHistory;
    public Dictionary<string, OperationConfig> Operations { get; set; } = new()
    {
        ["chat"] = new(ReasoningEffort: "high", PromptVersion: "v2", ExecutionMode: "workspace_write"),
        ["extraction"] = new(PromptVersion: "v2"),
        ["reference-detection"] = new(PromptVersion: "v2"),
        ["reference-resolution"] = new(ReasoningEffort: "medium", PromptVersion: "v2"),
        ["reconciliation"] = new(),
        ["state-update"] = new(PromptVersion: "v2"),
        ["summarization"] = new(),
        ["context-planning"] = new(ReasoningEffort: "medium", PromptVersion: "v3", SchemaVersion: "v2"),
        ["context-sufficiency"] = new(PromptVersion: "v3"),
        ["context-preparation"] = new(),
        ["context-evaluation"] = new(),
        ["context-answer"] = new(ReasoningEffort: "high")
    };
    public int RecentMessages { get; set; } = 10;
    public int RecentTokens { get; set; } = 3000;
    public int ContextTokens { get; set; } = 7000;
    public int TopK { get; set; } = 8;
    public string ExtractionMode { get; set; } = "combined";
    public string ContextMode { get; set; } = "combined";
    // Legacy is explicit for historical configs and controlled comparisons.
    public string ContextPacking { get; set; } = "legacy";
    public string AnswerMode { get; set; } = "separate";
    public string AgentPromptVersion { get; set; } = "v4";
    public int AgentMaxToolRounds { get; set; } = 3;
    public int AgentSessionTokens { get; set; } = 24000;
    public string AgentSessionMode { get; set; } = "reuse";
    public bool AgentAllowCoding { get; set; }
    public string AgentPermissionVersion { get; set; } = "";
    public int AgentRecentMessages { get; set; } = 8;
    public int AgentTurnContextTokens { get; set; } = 4000;
    public string MemoryProvider { get; set; } = "postgres";
    public bool CapturePayloads { get; set; } = true;
    public bool AllowSensitiveMemory { get; set; }
    public int ConsolidateEveryTurns { get; set; } = 30;
    public int MaxSearchRounds { get; set; } = 1;

    public MemoryConfiguration WithContextOperations()
    {
        // Existing installations inherit their explicitly selected models/effort.
        Operations.TryAdd("context-preparation", Operations["context-planning"] with { PromptVersion = "v1", SchemaVersion = "v1" });
        Operations.TryAdd("context-evaluation", Operations["context-sufficiency"] with { PromptVersion = "v1", SchemaVersion = "v1" });
        Operations.TryAdd("context-answer", Operations["chat"] with { PromptVersion = "v1", SchemaVersion = "v1", ExecutionMode = "read_only", WebSearch = false });
        return this;
    }
}
