using k8s;
using Microsoft.Extensions.DependencyInjection;
using Mintokei.Sandbox;
using Xunit;

namespace Mintokei.Sandbox.Tests;

/// <summary>
/// The cluster the k8s backend targets is config-driven (so sandbox Pods can run on a separate/dedicated
/// cluster). These cover the deterministic paths — explicit server+token and a kubeconfig file — without
/// needing an ambient cluster. The default (all-unset) path is in-cluster-or-ambient and env-dependent, so
/// it isn't asserted here.
/// </summary>
public class KubernetesClientConfigTests
{
    [Fact]
    public void Explicit_api_server_and_token_are_used()
    {
        var cfg = MintokeiSandboxServiceCollectionExtensions.BuildKubernetesConfig(new SandboxOptions
        {
            KubernetesApiServerUrl = "https://sandbox.example:6443",
            KubernetesToken = "tok-123",
            KubernetesSkipTlsVerify = true,
        });

        Assert.Equal("https://sandbox.example:6443", cfg.Host);
        Assert.Equal("tok-123", cfg.AccessToken);
        Assert.True(cfg.SkipTlsVerify);
    }

    [Fact]
    public void Kubeconfig_file_targets_its_server()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, MinimalKubeconfig("https://remote.example:6443"));

            var cfg = MintokeiSandboxServiceCollectionExtensions.BuildKubernetesConfig(new SandboxOptions
            {
                KubernetesKubeconfig = path,
            });

            Assert.Equal("https://remote.example:6443", cfg.Host);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Explicit_server_takes_precedence_over_kubeconfig()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, MinimalKubeconfig("https://from-file:6443"));

            var cfg = MintokeiSandboxServiceCollectionExtensions.BuildKubernetesConfig(new SandboxOptions
            {
                KubernetesApiServerUrl = "https://explicit:6443",
                KubernetesToken = "t",
                KubernetesKubeconfig = path,
            });

            Assert.Equal("https://explicit:6443", cfg.Host);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string MinimalKubeconfig(string server) => $"""
        apiVersion: v1
        kind: Config
        current-context: ctx
        clusters:
        - name: c
          cluster:
            server: {server}
            insecure-skip-tls-verify: true
        contexts:
        - name: ctx
          context:
            cluster: c
            user: u
        users:
        - name: u
          user:
            token: abc
        """;
}
