using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mintokei.AgentControlPlane;
using Mintokei.AgentEngine;
using Mintokei.AgentEngine.Acp;
using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.Claude;
using Mintokei.AgentEngine.Codex;
using Mintokei.AgentEngine.CommandRunner;
using Mintokei.AgentEngine.Contracts;

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};

var options = SampleOptions.Parse(args);
if (options.ShowHelp)
{
    PrintUsage();
    return 0;
}

var backend = CreateBackend(options.Tool);
var services = new ServiceCollection();
services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
services.AddSingleton<ICommandLineRunnerFactory, LocalCommandLineRunnerFactory>();
services.AddSingleton<IAgentBackend>(backend);
services.AddAgentControlPlane();

await using var provider = services.BuildServiceProvider();
var controlPlane = provider.GetRequiredService<IAgentControlPlane>();

controlPlane.SessionStarted += info =>
    Console.WriteLine($"[control-plane] started {Short(info.Key)} tool={info.Tool} machine={info.RunnerMachineId?.ToString() ?? "local"}");
controlPlane.SessionEnded += info =>
    Console.WriteLine($"[control-plane] ended {Short(info.Key)}");

var tasks = new List<Task<int>>();
for (var i = 1; i <= options.Count; i++)
{
    var key = Guid.NewGuid();
    var prompt = options.Count == 1
        ? options.Prompt
        : $"{options.Prompt}\n\nYou are local control-plane session {i} of {options.Count}.";

    var spec = new AgentSessionSpec
    {
        Tool = backend.Tool,
        WorkingDirectory = options.WorkingDirectory,
        Config = options.Config.Count == 0 ? null : options.Config,
        EnableMcp = false,
    };

    var sessionOptions = new AgentSessionOptions
    {
        InteractionMode = options.AutoApprove ? InteractionMode.AutoApprove : InteractionMode.Surface,
    };

    var session = await controlPlane.StartSessionAsync(
        key,
        spec,
        options: sessionOptions,
        ct: shutdown.Token);

    Console.WriteLine($"[control-plane] active sessions: {controlPlane.ListSessions().Count}");
    tasks.Add(RunTurnAsync(controlPlane, key, session, $"session-{i}", prompt, shutdown.Token));
}

var results = await Task.WhenAll(tasks);
Console.WriteLine($"[control-plane] active sessions after stop: {controlPlane.ListSessions().Count}");
return results.Any(code => code != 0) ? 1 : 0;

static async Task<int> RunTurnAsync(
    IAgentControlPlane controlPlane,
    Guid key,
    IAgentSession session,
    string label,
    string prompt,
    CancellationToken ct)
{
    try
    {
        var outputTask = PrintUntilTurnEndedAsync(session, label, ct);
        await session.SendMessageAsync(prompt, ct);
        return await outputTask;
    }
    finally
    {
        await controlPlane.StopSessionAsync(key);
    }
}

static IAgentBackend CreateBackend(string tool)
    => tool.ToLowerInvariant() switch
    {
        "claude" or "claude-code" => new ClaudeBackend(),
        "codex" => new CodexBackend(),
        "copilot" => new CopilotBackend(),
        "opencode" or "open-code" => new OpenCodeBackend(),
        _ => throw new ArgumentException(
            $"Unknown tool '{tool}'. Use claude, codex, copilot, or opencode.")
    };

static async Task<int> PrintUntilTurnEndedAsync(IAgentSession session, string label, CancellationToken ct)
{
    await foreach (var evt in session.Output.WithCancellation(ct))
    {
        switch (evt)
        {
            case MessageOutput message:
                PrintMessage(label, message.Message);
                break;

            case DeltaOutput { Payload: ContentDeltaPayload content }:
                Console.Write(content.Delta);
                break;

            case InteractionRequested interaction:
                await HandleInteractionAsync(session, interaction, label, ct);
                break;

            case TurnEnded turn:
                Console.WriteLine();
                if (turn.Failure is { } failure)
                {
                    Console.Error.WriteLine($"[{label}] turn failed: {failure.StatusLabel}");
                    if (!string.IsNullOrWhiteSpace(failure.Message))
                        Console.Error.WriteLine(failure.Message);
                    return 1;
                }
                Console.WriteLine($"[{label}] turn complete");
                return 0;
        }
    }

    Console.Error.WriteLine($"[{label}] output stream ended before the turn completed");
    return 1;
}

static void PrintMessage(string label, AgentMessage message)
{
    var text = message.Content;
    if (string.IsNullOrWhiteSpace(text) && message.ToolCall is { } tool)
        text = $"tool: {tool.ToolName}";
    if (string.IsNullOrWhiteSpace(text) && message.CommandExecution is { } command)
        text = $"command: {command.Command}";
    if (string.IsNullOrWhiteSpace(text))
        return;

    Console.WriteLine();
    Console.WriteLine($"[{label}] {message.Role}/{message.Type}: {text}");
}

static async Task HandleInteractionAsync(IAgentSession session, InteractionRequested request, string label, CancellationToken ct)
{
    var interaction = request.Message.UserInteraction;
    Console.WriteLine();
    Console.WriteLine($"[{label}] interaction requested");
    if (interaction?.ToolName is { Length: > 0 })
        Console.WriteLine($"tool: {interaction.ToolName}");
    if (interaction?.Command is { Length: > 0 })
        Console.WriteLine($"command: {interaction.Command}");
    if (interaction?.Reason is { Length: > 0 })
        Console.WriteLine($"reason: {interaction.Reason}");

    Console.Write($"[{label}] Decision [allow/deny, default deny]: ");
    var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
    var decision = answer is "allow" or "a" or "yes" or "y" ? "allow" : "deny";

    Console.Write($"[{label}] Optional message/answer: ");
    var message = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(message))
        message = null;

    await session.RespondAsync(
        request.RequestId,
        new UserInteractionResponse(decision, message, AnswersJson: null),
        ct);
}

static string Short(Guid value) => value.ToString("N")[..8];

static void PrintUsage()
{
    Console.WriteLine("""
    ControlPlaneLocal

    Runs one or more local coding-agent CLI sessions through Mintokei.AgentControlPlane.

    Usage:
      dotnet run --project samples/ControlPlaneLocal -- [options] [prompt]

    Options:
      --tool <name>       claude, codex, copilot, or opencode. Default: claude
      --dir <path>        Working directory for each CLI. Default: current directory
      --count <number>    Number of local sessions to start. Default: 1
      --prompt <text>     Prompt to send to each session.
      --config k=v        Add one backend config value. Can be repeated.
      --auto-approve      Auto-approve CLI permission requests.
      --help              Show this help.

    Example:
      dotnet run --project samples/ControlPlaneLocal -- --tool claude --count 2 --dir . "Summarise this repository in one sentence."
    """);
}

internal sealed record SampleOptions(
    string Tool,
    string WorkingDirectory,
    string Prompt,
    int Count,
    Dictionary<string, string?> Config,
    bool AutoApprove,
    bool ShowHelp)
{
    public static SampleOptions Parse(string[] args)
    {
        var tool = "claude";
        var dir = Environment.CurrentDirectory;
        var promptParts = new List<string>();
        var config = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var count = 1;
        var autoApprove = false;
        var showHelp = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help" or "-h":
                    showHelp = true;
                    break;
                case "--tool" when i + 1 < args.Length:
                    tool = args[++i];
                    break;
                case "--dir" when i + 1 < args.Length:
                    dir = Path.GetFullPath(args[++i]);
                    break;
                case "--count" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsed):
                    count = Math.Max(1, parsed);
                    i++;
                    break;
                case "--prompt" when i + 1 < args.Length:
                    promptParts.Add(args[++i]);
                    break;
                case "--config" when i + 1 < args.Length:
                    AddConfig(config, args[++i]);
                    break;
                case "--auto-approve":
                    autoApprove = true;
                    break;
                default:
                    promptParts.Add(args[i]);
                    break;
            }
        }

        var prompt = string.Join(' ', promptParts).Trim();
        if (string.IsNullOrWhiteSpace(prompt) && !showHelp)
            prompt = "Summarise this repository in one sentence.";

        return new SampleOptions(tool, dir, prompt, count, config, autoApprove, showHelp);
    }

    private static void AddConfig(Dictionary<string, string?> config, string raw)
    {
        var parts = raw.Split('=', 2);
        config[parts[0]] = parts.Length == 2 ? parts[1] : null;
    }
}
