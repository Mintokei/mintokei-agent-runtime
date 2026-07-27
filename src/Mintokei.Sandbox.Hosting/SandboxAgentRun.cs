using System.Text;
using Mintokei.AgentEngine;
using Mintokei.AgentEngine.Contracts;

namespace Mintokei.Sandbox.Hosting;

/// <summary>
/// A live agent session running inside its own sandbox. Consume <see cref="Output"/> (or the one-shot
/// <see cref="CollectTurnAsync"/>), then dispose: disposal stops the session and recycles the sandbox —
/// container, staged credentials and per-session broker included — so a run leaks nothing when it ends,
/// throws, or is cancelled.
/// </summary>
/// <remarks>Always <c>await using</c> this. The sandbox is a real container: if you drop the handle without
/// disposing, it keeps running (and keeps holding a capacity slot) until an external reaper removes it.</remarks>
public sealed class SandboxAgentRun : IAsyncDisposable
{
    private readonly Func<ValueTask> _cleanup;
    private int _disposed;

    internal SandboxAgentRun(IAgentSession session, Guid machineId, string sandboxName, Func<ValueTask> cleanup)
    {
        Session = session;
        MachineId = machineId;
        SandboxName = sandboxName;
        _cleanup = cleanup;
    }

    /// <summary>The underlying session — the same <see cref="IAgentSession"/> API as a non-sandboxed run, so
    /// anything written against it (interaction handling, resume, control requests) works unchanged.</summary>
    public IAgentSession Session { get; }

    /// <summary>Ephemeral machine identity of the sandbox's runner. Also the control-plane's handle for it.</summary>
    public Guid MachineId { get; }

    /// <summary>Name of the sandbox container/pod (useful for logs and for correlating with the backend).</summary>
    public string SandboxName { get; }

    /// <summary>The session's event stream, arriving over gRPC from inside the sandbox: transcript messages,
    /// deltas, turn boundaries, and permission prompts when the interaction mode surfaces them.</summary>
    public IAsyncEnumerable<AgentStreamOutput> Output => Session.Output;

    /// <summary>Send another message (a follow-up turn) into the running session.</summary>
    public Task SendMessageAsync(string content, CancellationToken ct = default) =>
        Session.SendMessageAsync(content, ct);

    /// <summary>
    /// Convenience for one-shot runs: consume <see cref="Output"/> until the current turn ends and return the
    /// transcript plus how the turn finished. Use <see cref="Output"/> directly when you need deltas, tool
    /// calls, or to answer permission prompts.
    /// </summary>
    public async Task<SandboxAgentTurn> CollectTurnAsync(CancellationToken ct = default)
    {
        var transcript = new StringBuilder();
        await foreach (var evt in Output.WithCancellation(ct))
        {
            switch (evt)
            {
                case MessageOutput { Message: var m } when !string.IsNullOrWhiteSpace(m.Content):
                    transcript.AppendLine($"[{m.Role}/{m.Type}] {m.Content}");
                    break;
                case TurnEnded turn:
                    return new SandboxAgentTurn(transcript.ToString(), turn.IsInterrupted, turn.Failure);
            }
        }

        // The stream ended without a turn boundary — the CLI died or the sandbox went away mid-turn.
        return new SandboxAgentTurn(transcript.ToString(), Interrupted: true, Failure: null);
    }

    /// <summary>Stop the session and recycle the sandbox. Idempotent; safe to call from a <c>finally</c>.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _cleanup();
    }
}

/// <summary>How one turn finished: what the agent said, and whether it completed, was interrupted, or failed.</summary>
public sealed record SandboxAgentTurn(string Transcript, bool Interrupted, TurnFailure? Failure)
{
    /// <summary>True when the turn ran to completion without a failure or an interrupt.</summary>
    public bool Succeeded => Failure is null && !Interrupted;
}
