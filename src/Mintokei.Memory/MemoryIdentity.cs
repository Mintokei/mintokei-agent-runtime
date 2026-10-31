using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace Mintokei.Memory;

public static class MemoryIdentity
{
    // Deterministic source namespacing: native message IDs remain in origin metadata.
    public static Guid SourceId(string sourceSystem, Guid id) => new(SHA256.HashData(Encoding.UTF8.GetBytes(sourceSystem + ":" + id))[..16]);
}
