using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mintokei.Memory;

public record Evidence(Guid MessageId, string Role, string Quote);
