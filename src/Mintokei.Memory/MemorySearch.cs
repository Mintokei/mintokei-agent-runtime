using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace Mintokei.Memory;

public record MemorySearch(string Query, int Limit = 8);
