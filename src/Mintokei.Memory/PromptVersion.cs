using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mintokei.Memory;

public class PromptVersion : Row
{
    public string Operation { get; set; } = "";
    public string Version { get; set; } = "v1";
    public string Template { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
