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

        // Opt-in: read-only container rootfs (the writable paths are served by the tmpfs set below / real mounts).
        if (spec.ReadOnlyRootfs)
            a.Add("--read-only");

        // Marks the container as ours so ListManagedAsync can reconcile after a process restart.
        a.Add("--label");
        a.Add($"{ManagedLabel}=1");

        // Scratch mounts (e.g. the runner data dir /data). Docker mounts a tmpfs 0755 root:root by default,
        // which the non-root agent user cannot write — pin ownership to the sandbox uid so --data-dir works. Skip
        // any target that is also a real mount (e.g. the persisted /repos volume): that already makes it writable,
        // and Docker rejects a tmpfs + volume at the same path.
        var mountTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in spec.Mounts)
            mountTargets.Add(m.Target);
        foreach (var target in spec.Tmpfs)
        {
            if (mountTargets.Contains(target))
                continue;
            a.Add("--tmpfs");
            a.Add($"{target}:uid={SandboxImage.AgentUid},gid={SandboxImage.AgentUid},mode=0700");
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
