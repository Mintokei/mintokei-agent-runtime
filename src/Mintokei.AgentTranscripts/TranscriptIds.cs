using System.Security.Cryptography;
using System.Text;

namespace Mintokei.AgentTranscripts;

/// <summary>
/// Deterministic ids for messages read out of a store.
///
/// <see cref="Mintokei.AgentEngine.Contracts.AgentMessage"/> carries <c>Id</c> and
/// <c>AgentTaskId</c> Guids because a live session mints them as frames arrive. A file reader has
/// no such moment — the transcript already exists — so it derives them from the CLI's own ids
/// instead of calling <see cref="Guid.NewGuid"/>.
///
/// That matters more than it looks: reading the same file twice must produce the same Guids, or
/// every re-read looks like a brand-new set of messages to anything downstream that dedupes,
/// diffs, or resumes an interrupted import.
/// </summary>
public static class TranscriptIds
{
    // Fixed namespace for this library's derived ids. Arbitrary but stable — changing it would
    // renumber every message every consumer has already seen.
    private static readonly Guid Namespace = new("6f2a1d54-9c3e-4b77-8f21-2d5c0a9e7b13");

    /// <summary>
    /// RFC 4122 §4.3 name-based (SHA-1) UUID. Same inputs always yield the same Guid.
    /// </summary>
    public static Guid Derive(params string?[] parts)
    {
        // Unit separator between parts so ("ab","c") and ("a","bc") cannot collide.
        var name = string.Join('\u001F', parts.Select(p => p ?? string.Empty));
        Span<byte> ns = stackalloc byte[16];
        WriteGuidBigEndian(Namespace, ns);

        var nameBytes = Encoding.UTF8.GetBytes(name);
        Span<byte> buffer = new byte[16 + nameBytes.Length];
        ns.CopyTo(buffer);
        nameBytes.CopyTo(buffer[16..]);

        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(buffer, hash);

        Span<byte> result = stackalloc byte[16];
        hash[..16].CopyTo(result);
        result[6] = (byte)((result[6] & 0x0F) | 0x50);   // version 5
        result[8] = (byte)((result[8] & 0x3F) | 0x80);   // RFC 4122 variant
        return ReadGuidBigEndian(result);
    }

    /// <summary>UUIDv7 — time-ordered, which is what Codex mints and sorts thread ids by.</summary>
    public static Guid NewV7(DateTimeOffset timestamp)
    {
        Span<byte> b = stackalloc byte[16];
        var ms = timestamp.ToUnixTimeMilliseconds();
        b[0] = (byte)(ms >> 40); b[1] = (byte)(ms >> 32); b[2] = (byte)(ms >> 24);
        b[3] = (byte)(ms >> 16); b[4] = (byte)(ms >> 8); b[5] = (byte)ms;
        RandomNumberGenerator.Fill(b[6..]);
        b[6] = (byte)((b[6] & 0x0F) | 0x70);            // version 7
        b[8] = (byte)((b[8] & 0x3F) | 0x80);            // RFC 4122 variant
        return ReadGuidBigEndian(b);
    }

    // Guid's in-memory layout is little-endian for the first three groups; RFC 4122 byte order is
    // big-endian throughout. Converting explicitly keeps derived ids stable across architectures.
    private static void WriteGuidBigEndian(Guid value, Span<byte> destination)
    {
        value.TryWriteBytes(destination);
        (destination[0], destination[3]) = (destination[3], destination[0]);
        (destination[1], destination[2]) = (destination[2], destination[1]);
        (destination[4], destination[5]) = (destination[5], destination[4]);
        (destination[6], destination[7]) = (destination[7], destination[6]);
    }

    private static Guid ReadGuidBigEndian(ReadOnlySpan<byte> source)
    {
        Span<byte> b = stackalloc byte[16];
        source.CopyTo(b);
        (b[0], b[3]) = (b[3], b[0]);
        (b[1], b[2]) = (b[2], b[1]);
        (b[4], b[5]) = (b[5], b[4]);
        (b[6], b[7]) = (b[7], b[6]);
        return new Guid(b);
    }
}
