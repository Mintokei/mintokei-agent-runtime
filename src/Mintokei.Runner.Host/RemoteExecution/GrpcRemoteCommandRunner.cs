using System.Text.Json;
using Mintokei.Runner.Contracts;
using Mintokei.Runner.Contracts.Messages;
using Mintokei.Runner.Host.RemoteExecution.Grpc;
using GrpcContracts = Mintokei.Runner.Contracts.Grpc;

namespace Mintokei.Runner.Host.RemoteExecution;

/// <summary>
/// Host-side <see cref="IRemoteCommandRunner"/> over the gRPC OpenQuery lane: correlate a request by id in
/// <see cref="PendingQueryStore"/>, send a <c>RunCommand</c> down the runner's open query stream
/// (<see cref="GrpcQueryChannelRegistry"/>), and await the runner's JSON reply. This is the reusable host
/// primitive the sandbox layers build on (e.g. staging credentials or dispatching <c>docker</c> to a worker).
/// </summary>
public sealed class GrpcRemoteCommandRunner(
    PendingQueryStore pendingQueryStore,
    GrpcQueryChannelRegistry grpcQueryChannels) : IRemoteCommandRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public async Task<RunCommandResponse> RunAsync(
        Guid machineId, string workingDirectory, string executable,
        IReadOnlyList<string> args, int timeoutMs, CancellationToken ct = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var timeout = TimeSpan.FromMilliseconds(timeoutMs) + TimeSpan.FromSeconds(5); // buffer over the command timeout
        var tcs = pendingQueryStore.Create(requestId, timeout);

        if (!grpcQueryChannels.IsOpen(machineId))
            throw new InvalidOperationException($"Runner machine {machineId} is not connected.");

        var msg = new GrpcContracts.QueryServerMessage
        {
            QueryId = requestId,
            RunCommand = new GrpcContracts.RunCommand
            {
                WorkingDirectory = workingDirectory,
                Executable = executable,
                Arguments = RunCommandArgs.Encode(args),
                TimeoutMs = timeoutMs,
            },
        };

        if (!await grpcQueryChannels.TrySendAsync(machineId, msg, ct))
            throw new InvalidOperationException($"Failed to send command to runner {machineId}.");

        var resultJson = await tcs.Task.WaitAsync(ct);
        return JsonSerializer.Deserialize<RunCommandResponse>(resultJson, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize command response.");
    }
}
