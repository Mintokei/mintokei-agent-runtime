using Mintokei.AgentEngine.AgentTools;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Mintokei.AgentEngine;
using Mintokei.AgentEngine.Codex;
using Mintokei.AgentEngine.Contracts;

namespace Mintokei.Memory.AgentEngine;

/// <summary>One read-only structured reasoning turn through the existing engine and host runner.</summary>
public sealed class AgentEngineLlmProvider(AgentSessionFactory sessions, MemoryCodexHome home) : ILlmProvider
{
    public async Task<LlmResult> ExecuteStructuredAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var events = new List<JsonNode?>();
        string? answer = null, thread = null;
        try
        {
            Validate(request);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(request.Config.TimeoutSeconds));
            var ct = timeout.Token;
            var profile = await home.PrepareAsync(ct);
            Directory.CreateDirectory(request.WorkingDirectory);
            var environment = new Dictionary<string, string>();
            foreach (var key in new[] { "PATH", "HOME", "USERPROFILE", "SYSTEMROOT", "WINDIR", "LOCALAPPDATA", "APPDATA", "TEMP", "TMP", "LANG", "SSL_CERT_FILE", "SSL_CERT_DIR" })
                if (Environment.GetEnvironmentVariable(key) is { } value) environment[key] = value;
            environment["CODEX_HOME"] = profile;
            var spec = new AgentSessionSpec
            {
                Tool = AgentToolKey.CodexCli,
                WorkingDirectory = request.WorkingDirectory,
                SystemPrompt = request.Instructions,
                Config = new()
                {
                    ["model"] = request.Config.Model, ["effort"] = request.Config.ReasoningEffort,
                    ["approvalPolicy"] = "never", ["sandbox"] = "read-only",
                    ["ephemeral"] = "true", ["noProjectDoc"] = "true", ["webSearch"] = "false"
                },
                EnableMcp = false,
                InheritEnvironment = false,
                EnvironmentVariables = environment,
                ExtraArgs = ["-c", "features.shell_tool=false"]
            };
            // Internal reasoning has no task/workspace entity and never resumes the user's agent session.
            await using var session = await sessions.CreateSessionAsync(new CodexBackend(), spec,
                options: new AgentSessionOptions { InteractionMode = InteractionMode.Deny }, ct: ct);
            thread = session.AgentSessionId;
            var schema = JsonSerializer.Deserialize<JsonElement>(request.JsonSchema!);
            await session.SendTurnAsync(new SessionTurn(request.Input ?? request.Prompt, OutputSchema: schema), ct);
            await foreach (var item in session.Output.WithCancellation(ct))
            {
                // These are observed normalized engine events, not claimed to be raw provider frames.
                events.Add(new JsonObject { ["source"] = "agent_engine", ["eventType"] = item.GetType().Name,
                    ["data"] = JsonSerializer.SerializeToNode(item, item.GetType()) });
                if (item is MessageOutput { Message.Type: MessageType.AgentMessage } message)
                    answer = message.Message.Content;
                if (item is TurnEnded ended)
                {
                    if (ended.IsInterrupted || ended.Failure != null)
                        throw new InvalidOperationException($"Memory reasoning did not complete: {ended.Failure?.Message ?? "interrupted"}");
                    if (string.IsNullOrWhiteSpace(answer)) throw new InvalidOperationException("Engine returned no structured answer");
                    var output = JsonNode.Parse(answer);
                    if (!JsonSchema.FromText(request.JsonSchema!).Evaluate(output).IsValid)
                        throw new InvalidOperationException("Engine answer failed JSON schema validation");
                    return Result("completed", null);
                }
            }
            throw new InvalidOperationException("Engine ended before a completed reasoning turn");
        }
        catch (OperationCanceledException)
        {
            return Result(cancellationToken.IsCancellationRequested ? "cancelled" : "failed",
                cancellationToken.IsCancellationRequested ? "Memory reasoning cancelled" : "Memory reasoning timed out");
        }
        catch (Exception error) { return Result("failed", error.Message); }

        LlmResult Result(string status, string? error) => new(answer, events, status, watch.Elapsed.TotalMilliseconds,
            request.Config.Model, request.Config.ReasoningEffort, null, null, null, null, error, thread, null,
            new JsonObject { ["execution"] = "AgentSessionFactory", ["backend"] = "CodexBackend", ["eventFormat"] = "normalized_agent_engine" });
    }

    public async IAsyncEnumerable<LlmEvent> ExecuteStreamingAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var result = await ExecuteStructuredAsync(request, ct);
        yield return new LlmEvent("execution.completed", JsonSerializer.SerializeToNode(result, Json.Options));
    }

    private static void Validate(LlmRequest request)
    {
        if (request.Config.Provider != "agent_engine")
            throw new InvalidOperationException("Memory uses provider 'agent_engine'. Configure it explicitly; legacy providers are not silently substituted.");
        if (!request.Config.Enabled || request.Config.ExecutionMode != "read_only" || request.Config.WebSearch ||
            request.ThreadId != null || request.ClientTools != null || request.MemoryTools != null)
            throw new InvalidOperationException("Memory reasoning requires enabled, isolated, read-only execution without tools or session reuse");
        if (request.JsonSchema is null || request.Config.TimeoutSeconds is < 1 or > 3600)
            throw new ArgumentException("Memory reasoning requires a JSON schema and a timeout of 1–3600 seconds");
    }
}
