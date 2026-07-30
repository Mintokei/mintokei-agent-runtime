using System.Reflection;
using Mintokei.Sandbox.Docker;
using Xunit;

namespace Mintokei.Sandbox.Tests;

/// <summary>
/// Every sandbox backend must implement every optional capability, or be listed in <see cref="Exempt"/> with
/// a reason.
///
/// This exists because three capability gaps reached production, all the same shape: a capability was added
/// to one backend, never added to another, and nothing noticed — it compiled, CI passed, and the failure
/// surfaced far from its cause (a container dying in its entrypoint, a session unable to join a sandbox, a
/// working tree that did not survive a recycle). See <c>docs/sandbox-backend-capabilities.md</c>.
///
/// The failure mode was never a wrong decision — it was NO decision. So this test does not ask "is this
/// backend complete?", which nobody can answer in the abstract; it forces someone to write a line saying
/// either "implemented" or "not supported, because X". Adding a capability interface breaks every backend
/// until each has been considered, which is the whole point.
///
/// WHAT THIS CANNOT CATCH: capabilities that are steps inside a provision path rather than interfaces —
/// credential staging (the first of the three gaps) and broker egress are both invisible to a type check.
/// Those need behavioural conformance tests per backend. Reaching for an interface when adding a capability
/// is what keeps it inside this net.
/// </summary>
public class BackendCapabilityParityTests
{
    /// <summary>Optional capabilities layered on <see cref="ISandboxRuntime"/>. Add new ones here.</summary>
    private static readonly Type[] Capabilities =
    [
        typeof(ISandboxLogSource),
        typeof(ISandboxAdmissionSource),
        typeof(ISandboxWorkspaceStore),
        typeof(ISandboxCredentialSweeper),
    ];

    /// <summary>
    /// Backends that deliberately do NOT implement a capability, and why. An entry here is a decision on the
    /// record — it must also fail closed at runtime rather than silently accepting input it cannot honour.
    /// </summary>
    private static readonly Dictionary<(Type Backend, Type Capability), string> Exempt = new()
    {
        [(typeof(Kubernetes.KubernetesSandboxRuntime), typeof(ISandboxCredentialSweeper))] =
            "Nothing to sweep: the Kubernetes backend stages credentials in an init container into the Pod's own "
            + "emptyDir, so the copy is bounded by the Pod's lifetime and is destroyed with it. There is no "
            + "host-level staging root that can outlive a session, which is the leak this capability collects. "
            + "Fails closed trivially — the copy cannot survive the thing that owns it.",
    };

    public static TheoryData<Type, Type> BackendCapabilityPairs()
    {
        var data = new TheoryData<Type, Type>();
        foreach (var backend in Backends())
            foreach (var capability in Capabilities)
                data.Add(backend, capability);
        return data;
    }

    [Theory]
    [MemberData(nameof(BackendCapabilityPairs))]
    public void Every_backend_implements_every_capability_or_is_exempt_with_a_reason(Type backend, Type capability)
    {
        if (Exempt.TryGetValue((backend, capability), out var reason))
        {
            Assert.False(string.IsNullOrWhiteSpace(reason),
                $"{backend.Name} is exempt from {capability.Name} but gives no reason.");
            return;
        }

        Assert.True(capability.IsAssignableFrom(backend),
            $"{backend.Name} does not implement {capability.Name}. Implement it, or add an entry to "
            + $"{nameof(Exempt)} explaining why this backend cannot — and make sure it FAILS CLOSED rather "
            + "than accepting input it silently ignores. See docs/sandbox-backend-capabilities.md.");
    }

    /// <summary>
    /// The test is only worth anything if it sees every backend, so assert the discovery itself. A backend
    /// added to the library and not picked up here would be exempt from the whole check by accident — the
    /// same class of silent omission this file exists to prevent.
    /// </summary>
    [Fact]
    public void All_known_backends_are_discovered()
    {
        var discovered = Backends().Select(t => t.Name).ToHashSet();

        Assert.Contains(nameof(DockerSandboxRuntime), discovered);
        Assert.Contains(nameof(Kubernetes.KubernetesSandboxRuntime), discovered);
        // The nested path is machine-targeted and not itself an ISandboxRuntime; this is the bound view that
        // brings it under the same seam, and therefore under this test.
        Assert.Contains(nameof(WorkerBoundSandboxRuntime), discovered);
    }

    private static List<Type> Backends() =>
    [
        .. typeof(ISandboxRuntime).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ISandboxRuntime).IsAssignableFrom(t))
            .OrderBy(t => t.Name, StringComparer.Ordinal),
    ];
}
