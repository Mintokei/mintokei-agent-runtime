using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mintokei.Memory;

public abstract class Owned : Row
{
    public string UserId { get; set; } = "local";
    public string Namespace { get; set; } = "live";
}
