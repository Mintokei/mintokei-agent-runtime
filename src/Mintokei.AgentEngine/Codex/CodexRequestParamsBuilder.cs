using Mintokei.AgentEngine.Contracts;

namespace Mintokei.AgentEngine.Codex;

/// <summary>
/// Centralizes Codex JSON-RPC payload construction so thread/start,
/// thread/resume, and turn/start stay in sync as config support evolves.
/// </summary>
internal static class CodexRequestParamsBuilder
{
    public static Dictionary<string, object?> BuildThreadStartParams(CodexConfigMapper.ThreadStartConfig config)
    {
        var parameters = new Dictionary<string, object?>();
        AddThreadConfig(parameters, config, includeEphemeral: true);
        return parameters;
    }

    public static Dictionary<string, object?> BuildThreadResumeParams(string threadId, CodexConfigMapper.ThreadStartConfig config)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["threadId"] = threadId,
            // Full-history hydration (the default) replays every turn in the response, which for
            // long-running threads can run tens of MB and blow well past SendRequestAndWaitAsync's
            // 30s cap, killing the process mid-handshake. We only ever read result.thread.id from
            // this response (see CodexJsonRpcHelper.ExtractThreadId) — the turns are never used — so
            // excluding them is free. History for display comes from Mintokei's own message store,
            // not this response.
            ["excludeTurns"] = true,
        };

        AddThreadConfig(parameters, config, includeEphemeral: false);
        return parameters;
    }

    public static Dictionary<string, object?> BuildTurnStartParams(
        string threadId,
        IReadOnlyList<object> input,
        CodexConfigMapper.TurnStartConfig? config)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["threadId"] = threadId,
            ["input"] = input,
        };

        if (config is null)
            return parameters;

        AddIfNotNull(parameters, "model", config.Model);
        AddIfNotNull(parameters, "effort", config.Effort);
        AddIfNotNull(parameters, "summary", config.Summary);
        AddIfNotNull(parameters, "personality", config.Personality);
        AddIfNotNull(parameters, "approvalPolicy", config.ApprovalPolicy);
        AddIfNotNull(parameters, "sandboxPolicy", config.SandboxPolicy);

        var collaborationMode = BuildCollaborationMode(config);
        AddIfNotNull(parameters, "collaborationMode", collaborationMode);

        return parameters;
    }

    private static void AddThreadConfig(
        IDictionary<string, object?> parameters,
        CodexConfigMapper.ThreadStartConfig config,
        bool includeEphemeral)
    {
        AddIfNotNull(parameters, "model", config.Model);
        AddIfNotNull(parameters, "modelProvider", config.ModelProvider);
        AddIfNotNull(parameters, "approvalPolicy", config.ApprovalPolicy);
        AddIfNotNull(parameters, "sandbox", config.Sandbox);
        AddIfNotNull(parameters, "personality", config.Personality);
        AddIfNotNull(parameters, "config", config.Config);
        AddIfNotNull(parameters, "baseInstructions", config.BaseInstructions);

        if (includeEphemeral && config.Ephemeral.HasValue)
            parameters["ephemeral"] = config.Ephemeral.Value;
    }

    private static object? BuildCollaborationMode(CodexConfigMapper.TurnStartConfig? turnConfig)
    {
        if (turnConfig?.CollaborationMode is null)
            return null;

        return new Dictionary<string, object?>
        {
            ["mode"] = turnConfig.CollaborationMode,
            ["settings"] = new Dictionary<string, object?>
            {
                ["model"] = turnConfig.Model ?? "o3",
                ["reasoning_effort"] = turnConfig.Effort,
            },
        };
    }

    private static void AddIfNotNull(IDictionary<string, object?> parameters, string key, object? value)
    {
        if (value is not null)
            parameters[key] = value;
    }
}
