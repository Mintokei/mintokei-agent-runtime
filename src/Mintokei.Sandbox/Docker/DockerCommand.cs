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
        // Fail closed: broker egress must join a per-session --internal network (deny-by-default; its only exit
        // is the session broker). Until that network is provisioned (spec.NetworkName set by the manager),
        // refuse — an unenforced "brokered" container would have open network and no creds.
        if (spec.Egress == SandboxEgress.Broker && string.IsNullOrWhiteSpace(spec.NetworkName))
            throw new SandboxRuntimeException(
                "broker egress is configured but no per-session broker network is provisioned — refusing to launch (fail-closed).");

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

        // Broker egress: join the per-session --internal network. That network is the ENFORCEMENT — a process
        // that ignores the proxy env below still has no route off it; its only reachable peer is the broker.
        if (spec.Egress == SandboxEgress.Broker)
        {
            a.Add("--network");
            a.Add(spec.NetworkName!);
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

        // Route egress through an allowlisting HTTP CONNECT proxy. In Proxy mode this is advisory (env only); in
        // Broker mode the --internal network above makes it enforced — the proxy is simply how allowed traffic
        // actually leaves (via the session broker), since there is no other route out.
        if (spec.Egress is SandboxEgress.Proxy or SandboxEgress.Broker && !string.IsNullOrWhiteSpace(spec.EgressProxyUrl))
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
