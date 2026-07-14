namespace Mintokei.AgentEngine.AgentTools;

/// <summary>
/// UI-facing descriptor for a single agent-tool config field. The ground truth
/// of which keys exist lives in the per-CLI <c>ConfigMapper</c> classes; this
/// record mirrors the subset we expose to the frontend so it can render rows
/// dynamically and gate mutability on live process state.
/// </summary>
public record AgentToolConfigField(
    string Key,
    string Label,
    AgentToolConfigFieldType Type,
    AgentToolConfigApplyMode ApplyMode,
    IReadOnlyList<AgentToolConfigOption>? Options = null,
    string? HelpText = null,
    string? DefaultValue = null);

public record AgentToolConfigOption(string Value, string Label);

public enum AgentToolConfigFieldType
{
    Select,
    Bool,
    String,
    Int,
}

public enum AgentToolConfigApplyMode
{
    /// <summary>
    /// Change is pushed to the running process and takes effect on the current
    /// in-flight turn. Safe to expose mid-stream.
    /// </summary>
    Immediate,

    /// <summary>
    /// Change is stored but only takes effect on the next turn. Safe to edit
    /// while the process is alive; the running turn continues with old values.
    /// </summary>
    NextTurn,

    /// <summary>
    /// Change requires a process restart to take effect. Should be editable
    /// only when the process is idle / not running.
    /// </summary>
    Restart,
}
