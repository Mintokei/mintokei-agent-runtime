using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace Mintokei.Memory;

public record MemoryMessage(Guid Id, string Role, string Content, DateTimeOffset CreatedAt, string Sender, string? Metadata = null);
