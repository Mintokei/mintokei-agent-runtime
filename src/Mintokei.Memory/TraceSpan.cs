using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mintokei.Memory;

public class TraceSpan : Owned
{
    public Guid TraceId { get; set; }
    public Guid? ParentSpanId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid TurnId { get; set; }
    public string Operation { get; set; } = "";
    public string Status { get; set; } = "running";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAt { get; set; }
    public double? DurationMs { get; set; }
    public string? InputJson { get; set; }
    public string? OutputJson { get; set; }
    public string? RenderedPrompt { get; set; }
    public string? PromptHash { get; set; }
    public string? SchemaJson { get; set; }
    public string? PromptVersion { get; set; }
    public string? RequestedModel { get; set; }
    public string? ObservedModel { get; set; }
    public string? RequestedReasoningEffort { get; set; }
    public string? ObservedReasoningEffort { get; set; }
    public string? Provider { get; set; }
    public string? ExecutionMode { get; set; }
    public string? CliVersion { get; set; }
    public string? RawEventsJson { get; set; }
    public string? TokenUsageJson { get; set; }
    public int? ExitCode { get; set; }
    public string? Error { get; set; }
}
