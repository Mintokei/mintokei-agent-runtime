using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mintokei.Memory;

public record OperationConfig(string Provider = "agent_engine", string? Model = null, string ReasoningEffort = "low", string PromptVersion = "v1", string SchemaVersion = "v1", int TimeoutSeconds = 120, bool Enabled = true, string ExecutionMode = "read_only", bool WebSearch = false);
