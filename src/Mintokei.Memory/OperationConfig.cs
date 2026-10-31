namespace Mintokei.Memory;

/// <summary>Pipeline settings only. Agent/tool/model selection belongs to the host application.</summary>
public record OperationConfig(string PromptVersion = "v1", string SchemaVersion = "v1", int TimeoutSeconds = 120, bool Enabled = true);
