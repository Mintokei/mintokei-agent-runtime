namespace Mintokei.Runner.Contracts;

/// <summary>
/// Message-size limits for the runner &lt;-&gt; backend gRPC Task/Control streams.
///
/// Both gRPC stacks default to a 4 MiB receive cap, which is far too small for the frames a CLI
/// can emit. Resuming a long-lived agent thread replays its whole transcript in one go, so a single
/// frame scales with the session's history rather than with anything we control. When a frame crosses
/// the cap the peer raises <c>ResourceExhausted ("Received message exceeds the maximum configured
/// message size.")</c> and <c>GrpcTaskStreamManager</c> tears the stream down — after which the
/// in-flight handshake can never get its reply, so it burns its 30s timeout and the CLI is SIGKILLed.
/// The failure looks like "the agent process died" and gives no hint that a size cap caused it.
///
/// Both ends must agree, so keep this the single source of truth: it is referenced by the runner
/// client channel, the runner host's <c>AddGrpc</c>, and the API's <c>AddGrpc</c>.
/// </summary>
public static class RunnerGrpcLimits
{
    /// <summary>
    /// Max send/receive size, in bytes, for runner gRPC streams (64 MiB).
    ///
    /// Chosen as headroom over the largest transcript observed in practice (~18 MiB) rather than as a
    /// measured ceiling. It is a backstop against a runaway frame, not a budget anyone should plan to
    /// spend — the durable fix for very large sessions is to chunk the payload, not to keep raising this.
    /// </summary>
    public const int MaxMessageSizeBytes = 64 * 1024 * 1024;
}
