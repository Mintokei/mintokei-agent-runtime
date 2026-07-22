namespace Mintokei.Sandbox.Docker;

/// <summary>
/// Pure translation of per-session sandbox network operations into <c>docker network</c> argv (no I/O, so
/// unit-tested without a daemon) — the deny-by-default egress primitive for <see cref="SandboxEgress.Broker"/>.
///
/// The network is created <c>--internal</c>: it has no NAT to the outside, so a container joined to it can
/// reach ONLY other containers on the same network — its per-session broker — and nothing else (an --internal
/// container cannot route to the internet even by raw IP). The broker is the single member that is ALSO
/// attached to a normal network, so the only way allowlisted traffic leaves is through it. This is real
/// network-layer enforcement, unlike the advisory <c>HTTP(S)_PROXY</c> env of <see cref="SandboxEgress.Proxy"/>.
/// </summary>
public static class DockerNetwork
{
    /// <summary>Per-session internal network name derived from the session name (parallels the container name).</summary>
    public static string Name(string sessionName) => $"mintokei-sbx-{Sanitize(sessionName)}";

    /// <summary><c>docker network create --internal --label mintokei.sandbox=1 &lt;name&gt;</c> — a NAT-less network
    /// whose members can reach each other (the broker) but not the outside. Labelled so it reconciles/GCs with
    /// the other sandbox resources.</summary>
    public static IReadOnlyList<string> CreateArgs(string name) =>
        ["network", "create", "--internal", "--label", $"{DockerCommand.ManagedLabel}=1", name];

    /// <summary><c>docker network rm &lt;name&gt;</c> — removed with the session (best-effort).</summary>
    public static IReadOnlyList<string> RemoveArgs(string name) => ["network", "rm", name];

    // A single name segment safe to interpolate into a network name — no separators, mirroring the credential
    // stager's sanitizer. Session names are already tame (e.g. "sandbox-standard-<hex>"); defence in depth.
    private static string Sanitize(string name)
    {
        var chars = name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        return chars.Length == 0 ? "session" : new string(chars);
    }
}
