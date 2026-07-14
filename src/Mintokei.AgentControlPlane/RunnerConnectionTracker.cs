using System.Collections.Concurrent;

namespace Mintokei.AgentControlPlane;

/// <summary>
/// Tracks which runner machines are currently connected and their SignalR connection IDs.
/// </summary>
public sealed class RunnerConnectionTracker
{
    private readonly ConcurrentDictionary<Guid, string> _machineToConnection = new();
    private readonly ConcurrentDictionary<string, Guid> _connectionToMachine = new();

    public void Register(Guid machineId, string connectionId)
    {
        // Remove old connection if exists
        if (_machineToConnection.TryRemove(machineId, out var oldConnectionId))
            _connectionToMachine.TryRemove(oldConnectionId, out _);

        _machineToConnection[machineId] = connectionId;
        _connectionToMachine[connectionId] = machineId;
    }

    public void Unregister(Guid machineId)
    {
        if (_machineToConnection.TryRemove(machineId, out var connectionId))
            _connectionToMachine.TryRemove(connectionId, out _);
    }

    public void UnregisterByConnection(string connectionId)
    {
        if (_connectionToMachine.TryRemove(connectionId, out var machineId))
            _machineToConnection.TryRemove(machineId, out _);
    }

    public string? GetConnectionId(Guid machineId) =>
        _machineToConnection.TryGetValue(machineId, out var id) ? id : null;

    public Guid? GetMachineId(string connectionId) =>
        _connectionToMachine.TryGetValue(connectionId, out var id) ? id : null;

    public bool IsConnected(Guid machineId) => _machineToConnection.ContainsKey(machineId);

    /// <summary>Snapshot of the machine ids currently connected. DB-free — the live registry only.</summary>
    public IReadOnlyCollection<Guid> ConnectedMachineIds => [.. _machineToConnection.Keys];
}
