namespace Mintokei.Sandbox;

/// <summary>Thrown when a session may not join a sandbox because the sandbox was not built to serve its tool.</summary>
public sealed class SandboxAdmissionException(string message) : InvalidOperationException(message);

/// <summary>
/// The rule that decides whether a session may join an EXISTING sandbox.
///
/// It exists because sharing a sandbox shares more than the working tree. A sandbox has one broker on one
/// network, and the broker cannot attribute a connection to a session — so its egress allowlist and the model
/// credentials it injects apply to every session inside. Admitting a tool the sandbox was not provisioned for
/// therefore hands EVERY session in it that tool's network reach and credentials, silently and for the
/// sandbox's whole life. The allowlist is fixed when the broker starts, so this cannot be corrected afterwards
/// without recycling the sandbox.
///
/// Enforced here, in the library, rather than left to the caller: a sandbox refusing to serve a session it
/// cannot safely serve is the same class of guarantee as <c>RemoteSandboxManager</c> refusing to launch a
/// broker-egress profile with no broker registered. A caller may pre-check to give a better message, but a
/// caller that forgets — or a code path added later — must not be able to route around it.
/// </summary>
public static class SandboxAdmission
{
    /// <summary>
    /// True when a sandbox declaring <paramref name="admittedTools"/> may serve a session using
    /// <paramref name="requestedTool"/>. An empty declaration means the sandbox is unconstrained (the
    /// single-session case, where there is nothing to admit).
    /// </summary>
    public static bool Admits(IReadOnlyList<string> admittedTools, string requestedTool)
    {
        if (admittedTools is not { Count: > 0 })
            return true;

        // A blank request cannot be matched against a constrained sandbox, and must not pass by accident.
        if (string.IsNullOrWhiteSpace(requestedTool))
            return false;

        foreach (var tool in admittedTools)
        {
            if (string.Equals(tool, requestedTool, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Throw unless the sandbox admits the tool. The message names the sandbox, what it serves and what was
    /// asked for, because the actionable answer ("provision a sandbox for that tool, or add it to this one and
    /// recycle") depends on all three.
    /// </summary>
    public static void EnsureAdmits(string sandboxName, IReadOnlyList<string> admittedTools, string requestedTool)
    {
        if (Admits(admittedTools, requestedTool))
            return;

        throw new SandboxAdmissionException(
            $"sandbox '{sandboxName}' serves [{string.Join(", ", admittedTools)}] and cannot host a "
            + $"'{(string.IsNullOrWhiteSpace(requestedTool) ? "<none>" : requestedTool)}' session — its egress "
            + "allowlist and injected credentials are fixed for every session inside it, so admitting another "
            + "tool would widen them all. Provision a separate sandbox, or declare the tool and recycle this one.");
    }
}
