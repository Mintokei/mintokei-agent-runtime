using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Json.Schema;

namespace Mintokei.Memory.Providers;

public sealed class CodexCliProvider(IConfiguration configuration) : ILlmProvider
{
    private readonly SemaphoreSlim gate = new(4);
    public async Task<LlmResult> ExecuteStructuredAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        LlmResult? result = null;
        await foreach (var e in ExecuteStreamingAsync(request, cancellationToken))
            if (e.Type == "execution.completed")
                result = e.Data!.DeserializeResult();
        return result ?? throw new InvalidOperationException("Codex returned no execution result.");
    }

    public async IAsyncEnumerable<LlmEvent> ExecuteStreamingAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<LlmEvent>();
        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var task = RunAsync(request, channel.Writer, run.Token);
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(CancellationToken.None))
                yield return item;
            await task;
        }
        finally
        {
            run.Cancel();
            await task;
        }
    }

    private async Task RunAsync(LlmRequest r, ChannelWriter<LlmEvent> writer, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var events = new List<JsonNode?>();
        string? output = null, error = null, thread = r.ThreadId, version = null;
        int? code = null;
        JsonNode? usage = null;
        var status = "failed";
        bool acquired = false;
        Task<string>? diagnosticsTask = null;
        JsonNode? metadata = null;
        var temp = Path.Combine(Path.GetTempPath(), "mintokei-memory", Guid.NewGuid().ToString("N"));
        try
        {
            if (r.Config.ExecutionMode is not ("read_only" or "workspace_write") || r.Operation is not ("chat" or "agent-chat") && r.Config.ExecutionMode != "read_only")
                throw new InvalidOperationException("Background calls must be read-only; host full access is unavailable.");
            WebSearchPolicy.Validate(r.Operation, r.Config);
            if (!r.Config.Enabled)
                throw new InvalidOperationException($"Operation {r.Operation} is disabled.");
            if (r.MemoryTools != null && r.Operation != "agent-chat")
                throw new InvalidOperationException("Memory-agent MCP requires the agent-chat operation.");
            await gate.WaitAsync(ct);
            acquired = true;
            Directory.CreateDirectory(temp);
            Directory.CreateDirectory(r.WorkingDirectory);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(r.Config.TimeoutSeconds));
            var start = new ProcessStartInfo(configuration["Memory:CodexExecutable"] ?? "codex")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = r.WorkingDirectory
            };
            // Never inherit API keys, database secrets, or connector environment into model subprocesses.
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
                "CODEX_HOME",
                "SSL_CERT_FILE",
                "SSL_CERT_DIR",
                "HTTPS_PROXY",
                "HTTP_PROXY",
                "NO_PROXY"
            };
            foreach (var key in start.Environment.Keys.ToArray())
                if (!allowed.Contains(key))
                    start.Environment.Remove(key);
            void Add(params string[] args)
            {
                foreach (var a in args)
                    start.ArgumentList.Add(a);
            }

            Add("exec");
            if (r.ThreadId != null)
                Add("resume");
            Add("--ignore-user-config", "--skip-git-repo-check", "--json", "-c", "approval_policy=\"never\"", "-c", "project_doc_max_bytes=0", "-c", "web_search=" + Json.Write(WebSearchPolicy.Enabled(r.Operation, r.Config) ? "live" : "disabled"), "-c", "features.apps=false", "-c", "features.plugins=false", "-c", "features.multi_agent=false");
            Add("-c", "sandbox_mode=" + Json.Write(r.Config.ExecutionMode == "workspace_write" ? "workspace-write" : "read-only"));
            if (r.ThreadId == null)
                Add("--sandbox", r.Config.ExecutionMode == "workspace_write" ? "workspace-write" : "read-only");
            if (r.Operation != "chat")
            {
                if (r.MemoryTools?.PersistSession != true)
                    Add("--ephemeral");
                var coding = r.Operation == "agent-chat" && r.Config.ExecutionMode == "workspace_write";
                Add("-c", "features.shell_tool=" + (coding ? "true" : "false"), "-c", "features.unified_exec=" + (coding ? "true" : "false"));
            }

            if (r.MemoryTools is { } memory)
            {
                start.Environment["MINTOKEI_MEMORY_TOOL_TOKEN"] = memory.BearerToken;
                Add("-c", "mcp_servers.memory.url=" + Json.Write(memory.Url), "-c", "mcp_servers.memory.bearer_token_env_var=\"MINTOKEI_MEMORY_TOOL_TOKEN\"", "-c", "mcp_servers.memory.required=true", "-c", "mcp_servers.memory.enabled_tools=" + Json.Write(r.MemoryTools.ToolNames), "-c", "mcp_servers.memory.default_tools_approval_mode=\"approve\"");
            }

            if (r.Config.Model is not null)
                Add("--model", r.Config.Model);
            Add("-c", "model_reasoning_effort=" + Json.Write(r.Config.ReasoningEffort));
            if (r.JsonSchema != null)
            {
                var path = Path.Combine(temp, "schema.json");
                await File.WriteAllTextAsync(path, r.JsonSchema, ct);
                Add("--output-schema", path);
            }

            var outputPath = Path.Combine(temp, "output.txt");
            Add("--output-last-message", outputPath);
            if (r.ThreadId != null)
                Add(r.ThreadId);
            Add("-");
            var previousCounters = r.MemoryTools?.PersistSession == true && r.ThreadId != null ? await CodexSessionUsage.ReadCountersAsync(r.ThreadId, ct) : null;
            using var process = new Process
            {
                StartInfo = start
            };
            process.Start();
            using var kill = timeout.Token.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(true);
                }
                catch (InvalidOperationException)
                {
                }
            });
            var stderr = process.StandardError.ReadToEndAsync();
            diagnosticsTask = stderr;
            await process.StandardInput.WriteAsync(r.Prompt.AsMemory(), timeout.Token);
            process.StandardInput.Close();
            while (await process.StandardOutput.ReadLineAsync(timeout.Token)is { } line)
            {
                var parsed = CodexEventParser.Parse(line);
                var node = parsed.Event;
                events.Add(node);
                if (node?["type"]?.ToString() == "thread.started")
                    thread = node["thread_id"]?.ToString();
                if (node?["type"]?.ToString() == "turn.completed")
                    usage = node["usage"]?.DeepClone();
                await writer.WriteAsync(new LlmEvent(node?["type"]?.ToString() ?? "event", node), CancellationToken.None);
                if (parsed.Diagnostic is { } diagnostic)
                {
                    events.Add(diagnostic);
                    await writer.WriteAsync(new LlmEvent("application.cli_event_normalized", diagnostic), CancellationToken.None);
                }
            }

            await process.WaitForExitAsync(timeout.Token);
            code = process.ExitCode;
            var diagnostics = await stderr;
            output = File.Exists(outputPath) ? await File.ReadAllTextAsync(outputPath, ct) : null;
            if (code != 0)
                error = diagnostics + "\n" + string.Join("\n", events.Where(x => x?["type"]?.ToString()is "error" or "turn.failed").Select(x => x!.ToJsonString()));
            else if (string.IsNullOrWhiteSpace(output))
                error = "Codex completed without a final message.";
            else if (r.JsonSchema != null)
            {
                var schema = JsonSchema.FromText(r.JsonSchema);
                var validation = schema.Evaluate(JsonNode.Parse(output));
                if (!validation.IsValid)
                    error = "Structured output failed JSON Schema validation.";
            }

            status = error == null ? "completed" : "failed";
            if (r.MemoryTools?.PersistSession == true && thread != null)
            {
                var counters = await CodexSessionUsage.ReadCountersAsync(thread, ct);
                metadata = new JsonObject
                {
                    ["sessionContextTokens"] = counters?.ContextTokens,
                    ["contextTokenSource"] = counters == null ? "unavailable" : "Codex rollout event_msg.token_count.info.last_token_usage.total_tokens",
                    ["usageAccounting"] = r.ThreadId == null ? "new_thread_totals" : "delta_from_prior_thread_totals",
                    ["previousThreadUsage"] = previousCounters?.TotalUsage?.DeepClone()
                };
                // CLI 0.165 also includes earlier user turns in resumed stdout
                // totals. Preserve raw events, but report only this turn's delta.
                if (r.ThreadId != null)
                    usage = CodexSessionUsage.TurnDelta(usage, previousCounters?.TotalUsage);
            }

            version = await GetVersionAsync(ct);
        }
        catch (OperationCanceledException)
        {
            status = ct.IsCancellationRequested ? "cancelled" : "timed_out";
            error = "Codex execution cancelled or exceeded its configured timeout.";
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            if (diagnosticsTask != null && error != null)
            {
                try
                {
                    var diagnostics = await diagnosticsTask.WaitAsync(TimeSpan.FromSeconds(2));
                    if (!string.IsNullOrWhiteSpace(diagnostics))
                        error += "\nCLI diagnostics: " + diagnostics;
                }
                catch (Exception)
                {
                }
            }

            if (acquired)
                gate.Release();
            if (r.MemoryTools is { } connection)
                error = error?.Replace(connection.BearerToken, "[REDACTED]", StringComparison.Ordinal);
            try
            {
                if (Directory.Exists(temp))
                    Directory.Delete(temp, true);
            }
            catch (IOException)
            {
            }

            var result = new LlmResult(output, events, status, watch.Elapsed.TotalMilliseconds, r.Config.Model, r.Config.ReasoningEffort, null, null, usage, code, error, thread, version, metadata);
            await writer.WriteAsync(new LlmEvent("execution.completed", Json.Node(Json.Write(result))), CancellationToken.None);
            writer.TryComplete();
        }
    }

    public async Task<string?> GetVersionAsync(CancellationToken ct)
    {
        using var p = Process.Start(new ProcessStartInfo(configuration["Memory:CodexExecutable"] ?? "codex", "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
        if (p == null)
            return null;
        var text = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return text.Trim();
    }

    public static async Task<JsonNode> DiscoverModelsAsync()
    {
        var home = Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var path = Path.Combine(home, "models_cache.json");
        if (!File.Exists(path))
            return new JsonObject
            {
                ["source"] = "unavailable",
                ["models"] = new JsonArray()
            };
        var cache = JsonNode.Parse(await File.ReadAllTextAsync(path));
        var models = new JsonArray();
        foreach (var m in cache?["models"]?.AsArray() ?? [])
            if (m?["slug"] is not null)
                models.Add(new JsonObject { ["id"] = m["slug"]!.DeepClone(), ["efforts"] = m["supported_reasoning_levels"]?.DeepClone(), ["defaultEffort"] = m["default_reasoning_level"]?.DeepClone() });
        return new JsonObject
        {
            ["source"] = "Codex local account model cache (runtime remains authoritative)",
            ["updatedAt"] = File.GetLastWriteTimeUtc(path).ToString("O"),
            ["models"] = models
        };
    }
}
