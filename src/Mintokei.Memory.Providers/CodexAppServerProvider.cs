using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Json.Schema;

namespace Mintokei.Memory.Providers;

public sealed class CodexAppServerProvider : ILlmProvider, IAsyncDisposable
{
    private readonly Runner[] chats;
    private readonly SemaphoreSlim chatSlots = new(4);
    private readonly object poolLock = new();
    private readonly HashSet<Runner> busyChats = [];
    private readonly Runner reasoning;
    public CodexAppServerProvider(IConfiguration configuration, CodexAppServerHome home)
    {
        chats = Enumerable.Range(0, 4).Select(_ => new Runner(configuration, home, "chat")).ToArray();
        reasoning = new(configuration, home, "reasoning");
    }

    public async Task<LlmResult> ExecuteStructuredAsync(LlmRequest request, CancellationToken ct)
    {
        LlmResult? result = null;
        await foreach (var e in ExecuteStreamingAsync(request, ct))
            if (e.Type == "execution.completed")
                result = e.Data!.DeserializeResult();
        return result ?? throw new InvalidOperationException("App Server returned no execution result.");
    }

    public async IAsyncEnumerable<LlmEvent> ExecuteStreamingAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<LlmEvent>();
        Runner runner = reasoning;
        var isChat = request.Operation is "chat" or "agent-chat";
        if (isChat)
        {
            await chatSlots.WaitAsync(ct);
            lock (poolLock)
            {
                runner = chats.First(r => !busyChats.Contains(r));
                busyChats.Add(runner);
            }
        }

        using var execution = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var task = runner.Execute(request, channel.Writer, execution.Token);
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(CancellationToken.None))
                yield return item;
            await task;
        }
        finally
        {
            execution.Cancel();
            try
            {
                await task;
            }
            finally
            {
                if (isChat)
                {
                    lock (poolLock)
                        busyChats.Remove(runner);
                    chatSlots.Release();
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var chat in chats)
            await chat.DisposeAsync();
        await reasoning.DisposeAsync();
    }

    // Separate bounded lanes keep background reasoning from holding the chat runner.
    // A lane executes one turn at a time, so notifications cannot cross conversations.
    private sealed class Runner(IConfiguration configuration, CodexAppServerHome home, string lane) : IAsyncDisposable
    {
        private readonly SemaphoreSlim gate = new(1);
        private readonly SemaphoreSlim writes = new(1);
        private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode>> pending = new();
        private readonly CancellationTokenSource shutdown = new();
        private Process? process;
        private Task? reader;
        private Task? stderr;
        private Channel<JsonNode>? active;
        private long nextId;
        private string? version;
        private string? diagnostics;
        private int generation;
        public async Task Execute(LlmRequest request, ChannelWriter<LlmEvent> output, CancellationToken ct)
        {
            var watch = Stopwatch.StartNew();
            var events = new List<JsonNode?>();
            var status = "failed";
            string? error = null, text = null, threadId = null, turnId = null, model = null, effort = null;
            JsonNode? usage = null, threadSettings = null;
            int? exitCode = null;
            bool acquired = false, reused = false;
            JsonObject? previousUsage = null;
            int? contextTokens = null;
            var isAgent = request.Operation == "agent-chat";
            var persistent = request.Operation == "chat" || request.ClientTools?.PersistSession == true;
            double? firstDelta = null;
            double? startupMs = null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, shutdown.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(request.Config.TimeoutSeconds));
            try
            {
                WebSearchPolicy.Validate(request.Operation, request.Config);
                if (!request.Config.Enabled)
                    throw new InvalidOperationException("App Server operation is disabled.");
                if (request.Config.ExecutionMode is not ("read_only" or "workspace_write") || request.Operation is not ("chat" or "agent-chat") && request.Config.ExecutionMode != "read_only")
                    throw new InvalidOperationException("Invalid App Server permissions; background calls are read-only and host full access is unavailable.");
                if (request.Operation is not ("chat" or "agent-chat") && request.ThreadId != null)
                    throw new InvalidOperationException("Background App Server operations must use independent ephemeral threads.");
                if (isAgent && (request.ClientTools == null || request.Tools == null || request.Tools.Count == 0) || request.ClientTools != null && !isAgent)
                    throw new InvalidOperationException("Agent App Server turns require explicitly registered client memory tools.");
                if (request.ClientTools != null && request.Tools!.Any(t => t?["name"]?.ToString()is not ("search_knowledge" or "load_evidence") && !request.ClientTools.AllowedToolNames.Contains(t?["name"]?.ToString() ?? "", StringComparer.Ordinal)))
                    throw new InvalidOperationException("Only registered memory and execution tools are supported.");
                if (!persistent && request.ThreadId != null)
                    throw new InvalidOperationException("An ephemeral App Server turn cannot resume a thread.");
                await gate.WaitAsync(timeout.Token);
                acquired = true;
                reused = process is { HasExited: false };
                var startup = Stopwatch.StartNew();
                await EnsureStarted(timeout.Token);
                startupMs = startup.Elapsed.TotalMilliseconds;
                active = Channel.CreateUnbounded<JsonNode>();
                if (isAgent && request.ThreadId != null)
                    previousUsage = (await CodexSessionUsage.ReadCountersAsync(request.ThreadId, timeout.Token, home.RuntimeHome))?.TotalUsage;
                Directory.CreateDirectory(request.WorkingDirectory);
                var isChat = request.Operation == "chat";
                var coding = isAgent && request.Config.ExecutionMode == "workspace_write";
                var config = new JsonObject
                {
                    ["model_reasoning_effort"] = request.Config.ReasoningEffort,
                    ["web_search"] = WebSearchPolicy.Enabled(request.Operation, request.Config) ? "live" : "disabled",
                    ["features.shell_tool"] = isChat || coding,
                    ["features.unified_exec"] = isChat || coding
                };
                var thread = new JsonObject
                {
                    ["model"] = request.Config.Model,
                    ["cwd"] = Path.GetFullPath(request.WorkingDirectory),
                    ["approvalPolicy"] = "never",
                    ["sandbox"] = request.Config.ExecutionMode == "workspace_write" ? "workspace-write" : "read-only",
                    ["serviceTier"] = "default",
                    ["config"] = config
                };
                if (request.ThreadId != null)
                {
                    thread["threadId"] = request.ThreadId;
                    thread["excludeTurns"] = true;
                }
                else
                {
                    thread["ephemeral"] = !persistent;
                    thread["allowProviderModelFallback"] = false;
                    if (request.ClientTools != null)
                        thread["dynamicTools"] = new JsonArray(request.Tools!.Select(t => (JsonNode)new JsonObject { ["type"] = "function", ["name"] = t!["name"]!.DeepClone(), ["description"] = t["description"]!.DeepClone(), ["inputSchema"] = t["parameters"]!.DeepClone(), ["deferLoading"] = false }).ToArray());
                }

                var threadWatch = Stopwatch.StartNew();
                var started = await Rpc(request.ThreadId == null ? "thread/start" : "thread/resume", thread, timeout.Token);
                threadId = started["thread"]?["id"]?.ToString() ?? throw new InvalidOperationException("App Server returned no thread ID.");
                model = started["model"]?.ToString();
                effort = started["reasoningEffort"]?.ToString();
                threadSettings = new JsonObject
                {
                    ["model"] = model,
                    ["reasoningEffort"] = effort,
                    ["serviceTier"] = started["serviceTier"]?.DeepClone(),
                    ["sandbox"] = started["sandbox"]?.DeepClone()
                };
                await Emit("app_server.thread.ready", new JsonObject { ["threadId"] = threadId, ["resumed"] = request.ThreadId != null, ["ephemeral"] = !persistent, ["setupMs"] = threadWatch.Elapsed.TotalMilliseconds, ["settings"] = threadSettings.DeepClone() });
                var turn = new JsonObject
                {
                    ["threadId"] = threadId,
                    ["model"] = request.Config.Model,
                    ["effort"] = request.Config.ReasoningEffort,
                    ["serviceTier"] = "default",
                    ["input"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = request.Prompt })
                };
                if (request.JsonSchema != null)
                    turn["outputSchema"] = Json.Node(request.JsonSchema);
                var response = await Rpc("turn/start", turn, timeout.Token);
                turnId = response["turn"]?["id"]?.ToString();
                var messages = new Dictionary<string, string>();
                var seenToolCalls = new HashSet<string>();
                await foreach (var notification in active.Reader.ReadAllAsync(timeout.Token))
                {
                    var method = notification["method"]?.ToString();
                    var data = notification["params"];
                    if (method == "item/tool/call" && notification["id"] is { } toolRequestId)
                    {
                        var name = data?["tool"]?.ToString();
                        var callId = data?["callId"]?.ToString();
                        var definition = request.Tools?.FirstOrDefault(t => t?["name"]?.ToString() == name);
                        var allowed = request.ClientTools != null && data?["threadId"]?.ToString() == threadId && turnId != null && data?["turnId"]?.ToString() == turnId && data?["namespace"] == null && definition != null && callId != null && data?["arguments"] is JsonObject && seenToolCalls.Add(callId);
                        JsonObject toolResult;
                        if (!allowed)
                            toolResult = new()
                            {
                                ["error"] = "Unregistered, duplicate, or out-of-scope client tool call rejected."
                            };
                        else
                        {
                            await Emit("app_server.item/tool/call", data?.DeepClone());
                            if (!JsonSchema.FromText(definition!["parameters"]!.ToJsonString()).Evaluate(data!["arguments"]).IsValid)
                                toolResult = new()
                                {
                                    ["error"] = "Tool arguments failed validation."
                                };
                            else
                                toolResult = await request.ClientTools!.Execute(callId!, name!, data!["arguments"]!.AsObject(), timeout.Token);
                        }

                        await Send(new JsonObject { ["id"] = toolRequestId.DeepClone(), ["result"] = new JsonObject { ["success"] = !toolResult.ContainsKey("error"), ["contentItems"] = new JsonArray(new JsonObject { ["type"] = "inputText", ["text"] = toolResult.ToJsonString() }) } }, timeout.Token);
                        continue;
                    }

                    // Only forward this thread's execution notifications, never account/auth state.
                    if (data?["threadId"]?.ToString()is { } eventThread && eventThread != threadId)
                        continue;
                    if (!(method?.StartsWith("item/") == true || method?.StartsWith("turn/") == true || method == "thread/tokenUsage/updated" || method == "error"))
                        continue;
                    if (data?["turnId"]?.ToString()is { } eventTurn && turnId != null && eventTurn != turnId)
                        continue;
                    await Emit("app_server." + method, data?.DeepClone());
                    if (method == "item/agentMessage/delta")
                        firstDelta ??= watch.Elapsed.TotalMilliseconds;
                    if (method == "item/completed" && data?["item"]?["type"]?.ToString() == "agentMessage")
                        messages[data["item"]!["id"]!.ToString()] = data["item"]!["text"]?.ToString() ?? "";
                    if (method == "thread/tokenUsage/updated")
                    {
                        var counts = data?["tokenUsage"];
                        contextTokens = counts?["last"]?["totalTokens"]?.GetValue<int>();
                        usage = isAgent ? (request.ThreadId == null ? NormalizeUsage(counts?["total"]) : CodexSessionUsage.TurnDelta(NormalizeUsage(counts?["total"]), previousUsage)) : NormalizeUsage(counts?["last"]);
                    }

                    if (method == "turn/completed")
                    {
                        status = data?["turn"]?["status"]?.ToString() == "completed" ? "completed" : "failed";
                        if (status != "completed")
                            error = "App Server turn " + data?["turn"]?["status"] + ": " + data?["turn"]?["error"];
                        break;
                    }
                }

                text = messages.Values.LastOrDefault();
                if (status != "completed")
                    error ??= "App Server ended before completing the turn. Partial output was not accepted.";
                if (status == "completed" && string.IsNullOrWhiteSpace(text))
                {
                    status = "failed";
                    error = "App Server completed without a final assistant message.";
                }

                if (status == "completed" && request.JsonSchema != null && !JsonSchema.FromText(request.JsonSchema).Evaluate(Json.Node(text)).IsValid)
                {
                    status = "failed";
                    error = "Structured output failed JSON Schema validation.";
                }

                // Unload even persistent threads after each turn; disk history remains resumable
                // and idle context does not accumulate in the long-lived runner.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await Rpc("thread/unsubscribe", new JsonObject { ["threadId"] = threadId }, cleanup.Token);
                }
                catch
                {
                    await Stop();
                } // Successful output stays successful if idle cleanup fails.
            }
            catch (OperationCanceledException)
            {
                status = ct.IsCancellationRequested || shutdown.IsCancellationRequested ? "cancelled" : "timed_out";
                error = "App Server execution cancelled or exceeded its configured timeout.";
                if (acquired)
                    await Stop();
            }
            catch (Exception ex)
            {
                status = "failed";
                error = Safe(ex.Message);
                if (acquired)
                {
                    exitCode = process is { HasExited: true } ? process.ExitCode : null;
                    await Stop();
                }
            }
            finally
            {
                if (acquired)
                {
                    active = null;
                    gate.Release();
                }

                var metadata = new JsonObject
                {
                    ["transport"] = "stdio",
                    ["lane"] = lane,
                    ["processReused"] = reused,
                    ["processGeneration"] = generation,
                    ["startupMs"] = startupMs,
                    ["firstTextDeltaMs"] = firstDelta,
                    ["threadId"] = threadId,
                    ["turnId"] = turnId,
                    ["configuredSettings"] = threadSettings,
                    ["requestedWebSearch"] = WebSearchPolicy.Enabled(request.Operation, request.Config),
                    ["servedServiceTier"] = null,
                    ["ephemeral"] = !persistent,
                    ["sessionContextTokens"] = contextTokens,
                    ["contextTokenSource"] = contextTokens == null ? "unavailable" : "thread/tokenUsage/updated.last.totalTokens",
                    ["usageAccounting"] = isAgent ? "observed_thread_totals_minus_prior_turns" : "last_model_request",
                    ["previousThreadUsage"] = previousUsage?.DeepClone()
                };
                var result = new LlmResult(text, events, status, watch.Elapsed.TotalMilliseconds, request.Config.Model, request.Config.ReasoningEffort, model, effort, usage, exitCode, error == null ? null : Safe(error), threadId, version, metadata);
                await output.WriteAsync(new("execution.completed", Json.Node(Json.Write(result))), CancellationToken.None);
                output.TryComplete();
            }

            async Task Emit(string type, JsonNode? data)
            {
                var safe = Json.Node(Safe(Json.Write(data)));
                events.Add(new JsonObject { ["type"] = type, ["data"] = safe?.DeepClone() });
                await output.WriteAsync(new(type, safe), CancellationToken.None);
            }
        }

        private async Task EnsureStarted(CancellationToken ct)
        {
            if (process is { HasExited: false })
                return;
            await Stop();
            var runtime = await home.PrepareAsync(ct);
            var work = Path.Combine(runtime, "runner-" + lane);
            Directory.CreateDirectory(work);
            var info = new ProcessStartInfo(configuration["Memory:CodexExecutable"] ?? "codex")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = work
            };
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "PATH",
                "HOME",
                "USERPROFILE",
                "LOCALAPPDATA",
                "APPDATA",
                "SYSTEMROOT",
                "WINDIR",
                "TEMP",
                "TMP",
                "TMPDIR",
                "LANG",
                "LC_ALL",
                "SSL_CERT_FILE",
                "SSL_CERT_DIR",
                "HTTPS_PROXY",
                "HTTP_PROXY",
                "NO_PROXY"
            };
            foreach (var key in info.Environment.Keys.ToArray())
                if (!allowed.Contains(key))
                    info.Environment.Remove(key);
            info.Environment["CODEX_HOME"] = runtime;
            foreach (var arg in new[]
            {
                "app-server",
                "--stdio",
                "-c",
                "approval_policy=\"never\"",
                "-c",
                "project_doc_max_bytes=0",
                "-c",
                "features.apps=false",
                "-c",
                "features.plugins=false",
                "-c",
                "features.multi_agent=false",
                "-c",
                "web_search=\"disabled\"",
                "-c",
                "features.shell_tool=false",
                "-c",
                "features.unified_exec=false",
                "-c",
                "sandbox_mode=\"read-only\""
            }

            )
                info.ArgumentList.Add(arg);
            process = new Process
            {
                StartInfo = info
            };
            process.Start();
            generation++;
            diagnostics = null;
            var running = process;
            stderr = Task.Run(async () =>
            {
                while (await running.StandardError.ReadLineAsync()is { } line)
                    diagnostics = Safe(line);
            });
            reader = ReadLoop(running);
            var hello = await Rpc("initialize", new JsonObject { ["clientInfo"] = new JsonObject { ["name"] = "mintokei_memory", ["version"] = "1.0.0" }, ["capabilities"] = new JsonObject { ["experimentalApi"] = true } }, ct);
            version = hello["userAgent"]?.ToString()?.Split(' ').FirstOrDefault();
            await Send(new JsonObject { ["method"] = "initialized", ["params"] = new JsonObject() }, ct);
        }

        private async Task<JsonNode> Rpc(string method, JsonNode parameters, CancellationToken ct)
        {
            var id = Interlocked.Increment(ref nextId);
            var done = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = done;
            try
            {
                await Send(new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters }, ct);
                return await done.Task.WaitAsync(ct);
            }
            finally
            {
                pending.TryRemove(id, out _);
            }
        }

        private async Task Send(JsonNode message, CancellationToken ct)
        {
            await writes.WaitAsync(ct);
            try
            {
                var running = process ?? throw new InvalidOperationException("App Server is not running.");
                await running.StandardInput.WriteLineAsync(message.ToJsonString().AsMemory(), ct);
                await running.StandardInput.FlushAsync(ct);
            }
            finally
            {
                writes.Release();
            }
        }

        private async Task ReadLoop(Process running)
        {
            Exception failure = new InvalidOperationException("App Server connection closed before completion.");
            try
            {
                while (await running.StandardOutput.ReadLineAsync()is { } line)
                {
                    var node = Json.Node(line) ?? throw new InvalidOperationException("Empty App Server message.");
                    if (node["id"] is { } id && (node["result"] != null || node["error"] != null))
                    {
                        if (long.TryParse(id.ToString(), out var number) && pending.TryRemove(number, out var reply))
                        {
                            if (node["error"] != null)
                                reply.TrySetException(new InvalidOperationException("App Server RPC: " + Safe(node["error"]!.ToJsonString())));
                            else
                                reply.TrySetResult(node["result"]!.DeepClone());
                        }

                        continue;
                    }

                    if (node["id"] != null)
                    {
                        // Queue only native tool requests. The active execution
                        // verifies thread/turn, tool schema, and duplicate calls.
                        if (node["method"]?.ToString() == "item/tool/call" && active?.Writer.TryWrite(node) == true)
                            continue;
                        // Interactive approvals remain unavailable.
                        await Send(new JsonObject { ["id"] = node["id"]!.DeepClone(), ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Interactive requests are unavailable; no permission or tool execution was granted." } }, shutdown.Token);
                        active?.Writer.TryComplete(new InvalidOperationException("App Server requested an unsupported interactive approval or client tool: " + node["method"]));
                        continue;
                    }

                    active?.Writer.TryWrite(node);
                }

                if (!string.IsNullOrWhiteSpace(diagnostics))
                    failure = new InvalidOperationException("App Server closed: " + diagnostics);
            }
            catch (Exception ex)
            {
                failure = new InvalidOperationException("App Server connection failed: " + Safe(ex.Message));
            }
            finally
            {
                foreach (var reply in pending.Values)
                    reply.TrySetException(failure);
                active?.Writer.TryComplete(failure);
            }
        }

        private async Task Stop()
        {
            if (process is not { } running)
                return;
            try
            {
                if (!running.HasExited)
                    running.Kill(entireProcessTree: true);
                await running.WaitForExitAsync();
            }
            catch (InvalidOperationException)
            {
            }

            if (reader != null)
                await reader;
            if (stderr != null)
                await stderr;
            running.Dispose();
            process = null;
            reader = null;
            stderr = null;
        }

        public async ValueTask DisposeAsync()
        {
            shutdown.Cancel();
            await gate.WaitAsync();
            try
            {
                await Stop();
            }
            finally
            {
                gate.Release();
            }
        }
    }

    public static JsonNode? NormalizeUsage(JsonNode? usage) => usage == null ? null : new JsonObject
    {
        ["input_tokens"] = usage["inputTokens"]?.DeepClone(),
        ["output_tokens"] = usage["outputTokens"]?.DeepClone(),
        ["cached_input_tokens"] = usage["cachedInputTokens"]?.DeepClone(),
        ["reasoning_output_tokens"] = usage["reasoningOutputTokens"]?.DeepClone()
    };
    private static string Safe(string text) => OpenAiApiProvider.Redact(text, null);
}
