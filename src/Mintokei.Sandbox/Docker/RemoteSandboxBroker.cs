using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mintokei.Runner.Contracts;

namespace Mintokei.Sandbox.Docker;

/// <summary>
/// <see cref="ISandboxBroker"/> over a remote worker's Docker (via <see cref="IRemoteCommandRunner"/>, the same
/// seam <see cref="RemoteDockerSandboxRuntime"/> uses): create the per-session <c>--internal</c> network, run
/// the broker image dual-homed (internal net + an egress network so only the broker reaches the outside), and
/// remove both on stop. Secrets are passed as container env — they live on the worker (held by the broker
/// process), never in the sandbox.
/// </summary>
public sealed class RemoteSandboxBroker(
    IRemoteCommandRunner commandRunner,
    IOptions<SandboxOptions> options,
    ILogger<RemoteSandboxBroker> logger) : ISandboxBroker
{
    private readonly SandboxOptions _options = options.Value;
    private readonly int _timeoutMs = Math.Max(10_000, options.Value.RemoteRunTimeoutSeconds * 1000);

    public async Task<BrokerEndpoint> StartAsync(Guid workerId, SandboxBrokerRequest request, CancellationToken ct = default)
    {
        var net = DockerNetwork.Name(request.SessionName);
        var container = BrokerContainerName(request.SessionName);

        // The BROKER_* env is backend-agnostic; the sandbox-facing URLs are container-name based here.
        var brokerEnv = BrokerEnvironment.Build(request);
        var modelUrls = brokerEnv.ModelPorts.ToDictionary(kv => kv.Key, kv => $"http://{container}:{kv.Value}");
        var githubApiUrl = brokerEnv.HasGitHub ? $"http://{container}:{BrokerEnvironment.GitHubPort}" : null;

        var endpoint = new BrokerEndpoint(
            net, container,
            ProxyUrl: $"http://{container}:3128",
            GitMintUrl: $"http://{container}:3129/git-credential",
            ModelUrls: modelUrls.Count > 0 ? modelUrls : null,
            GitHubApiUrl: githubApiUrl);

        var (exit, _, stderr) = await DockerAsync(workerId, DockerNetwork.CreateArgs(net), ct);
        if (exit != 0)
            throw new SandboxRuntimeException($"broker network create failed on worker {workerId}: {stderr.Trim()}");

        var run = new List<string>
        {
            "run", "--detach", "--name", container, "--network", net, "--label", $"{DockerCommand.ManagedLabel}=1",
        };
        foreach (var (k, v) in brokerEnv.Env)
            run.AddRange(["--env", $"{k}={v}"]);
        run.Add(_options.BrokerImage);

        (exit, _, stderr) = await DockerAsync(workerId, run, ct);
        if (exit != 0)
        {
            await StopAsync(workerId, endpoint, ct); // don't leave the network behind
            throw new SandboxRuntimeException($"broker container start failed on worker {workerId}: {stderr.Trim()}");
        }

        // Attach the broker (only) to an egress network so allowlisted traffic can leave; the sandbox itself
        // stays --internal-only, so its sole route out is the broker.
        (exit, _, stderr) = await DockerAsync(workerId, ["network", "connect", _options.BrokerEgressNetwork, container], ct);
        if (exit != 0)
        {
            await StopAsync(workerId, endpoint, ct);
            throw new SandboxRuntimeException($"broker egress attach failed on worker {workerId}: {stderr.Trim()}");
        }

        var extras = new List<string>();
        if (endpoint.ModelUrls is not null) extras.Add($"model-inject({string.Join(",", endpoint.ModelUrls.Keys)})");
        if (endpoint.GitHubApiUrl is not null) extras.Add("github-token");
        logger.LogInformation("broker {Container} up on worker {Worker} (net {Net}, {N} allow-rules{Extra})",
            container, workerId, net, request.EgressAllowlist.Count, extras.Count == 0 ? "" : ", " + string.Join(", ", extras));
        return endpoint;
    }

    public async Task StopAsync(Guid workerId, BrokerEndpoint endpoint, CancellationToken ct = default)
    {
        try { await DockerAsync(workerId, ["rm", "--force", endpoint.ContainerName], ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogDebug(ex, "broker rm failed"); }
        try { await DockerAsync(workerId, DockerNetwork.RemoveArgs(endpoint.NetworkName), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogDebug(ex, "broker network rm failed"); }
    }

    /// <summary>The broker container / DNS name the sandbox reaches on the internal network (session-derived).</summary>
    internal static string BrokerContainerName(string sessionName)
    {
        var seg = new string(sessionName.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
        return (seg.Length == 0 ? "session" : seg) + "-broker";
    }

    private async Task<(int Exit, string Stdout, string Stderr)> DockerAsync(Guid workerId, IReadOnlyList<string> argv, CancellationToken ct)
    {
        var r = await commandRunner.RunAsync(workerId, "/tmp", "docker", argv, _timeoutMs, ct);
        return (r.ExitCode, r.Stdout ?? string.Empty, r.Stderr ?? string.Empty);
    }
}
