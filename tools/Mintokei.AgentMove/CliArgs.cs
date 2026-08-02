using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.Claude;
using Mintokei.AgentEngine.Copilot;

namespace Mintokei.AgentMove;

/// <summary>
/// Turns a profile's <c>config</c> into arguments for the CLI's own resume invocation — the form
/// <c>--attach</c> and the printed command need, as opposed to the protocol form the engine speaks.
///
/// Claude and Copilot already have mappers in <c>Mintokei.AgentEngine</c> that emit command-line
/// arguments, so those are reused and stay right as the mappers grow. Codex does not: its mapper
/// targets <c>codex app-server</c> JSON-RPC, so the translation to flags lives here, and every
/// entry in it was checked against the installed CLI rather than inferred:
///
/// <code>
/// codex resume --help                          # -m, -s, -a, --search, -c key=value
/// codex exec --strict-config -c &lt;key&gt;=x        # errors on an unknown config.toml field
/// </code>
///
/// The second is what makes <c>-c</c> safe to use: an unrecognised key is rejected at startup
/// rather than ignored, so the table below is a checked claim and not a hopeful one.
/// </summary>
internal static class CliArgs
{
    /// <summary>The arguments for <paramref name="tool"/>, and the config keys with no flag form.</summary>
    public static (IReadOnlyList<string> Args, IReadOnlyList<string> Dropped) For(
        AgentToolKey tool, Profile profile)
    {
        var config = new Dictionary<string, string?>(profile.Config, StringComparer.OrdinalIgnoreCase);
        if (config.Count == 0)
            return ([], []);

        return tool switch
        {
            AgentToolKey.ClaudeCodeCli => FromMap(ClaudeCodeConfigMapper.MapToCliArgs(config), config, ClaudeUnmappable),
            AgentToolKey.GithubCopilotCli => Copilot(config),
            AgentToolKey.CodexCli => Codex(config),
            _ => ([], config.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList()),
        };
    }

    // ── Codex ────────────────────────────────────────────────────────────

    private static (IReadOnlyList<string>, IReadOnlyList<string>) Codex(Dictionary<string, string?> config)
    {
        var args = new List<string>();
        var dropped = new List<string>();

        foreach (var key in config.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            var value = config[key];
            switch (key.ToLowerInvariant())
            {
                case "model" when NotEmpty(value):
                    args.Add("--model");
                    args.Add(value!);
                    break;

                // Same vocabulary either way: read-only | workspace-write | danger-full-access.
                case "sandbox" when NotEmpty(value):
                    args.Add("--sandbox");
                    args.Add(value!);
                    break;

                // Likewise untrusted | on-request | never.
                case "approvalpolicy" when NotEmpty(value):
                    args.Add("--ask-for-approval");
                    args.Add(value!);
                    break;

                case "websearch":
                    if (IsTruthy(value))
                        args.Add("--search");
                    break;

                // The rest are config.toml fields, reachable through -c. Names verified with
                // --strict-config, which rejects an unknown field instead of ignoring it.
                case "effort" when NotEmpty(value):
                    Override(args, "model_reasoning_effort", value!);
                    break;
                case "summary" when NotEmpty(value):
                    Override(args, "model_reasoning_summary", value!);
                    break;
                case "modelverbosity" when NotEmpty(value):
                    Override(args, "model_verbosity", value!);
                    break;
                case "modelprovider" when NotEmpty(value):
                    Override(args, "model_provider", value!);
                    break;
                case "personality" when NotEmpty(value):
                    Override(args, "personality", value!);
                    break;

                // There is no --no-project-doc; the CLI rejects it. Zeroing the budget is what
                // actually keeps AGENTS.md out of the prompt.
                case "noprojectdoc":
                    if (IsTruthy(value))
                        Override(args, "project_doc_max_bytes", "0");
                    break;

                default:
                    // collaborationMode is a turn-level app-server field with no config.toml
                    // equivalent, so only --launch can set it.
                    dropped.Add(key);
                    break;
            }
        }

        return (args, dropped);
    }

    private static void Override(List<string> args, string tomlKey, string value)
    {
        args.Add("--config");
        // The value is parsed as TOML and falls back to a literal string, so a bare word is fine
        // and a quoted one would arrive with its quotes.
        args.Add($"{tomlKey}={value}");
    }

    // ── Copilot ──────────────────────────────────────────────────────────

    private static (IReadOnlyList<string>, IReadOnlyList<string>) Copilot(Dictionary<string, string?> config)
    {
        var mapped = CopilotCliConfigMapper.ToCliArguments(CopilotCliConfigMapper.Map(config));

        // The engine's mapper is written for its own ACP launch and always includes these.
        // --acp in particular would start the protocol server instead of the interface a person
        // wants to look at, which is the opposite of attaching.
        mapped.Remove("--acp");
        mapped.Remove("--no-auto-update");

        return FromMap(mapped, config, CopilotUnmappable);
    }

    // ── shared ───────────────────────────────────────────────────────────

    private static (IReadOnlyList<string>, IReadOnlyList<string>) FromMap(
        Dictionary<string, string?> mapped,
        Dictionary<string, string?> config,
        IReadOnlySet<string> unmappable)
    {
        var args = new List<string>();
        foreach (var (flag, value) in mapped)
        {
            args.Add(flag);
            if (value is not null)
                args.Add(value);
        }

        // Judged by key rather than by whether a flag came out: a key whose value produced no
        // argument (an empty string, a false bool) got what it asked for.
        var dropped = config.Keys
            .Where(unmappable.Contains)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return (args, dropped);
    }

    /// <summary>Claude's mapper covers every key agentmove accepts for it.</summary>
    private static readonly HashSet<string> ClaudeUnmappable = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Copilot's likewise.</summary>
    private static readonly HashSet<string> CopilotUnmappable = new(StringComparer.OrdinalIgnoreCase);

    private static bool NotEmpty(string? value) => !string.IsNullOrEmpty(value);

    private static bool IsTruthy(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}
