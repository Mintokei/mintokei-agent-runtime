using System.Globalization;

namespace Mintokei.Sandbox;

/// <summary>
/// Computes the broker container's <c>BROKER_*</c> environment from a <see cref="SandboxBrokerRequest"/> —
/// backend-agnostic, so the Docker (<see cref="Docker.RemoteSandboxBroker"/>) and Kubernetes
/// (<see cref="Kubernetes.KubernetesSandboxBroker"/>) brokers hand the SAME contract to the broker image. Each
/// backend builds the sandbox-facing URLs itself (Docker uses the container name, K8s the Service DNS name) from
/// <see cref="Result.ModelPorts"/> / <see cref="Result.HasGitHub"/>.
/// </summary>
public static class BrokerEnvironment
{
    /// <summary>The broker's GitHub-token mint port (fixed; Copilot's GitHub API is pointed here).</summary>
    public const int GitHubPort = 3132;

    /// <param name="Env">The <c>BROKER_*</c> env pairs to hand the broker container.</param>
    /// <param name="ModelPorts">Configured model providers → their reverse-proxy port (to build base URLs).</param>
    /// <param name="HasGitHub">Whether a GitHub token was minted (build the github API URL).</param>
    public sealed record Result(
        IReadOnlyList<KeyValuePair<string, string>> Env,
        IReadOnlyList<KeyValuePair<string, int>> ModelPorts,
        bool HasGitHub);

    public static Result Build(SandboxBrokerRequest request)
    {
        var env = new List<KeyValuePair<string, string>>
        {
            new("BROKER_ALLOW", string.Join(',', request.EgressAllowlist)),
        };
        var s = request.Secrets;
        if (!string.IsNullOrWhiteSpace(s?.GitCredentials))
            env.Add(new("BROKER_GIT_CREDS", s!.GitCredentials));

        var modelPorts = new List<KeyValuePair<string, int>>();
        foreach (var m in s?.EffectiveModelUpstreams ?? [])
        {
            if (ModelProviders.Find(m.Provider) is not { } p) continue; // unknown provider name → skip
            var key = p.Name.ToUpperInvariant();
            env.Add(new($"BROKER_MODEL_{key}_UPSTREAM", m.Upstream));
            env.Add(new($"BROKER_MODEL_{key}_PORT", p.Port.ToString(CultureInfo.InvariantCulture)));
            if (!string.IsNullOrWhiteSpace(m.Auth))
                env.Add(new($"BROKER_MODEL_{key}_AUTH", m.Auth!));
            modelPorts.Add(new(p.Name, p.Port));
        }

        var hasGitHub = !string.IsNullOrWhiteSpace(s?.GitHubToken);
        if (hasGitHub)
        {
            env.Add(new("BROKER_GITHUB_TOKEN", s!.GitHubToken!));
            env.Add(new("BROKER_GITHUB_PORT", GitHubPort.ToString(CultureInfo.InvariantCulture)));
        }

        return new Result(env, modelPorts, hasGitHub);
    }
}
