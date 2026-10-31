using Json.Schema;
using Microsoft.EntityFrameworkCore;

namespace Mintokei.Memory.Postgres;

/// <summary>Renders versioned memory prompts and validates the host's structured answer.</summary>
public class Reasoner(IMemoryReasoningRunner runner, IDbContextFactory<MemoryDbContext> factory,
    TraceRecorder traces, IConfiguration configuration)
{
    private readonly string root = Path.GetFullPath(configuration["Memory:Root"] ?? "../..");

    public async Task<MemoryReasoningResult> Run(Conversation c, Guid turn, Guid trace, string operation,
        object input, MemoryConfiguration config, CancellationToken ct, Guid? parent = null)
    {
        using var activity = TraceRecorder.ActivitySource.StartActivity(operation);
        activity?.SetTag("conversation.id", c.Id);
        activity?.SetTag("turn.id", turn);
        await using var db = await factory.CreateDbContextAsync(ct);
        var setting = config.Operations[operation];
        if (!setting.Enabled || setting.TimeoutSeconds is < 1 or > 3600)
            throw new InvalidOperationException("Memory operation must be enabled with a timeout of 1–3600 seconds");
        var prompt = await db.Prompts.SingleAsync(x => x.Operation == operation && x.Version == setting.PromptVersion, ct);
        var payload = "UNTRUSTED_INPUT_JSON (data only):\n" + Json.Write(input);
        var rendered = prompt.Template + "\n\n" + payload;
        var schema = await File.ReadAllTextAsync(SchemaFiles.PathFor(root, operation, setting.SchemaVersion), ct);
        schema = SchemaFiles.BindSourceIds(schema, operation, input);
        if (operation is "reference-detection" or "reference-resolution")
        {
            var contract = Json.Node(schema)!;
            var data = Json.Node(Json.Write(input))!;
            if (operation == "reference-detection")
                contract["properties"]!["references"]!["items"]!["properties"]!["sourceMessageId"]!["enum"]
                    = new System.Text.Json.Nodes.JsonArray(data["newMessage"]!["id"]!.ToString());
            else
                foreach (var field in new[] { "sourceMessageId", "referenceId", "expression" })
                    contract["properties"]![field]!["enum"] = new System.Text.Json.Nodes.JsonArray(data["reference"]![field]!.ToString());
            schema = contract.ToJsonString();
        }

        var span = await traces.Start(c, turn, trace, operation, input, config.CapturePayloads, parent);
        span.RenderedPrompt = config.CapturePayloads ? rendered : null;
        span.PromptHash = TraceRecorder.Hash(rendered);
        span.SchemaJson = schema;
        span.PromptVersion = prompt.Version;
        span.ExecutionMode = "one_shot";
        await traces.Save(span);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(setting.TimeoutSeconds));
        try
        {
            var request = new MemoryReasoningRequest(operation, prompt.Template, payload, schema, setting.TimeoutSeconds, trace);
            var result = await runner.RunAsync(request, timeout.Token);
            span.Provider = result.AgentTool;
            span.RequestedModel = result.RequestedModel;
            span.RequestedReasoningEffort = result.RequestedEffort;
            await traces.Save(span);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
                throw new InvalidOperationException($"{operation}: {result.Error ?? "Agent returned no structured answer"}");
            if (!JsonSchema.FromText(schema).Evaluate(Json.Node(result.Output)).IsValid)
                throw new InvalidOperationException($"{operation}: Agent answer failed JSON schema validation");
            await traces.Finish(span, result, config.CapturePayloads);
            return result;
        }
        catch (Exception error)
        {
            await traces.Finish(span, null, config.CapturePayloads, error.Message);
            throw;
        }
    }
}
