using Microsoft.Extensions.Logging;
using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.CommandRunner;

using Mintokei.AgentEngine.Contracts;

namespace Mintokei.AgentEngine;

/// <summary>
/// One module per backend — the single place that knows both how to <em>launch</em> a CLI and how to
/// <em>talk</em> to it. Unifies what was split three ways: the launch args (the execution service's
/// <c>BuildCliOptions</c>), the wire protocol (<see cref="IAgentSessionProtocol"/>), and the reply
/// serializer. Keyed by <see cref="Tool"/> so the launcher can pick one from a spec.
/// </summary>
public interface IAgentBackend
{
    AgentToolKey Tool { get; }

    /// <summary>Builds the exact CLI invocation from a DB-free spec. Pure — reads only the spec.</summary>
    CommandLineOptions BuildCommandLine(AgentSessionSpec spec);

    /// <summary>Creates the wire protocol for a running session of this backend.</summary>
    IAgentSessionProtocol CreateProtocol(AgentSessionSpec spec, ILogger logger);

    /// <summary>The backend's interaction reply serializer (permission/question answers).</summary>
    IInteractionReplyBuilder ReplyBuilder { get; }

    /// <summary>
    /// True when <paramref name="stderr"/> shows the CLI refused to start because the session it was
    /// asked to resume no longer exists on disk — the transcript was reclaimed (GC'd workspace volume,
    /// the CLI's own retention sweep) while the caller's stored session id lived on.
    ///
    /// This is a DETERMINISTIC failure: the same launch will fail identically forever, so a caller that
    /// retries on process death needs to tell it apart from a transient crash and stop, rather than burn
    /// its retry budget and report a misleading "the runner was unreachable".
    ///
    /// Defaults to false — a backend that can't recognise its own flavour of this must not guess, since a
    /// false positive silently discards a resumable session.
    /// </summary>
    bool IsSessionNotFoundError(string stderr) => false;
}
