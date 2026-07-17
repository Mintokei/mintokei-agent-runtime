using System.Globalization;

namespace Mintokei.Sandbox.Docker;

/// <summary>
/// Pure translation of a <see cref="SandboxSpec"/> into <c>docker run</c> arguments. No I/O, so the
/// argv is unit-tested without a Docker daemon. Keeps the isolation runtime a single knob
/// (<c>--runtime</c>) so switching profiles (runc → runsc → kata-fc) never touches this shape.
/// </summary>
public static class DockerCommand
{
    /// <summary>Docker label applied to every sandbox container, so we can list/reconcile only ours.</summary>
    public const string ManagedLabel = "mintokei.sandbox";

    public static IReadOnlyList<string> BuildRunArgs(SandboxSpec spec)
    {
        var a = new List<string> { "run", "--detach", "--name", spec.Name };

        // Isolation runtime chosen by the profile. runc is Docker's default, but passing it is valid + explicit.
        a.Add("--runtime");
        a.Add(spec.RuntimeClass);

        // cgroup caps.
        a.Add("--memory");
        a.Add(spec.Limits.MemoryBytes.ToString(CultureInfo.InvariantCulture));
        a.Add("--cpus");
        a.Add(spec.Limits.Cpus.ToString(CultureInfo.InvariantCulture));
        a.Add("--pids-limit");
        a.Add(spec.Limits.PidsLimit.ToString(CultureInfo.InvariantCulture));

        // Phase-1 "standard" hardening posture.
        a.Add("--cap-drop");
        a.Add("ALL");
        a.Add("--security-opt");
        a.Add("no-new-privileges");

        // Marks the container as ours so ListManagedAsync can reconcile after a process restart.
        a.Add("--label");
        a.Add($"{ManagedLabel}=1");

        foreach (var target in spec.Tmpfs)
        {
            a.Add("--tmpfs");
            a.Add(target);
        }

        if (spec.AddHostGateway)
        {
            a.Add("--add-host");
            a.Add("host.docker.internal:host-gateway");
        }

        foreach (var m in spec.Mounts)
        {
            a.Add("--volume");
            a.Add($"{m.Source}:{m.Target}{(m.ReadOnly ? ":ro" : string.Empty)}");
        }

        foreach (var (key, value) in spec.Env)
        {
            a.Add("--env");
            a.Add($"{key}={value}");
        }

        // Proxy egress: force the container through an allowlisting HTTP CONNECT proxy.
        if (spec.Egress == SandboxEgress.Proxy && !string.IsNullOrWhiteSpace(spec.EgressProxyUrl))
        {
            a.Add("--env");
            a.Add($"HTTPS_PROXY={spec.EgressProxyUrl}");
            a.Add("--env");
            a.Add($"HTTP_PROXY={spec.EgressProxyUrl}");
        }

        a.Add(spec.Image);
        a.AddRange(spec.Args); // → container entrypoint (runner flags)
        return a;
    }
}
