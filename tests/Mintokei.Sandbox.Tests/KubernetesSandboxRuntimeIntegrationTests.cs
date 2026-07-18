using k8s;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mintokei.Sandbox;
using Mintokei.Sandbox.Kubernetes;
using Xunit;

namespace Mintokei.Sandbox.Tests;

/// <summary>
/// Exercises <see cref="KubernetesSandboxRuntime"/> against a REAL cluster (provision → status → list →
/// stop) using the ambient kubeconfig. Opt-in only — skipped unless <c>MINTOKEI_SANDBOX_K8S_ITEST=1</c> and
/// a kubeconfig loads — so normal CI never runs it. This is what proves the actual API calls + phase/exit
/// mapping, not just that the pod-spec builder produces the right manifest.
/// Env: <c>MINTOKEI_SANDBOX_K8S_ITEST_NAMESPACE</c> (default "default"),
/// <c>MINTOKEI_SANDBOX_K8S_ITEST_IMAGE</c> (default "busybox:1.36").
/// </summary>
public class KubernetesSandboxRuntimeIntegrationTests
{
    [Fact]
    public async Task Provision_status_list_stop_against_real_cluster()
    {
        if (!ClusterAvailableAndOptedIn(out var client, out var ns, out var reason))
            Assert.Skip(reason);

        var options = Options.Create(new SandboxOptions { KubernetesNamespace = ns });
        var runtime = new KubernetesSandboxRuntime(client, options, NullLogger<KubernetesSandboxRuntime>.Instance);

        var image = Environment.GetEnvironmentVariable("MINTOKEI_SANDBOX_K8S_ITEST_IMAGE") ?? "busybox:1.36";
        var spec = new SandboxSpec
        {
            Image = image,
            Name = $"mk-itest-{Guid.NewGuid():N}"[..24], // DNS-1123 label (lowercase hex + hyphen)
            RuntimeClass = "runc",
            Limits = new SandboxResourceLimits(128L * 1024 * 1024, 0.25, 128), // modest, so it schedules on a small node
            Tmpfs = [],
            Args = ["sleep", "60"],
        };

        SandboxHandle? handle = null;
        try
        {
            handle = await runtime.ProvisionAsync(spec);
            Assert.Equal(spec.Name, handle.Name);
            Assert.Equal("kubernetes", handle.Backend);

            // Pod reaches Running once the (first-time) image pull completes.
            Assert.Equal(SandboxState.Running, await WaitForStateAsync(runtime, handle, SandboxState.Running, TimeSpan.FromSeconds(90)));
            Assert.Contains(await runtime.ListManagedAsync(), h => h.Name == spec.Name); // labelled + listed

            await runtime.StopAsync(handle);
            Assert.Equal(SandboxState.NotFound, await WaitForStateAsync(runtime, handle, SandboxState.NotFound, TimeSpan.FromSeconds(60)));
            handle = null;
        }
        finally
        {
            if (handle is not null)
                await runtime.StopAsync(handle); // best-effort cleanup if an assert failed
        }
    }

    private static async Task<SandboxState> WaitForStateAsync(
        KubernetesSandboxRuntime runtime, SandboxHandle handle, SandboxState target, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        SandboxState last;
        do
        {
            last = (await runtime.GetStatusAsync(handle)).State;
            if (last == target)
                return last;
            await Task.Delay(1000);
        }
        while (DateTime.UtcNow < deadline);
        return last;
    }

    private static bool ClusterAvailableAndOptedIn(out IKubernetes client, out string ns, out string reason)
    {
        client = null!;
        ns = Environment.GetEnvironmentVariable("MINTOKEI_SANDBOX_K8S_ITEST_NAMESPACE") ?? "default";

        if (Environment.GetEnvironmentVariable("MINTOKEI_SANDBOX_K8S_ITEST") != "1")
        {
            reason = "opt-in only: set MINTOKEI_SANDBOX_K8S_ITEST=1 to run the real-cluster test";
            return false;
        }

        try
        {
            client = new k8s.Kubernetes(KubernetesClientConfiguration.BuildDefaultConfig());
            reason = "";
            return true;
        }
        catch (Exception ex)
        {
            reason = $"no usable kubeconfig: {ex.Message}";
            return false;
        }
    }
}
