using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mintokei.Memory;

public record PreparedTurn(object Context, LlmResult? Answer = null, string? AnswerOperation = null, JsonObject? AgentSession = null);
