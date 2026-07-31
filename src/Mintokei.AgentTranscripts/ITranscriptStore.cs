using Mintokei.AgentEngine.AgentTools;

namespace Mintokei.AgentTranscripts;

/// <summary>
/// Read and write one CLI's on-disk session store.
///
/// The counterpart to <see cref="Mintokei.AgentEngine.IAgentBackend"/>: that one knows how to
/// <em>launch and talk to</em> a CLI, this one knows how to <em>read and write the transcripts it
/// leaves behind</em>. Both are keyed by <see cref="Tool"/> so a caller can resolve either from a
/// tool key, and neither knows about the embedder's database.
///
/// Converting a session between CLIs is then just:
/// <code>
/// var session = await stores[AgentToolKey.ClaudeCodeCli].ReadAsync(id, ct);
/// var newId   = await stores[AgentToolKey.CodexCli].WriteAsync(session, opts, ct);
/// // the target CLI's own --resume now finds it
/// </code>
///
/// Implementations must be safe to use concurrently for reads. Writes create a NEW session each
/// time and never mutate one the caller did not ask for, so two concurrent writes cannot corrupt
/// each other — but a store whose index is a shared SQLite file will serialize on that file.
/// </summary>
public interface ITranscriptStore
{
    /// <summary>Which CLI this store belongs to.</summary>
    AgentToolKey Tool { get; }

    /// <summary>
    /// Enumerate known sessions, newest first, without parsing full transcripts.
    /// </summary>
    /// <param name="cwd">Restrict to sessions filed under this working directory; null for all.</param>
    /// <param name="ct">Cancellation token.</param>
    IAsyncEnumerable<StoredTranscriptInfo> ListAsync(string? cwd = null, CancellationToken ct = default);

    /// <summary>
    /// Read one session, or null when the store has no such id.
    /// </summary>
    /// <exception cref="TranscriptStoreException">
    /// The session exists but could not be parsed — a truncated write, or a schema this version
    /// does not understand. Deliberately not swallowed: silently returning a partial transcript
    /// would let a caller convert it and quietly lose the rest of the conversation.
    /// </exception>
    Task<StoredTranscript?> ReadAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Write <paramref name="session"/> as a NEW session in this store and return its id, which the
    /// CLI's own resume flow will accept.
    /// </summary>
    /// <exception cref="TranscriptStoreException">The store is in a shape this version will not write to.</exception>
    Task<string> WriteAsync(
        StoredTranscript session, TranscriptWriteOptions? options = null, CancellationToken ct = default);
}

/// <summary>
/// A session store could not be read or written.
///
/// These formats are undocumented and versioned, so the failure mode that matters is writing
/// something a CLI will later choke on — or worse, silently mis-read. Implementations throw this
/// rather than guessing.
/// </summary>
public sealed class TranscriptStoreException : Exception
{
    public TranscriptStoreException(string message) : base(message) { }
    public TranscriptStoreException(string message, Exception inner) : base(message, inner) { }
}
