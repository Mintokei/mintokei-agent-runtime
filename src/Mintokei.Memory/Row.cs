using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mintokei.Memory;

public abstract class Row
{
    public Guid Id { get; set; } = Guid.NewGuid();
}
