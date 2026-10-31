using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mintokei.Memory;

public class Setting : Row
{
    public string Key { get; set; } = "default";
    public string ValueJson { get; set; } = "{}";
}
