namespace Mintokei.AgentEngine.AgentTools;

/// <summary>
/// Thrown when a launch died because the session it was asked to resume no longer exists on disk —
/// the transcript was reclaimed (GC'd workspace, the CLI's own retention sweep) while the caller's
/// stored session id lived on.
///
/// It is deliberately NOT an <see cref="AgentStreamEndedException"/>, even though that is how the
/// death physically presents (the CLI exits mid-handshake and its stream ends). Retry policies key
/// off the exception type, and this failure is DETERMINISTIC: the identical launch cannot succeed on
/// attempt 2, or 15. Treating it as a transient stream-end makes every retry layer rebuild the same
/// doomed command before reporting a cause that never happened.
/// </summary>
public sealed class UnresumableSessionException(string message) : InvalidOperationException(message);
