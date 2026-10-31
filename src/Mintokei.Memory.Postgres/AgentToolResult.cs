using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace Mintokei.Memory.Postgres;

public record AgentToolResult(JsonObject Data, List<AgentSource> Sources);
