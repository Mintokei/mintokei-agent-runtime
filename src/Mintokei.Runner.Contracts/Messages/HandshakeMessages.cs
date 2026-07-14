namespace Mintokei.Runner.Contracts.Messages;

/// <summary>
/// Sent by the runner immediately after connecting to establish/restore state.
/// <see cref="FileServerPort"/> is the loopback port of the runner's in-process
/// file server (used by the API to forward media-preview requests via the
/// tunnel). 0 when the runner build doesn't have a file server.
/// </summary>
public sealed record HandshakeRequest(
    long LastAckedOutboundSequence,
    long NextInboundSequence,
    List<Guid>? ActiveCorrelationIds = null,
    int FileServerPort = 0);

/// <summary>
/// Backend response to the runner's handshake, providing state sync info.
/// </summary>
public sealed record HandshakeResponse(
    Guid MachineId,
    long LastReceivedInboundSequence,
    bool Success,
    string? Error,
    List<CliProbeSpec>? CliProbes = null);

/// <summary>
/// Describes a single CLI the runner should probe to detect installation + version.
/// </summary>
public sealed record CliProbeSpec(
    string AgentToolKey,
    string BinaryName,
    string VersionArgs,
    string? VersionRegex);

/// <summary>
/// A single CLI installation reported by the runner after probing.
/// </summary>
public sealed record InstalledCli(
    string AgentToolKey,
    string Version,
    List<InstalledCliModel>? Models = null);

/// <summary>
/// A single model the runner discovered as available for a CLI installation.
/// </summary>
public sealed record InstalledCliModel(
    string ModelId,
    string? DisplayName,
    bool IsDefault);
