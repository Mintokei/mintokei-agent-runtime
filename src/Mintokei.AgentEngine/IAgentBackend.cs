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
}
