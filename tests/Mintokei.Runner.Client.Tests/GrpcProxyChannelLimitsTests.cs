using System.Reflection;
using Grpc.Net.Client;
using Mintokei.Runner;
using Mintokei.Runner.Contracts;
using Xunit;

namespace Mintokei.Runner.Client.Tests;

/// <summary>
/// Regression guard for the 4 MiB gRPC default. A channel built without explicit limits silently caps
/// frames at 4 MiB; overflowing it kills the Task stream mid-handshake, which surfaces as the agent CLI
/// being SIGKILLed 30s later with no indication that a size cap was involved. Both construction paths in
/// <see cref="GrpcProxyChannel.ForAddress"/> — proxied and direct — must carry the raised limits.
/// </summary>
public class GrpcProxyChannelLimitsTests
{
    // GrpcChannel surfaces the resolved limits only as internal properties, so read them reflectively.
    // If a Grpc.Net.Client upgrade renames these, the null check below fails loudly rather than
    // silently passing a test that no longer verifies anything.
    private static int? Limit(GrpcChannel channel, string property)
    {
        var prop = typeof(GrpcChannel).GetProperty(
            property, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(prop);
        return (int?)prop!.GetValue(channel);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://broker:3128")]
    public void ForAddress_raises_the_message_size_limits(string? proxy)
    {
        using var channel = GrpcProxyChannel.ForAddress(
            "http://localhost:5191", proxy is null ? null : new Uri(proxy));

        Assert.Equal(RunnerGrpcLimits.MaxMessageSizeBytes, Limit(channel, "ReceiveMaxMessageSize"));
        Assert.Equal(RunnerGrpcLimits.MaxMessageSizeBytes, Limit(channel, "SendMaxMessageSize"));
    }

    [Fact]
    public void Limit_is_above_the_grpc_default()
    {
        const int grpcDefaultMaxReceiveBytes = 4 * 1024 * 1024;
        Assert.True(RunnerGrpcLimits.MaxMessageSizeBytes > grpcDefaultMaxReceiveBytes);
    }
}
