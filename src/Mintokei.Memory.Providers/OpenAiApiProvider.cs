using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Json.Schema;

namespace Mintokei.Memory.Providers;

public sealed class OpenAiApiProvider(HttpClient http, OpenAiCredentials credentials) : ILlmProvider
{
    public const string Endpoint = "https://api.openai.com/v1/";
    private readonly SemaphoreSlim gate = new(2);
    public async Task<LlmResult> ExecuteStructuredAsync(LlmRequest request, CancellationToken ct)
    {
        LlmResult? result = null;
        await foreach (var e in ExecuteStreamingAsync(request, ct))
            if (e.Type == "execution.completed")
                result = e.Data!.DeserializeResult();
        return result ?? throw new InvalidOperationException("OpenAI returned no execution result.");
    }

    public async IAsyncEnumerable<LlmEvent> ExecuteStreamingAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<LlmEvent>();
        var task = Run(request, channel.Writer, ct);
        await foreach (var item in channel.Reader.ReadAllAsync(CancellationToken.None))
            yield return item;
        await task;
    }

    public static void Validate(OperationConfig config, string operation)
    {
        if (config.ExecutionMode != "read_only")
            throw new ArgumentException("OpenAI API mode has no local workspace tools; select read_only.");
        if (string.IsNullOrWhiteSpace(config.Model) || !Regex.IsMatch(config.Model, @"^[a-zA-Z0-9][a-zA-Z0-9_.:\-]{0,199}$"))
            throw new ArgumentException("OpenAI API requires an explicit model ID.");
        if (!Efforts.Contains(config.ReasoningEffort))
            throw new ArgumentException("Unsupported API reasoning effort.");
        if (config.Model.StartsWith("gpt-6-luna") && config.ReasoningEffort == "minimal")
            throw new ArgumentException("GPT-6 Luna does not support minimal effort; choose none, low, medium, high, xhigh, max, or default.");
        WebSearchPolicy.Validate(operation, config);
    }

    public static readonly string[] Efforts = ["default", "none", "minimal", "low", "medium", "high", "xhigh", "max"];
    public static JsonObject RequestBody(LlmRequest r)
    {
        Validate(r.Config, r.Operation);
        if ((r.Tools != null || r.PreviousResponseId != null || r.StoreResponse) && r.Operation != "agent-chat")
            throw new ArgumentException("Session continuation and application tools are restricted to agent-chat.");
        var body = new JsonObject
        {
            ["model"] = r.Config.Model,
            ["input"] = r.InputItems?.DeepClone() ?? JsonValue.Create(r.Input ?? r.Prompt),
            ["stream"] = true,
            ["store"] = r.StoreResponse,
            ["metadata"] = new JsonObject
            {
                ["operation"] = r.Operation,
                ["correlation_id"] = r.CorrelationId.ToString()
            }
        };
        if (r.Instructions != null)
            body["instructions"] = r.Instructions;
        if (r.PreviousResponseId != null)
            body["previous_response_id"] = r.PreviousResponseId;
        if (r.Tools != null)
        {
            body["tools"] = r.Tools.DeepClone();
            body["parallel_tool_calls"] = false;
        }

        if (r.Config.ReasoningEffort != "default")
            body["reasoning"] = new JsonObject
            {
                ["effort"] = r.Config.ReasoningEffort
            };
        if (r.JsonSchema != null)
            body["text"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["name"] = r.Operation.Replace('-', '_'),
                    ["strict"] = true,
                    ["schema"] = Json.Node(r.JsonSchema)
                }
            };
        if (WebSearchPolicy.Enabled(r.Operation, r.Config))
        {
            body["tools"] ??= new JsonArray();
            body["tools"]!.AsArray().Add(new JsonObject { ["type"] = "web_search" });
            body["include"] = new JsonArray("web_search_call.action.sources");
        }

        return body;
    }

    private async Task Run(LlmRequest r, ChannelWriter<LlmEvent> writer, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var events = new List<JsonNode?>();
        string? output = null, error = null, key = null;
        string status = "failed";
        bool acquired = false;
        JsonNode? response = null;
        int? httpStatus = null;
        string? requestId = null;
        double? firstTextMs = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(r.Config.TimeoutSeconds));
        try
        {
            if (!r.Config.Enabled)
                throw new InvalidOperationException($"Operation {r.Operation} is disabled.");
            var body = RequestBody(r);
            key = await credentials.ReadAsync(timeout.Token) ?? throw new InvalidOperationException("OpenAI API key is not configured on the server. Set OPENAI_API_KEY or .data/secrets/openai-api-key. Codex was not used as a fallback.");
            await gate.WaitAsync(timeout.Token);
            acquired = true;
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint + "responses");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var result = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            httpStatus = (int)result.StatusCode;
            requestId = result.Headers.TryGetValues("x-request-id", out var values) ? values.FirstOrDefault() : null;
            if (!result.IsSuccessStatusCode)
            {
                var payload = await result.Content.ReadAsStringAsync(timeout.Token);
                string? detail = null;
                try
                {
                    var e = Json.Node(payload)?["error"];
                    detail = e?["message"]?.ToString();
                }
                catch (System.Text.Json.JsonException)
                {
                }

                throw new InvalidOperationException($"OpenAI API HTTP {httpStatus}: {Redact(detail ?? result.ReasonPhrase ?? "Request failed", key)}");
            }

            if (result.Content.Headers.ContentType?.MediaType != "text/event-stream")
                throw new InvalidOperationException("OpenAI returned an unexpected non-streaming response.");
            await using var stream = await result.Content.ReadAsStreamAsync(timeout.Token);
            await foreach (var node in ReadEvents(stream, timeout.Token))
            {
                // Headers/authentication are never recorded. Error messages may echo credentials.
                var safe = Json.Node(Redact(node.ToJsonString(), key))!;
                var type = safe["type"]?.ToString() ?? "api.event";
                if (type == "response.output_text.delta")
                    firstTextMs ??= watch.Elapsed.TotalMilliseconds;
                events.Add(safe);
                await writer.WriteAsync(new(type, safe), CancellationToken.None);
                if (type is "response.completed" or "response.failed" or "response.incomplete")
                {
                    response = safe["response"]?.DeepClone();
                    if (type != "response.completed")
                        error = "OpenAI response " + (response?["status"]?.ToString() ?? type) + ": " + (response?["error"]?.ToJsonString() ?? response?["incomplete_details"]?.ToJsonString());
                    break;
                }

                if (type == "error")
                {
                    error = "OpenAI stream error: " + (safe["message"]?.ToString() ?? safe.ToJsonString());
                    break;
                }
            }

            if (error == null && response?["status"]?.ToString() != "completed")
                error = "OpenAI stream ended before a completed response. Partial output was not accepted.";
            if (error == null)
            {
                var items = response?["output"]?.AsArray() ?? [];
                var functions = items.Where(i => i?["type"]?.ToString() == "function_call").ToList();
                if (items.Any(i => i?["type"]?.ToString()is "computer_call" or "shell_call") || functions.Any(i => r.Tools?.Any(t => t?["name"]?.ToString() == i?["name"]?.ToString()) != true))
                    error = "OpenAI requested an unsupported local tool. No action was executed.";
                var content = items.Where(i => i?["type"]?.ToString() == "message").SelectMany(i => i?["content"]?.AsArray() ?? []).ToList();
                if (content.Any(i => i?["type"]?.ToString() == "refusal"))
                    error = "OpenAI refused this request.";
                output = string.Join("\n", content.Where(i => i?["type"]?.ToString() == "output_text").Select(i => i!["text"]!.ToString()));
                if (string.IsNullOrWhiteSpace(output) && functions.Count == 0)
                    error ??= "OpenAI completed without output text.";
                if (error == null && functions.Count == 0 && r.JsonSchema != null && !JsonSchema.FromText(r.JsonSchema).Evaluate(Json.Node(output)).IsValid)
                    error = "Structured output failed JSON Schema validation.";
                // Preserve native annotations in events and add readable source links to final chat text.
                if (error == null && r.Operation == "chat")
                {
                    var links = content.SelectMany(i => i?["annotations"]?.AsArray() ?? []).Where(a => a?["type"]?.ToString() == "url_citation").Select(a => new { Url = a!["url"]!.ToString(), Title = a["title"]?.ToString() ?? "Source" }).Where(a => Uri.TryCreate(a.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https").DistinctBy(a => a.Url).ToList();
                    if (links.Count > 0)
                        output += "\n\nSources: " + string.Join(" · ", links.Select(a => $"[{a.Title.Replace("[", "").Replace("]", "")}](<{a.Url.Replace(">", "%3E")}>)"));
                }
            }

            status = error == null ? "completed" : "failed";
        }
        catch (OperationCanceledException)
        {
            status = ct.IsCancellationRequested ? "cancelled" : "timed_out";
            error = "OpenAI execution cancelled or exceeded its configured timeout.";
        }
        catch (Exception ex)
        {
            error = Redact(ex.Message, key);
        }
        finally
        {
            if (acquired)
                gate.Release();
            var usage = response?["usage"];
            JsonNode? normalized = usage == null ? null : new JsonObject
            {
                ["input_tokens"] = usage["input_tokens"]?.DeepClone(),
                ["output_tokens"] = usage["output_tokens"]?.DeepClone(),
                ["cached_input_tokens"] = usage["input_tokens_details"]?["cached_tokens"]?.DeepClone(),
                ["reasoning_output_tokens"] = usage["output_tokens_details"]?["reasoning_tokens"]?.DeepClone()
            };
            var metadata = new JsonObject
            {
                ["endpoint"] = Endpoint + "responses",
                ["responseId"] = response?["id"]?.DeepClone(),
                ["requestId"] = requestId,
                ["httpStatusCode"] = httpStatus,
                ["firstTextDeltaMs"] = firstTextMs,
                ["usage"] = usage?.DeepClone(),
                ["store"] = r.StoreResponse,
                ["previousResponseId"] = r.PreviousResponseId
            };
            metadata["requestSettings"] = Json.Node(Json.Write(new { model = r.Config.Model, reasoningEffort = r.Config.ReasoningEffort, structuredOutput = r.JsonSchema != null, webSearch = WebSearchPolicy.Enabled(r.Operation, r.Config), stream = true }));
            var result = new LlmResult(output, events, status, watch.Elapsed.TotalMilliseconds, r.Config.Model, r.Config.ReasoningEffort, response?["model"]?.ToString(), response?["reasoning"]?["effort"]?.ToString(), normalized, null, error, null, null, metadata, response?["output"]?.DeepClone().AsArray());
            await writer.WriteAsync(new("execution.completed", Json.Node(Json.Write(result))), CancellationToken.None);
            writer.TryComplete();
        }
    }

    public static string Redact(string text, string? key)
    {
        if (!string.IsNullOrEmpty(key))
            text = text.Replace(key, "[redacted]", StringComparison.Ordinal);
        return Regex.Replace(text, @"sk-[A-Za-z0-9_\-*]{6,}", "[redacted]");
    }

    public async Task DeleteResponseAsync(string id, CancellationToken ct)
    {
        if (!Regex.IsMatch(id, @"^resp_[A-Za-z0-9_-]+$"))
            throw new ArgumentException("Invalid response ID");
        var key = await credentials.ReadAsync(ct) ?? throw new InvalidOperationException("OpenAI API key is required to delete stored agent responses.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Delete, Endpoint + "responses/" + id);
        request.Headers.Authorization = new("Bearer", key);
        using var response = await http.SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException($"Stored agent response deletion failed (HTTP {(int)response.StatusCode}); retry deletion.");
    }

    public static async IAsyncEnumerable<JsonNode> ReadEvents(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(ct)is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length == 0)
                    continue;
                var frame = data.ToString().TrimEnd('\n');
                data.Clear();
                if (frame == "[DONE]")
                    yield break;
                yield return Json.Node(frame) ?? throw new InvalidOperationException("Empty OpenAI stream event.");
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
                data.Append(line[5..].TrimStart(' ')).Append('\n');
        }

        if (data.Length > 0 && data.ToString().Trim() != "[DONE]")
            yield return Json.Node(data.ToString())!;
    }

    public async Task<JsonNode> DiscoverModelsAsync(CancellationToken ct)
    {
        var key = await credentials.ReadAsync(ct);
        if (key == null)
            return new JsonObject
            {
                ["provider"] = "openai_api",
                ["configured"] = false,
                ["source"] = "Server API key not configured",
                ["models"] = new JsonArray()
            };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint + "models");
        request.Headers.Authorization = new("Bearer", key);
        using var response = await http.SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenAI model discovery failed (HTTP {(int)response.StatusCode}). Check the server API key and account access.");
        var data = Json.Node(await response.Content.ReadAsStringAsync(timeout.Token));
        var models = new JsonArray();
        foreach (var id in (data?["data"]?.AsArray() ?? []).Select(m => m?["id"]?.ToString()).Where(id => id != null).Order())
            models.Add(new JsonObject { ["id"] = id });
        return new JsonObject
        {
            ["provider"] = "openai_api",
            ["configured"] = true,
            ["source"] = "OpenAI account /v1/models; Responses, reasoning, schema and tool support are validated by the API at execution",
            ["models"] = models
        };
    }
}
