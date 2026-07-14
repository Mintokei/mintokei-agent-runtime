using System.Collections.Concurrent;

namespace Mintokei.Runner.Host.RemoteExecution;

/// <summary>
/// In-memory map of <c>machineId → file-server port</c> reported by each
/// connected runner during handshake. Used by the media-preview endpoint to
/// build the tunnel URL that streams a workspace file from the runner's
/// loopback file server.
///
/// Lives only as long as the API process — runners re-report their port on
/// every handshake, including after reconnect.
/// </summary>
public sealed class RunnerFileServerPortStore
{
    private readonly ConcurrentDictionary<Guid, int> _ports = new();

    public void Register(Guid machineId, int port)
    {
        if (port <= 0)
        {
            _ports.TryRemove(machineId, out _);
            return;
        }
        _ports[machineId] = port;
    }

    public int? GetPort(Guid machineId) =>
        _ports.TryGetValue(machineId, out var port) ? port : null;

    public void Unregister(Guid machineId) => _ports.TryRemove(machineId, out _);
}
