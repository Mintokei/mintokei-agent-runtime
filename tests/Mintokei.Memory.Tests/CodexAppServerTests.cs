using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using J = Mintokei.Memory.Json;

namespace Mintokei.Memory.Tests;
// Fault-injection protocol tests use an explicitly scripted local executable.
// Actual authenticated Codex verification is recorded separately in docs/VALIDATION.md.
public class CodexAppServerTests
{
    private const string Schema = "{\"type\":\"object\",\"properties\":{\"ok\":{\"type\":\"boolean\"}},\"required\":[\"ok\"],\"additionalProperties\":false}";
    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "app-server-test-" + Guid.NewGuid());
        public IConfiguration Configuration { get; }
        public CodexAppServerHome Home { get; }
        public CodexAppServerProvider Provider { get; }

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Path.Combine(Root, "login"));
            File.WriteAllText(Path.Combine(Root, "login", "auth.json"), "{\"auth_mode\":\"chatgpt\",\"tokens\":{\"access_token\":\"scripted-only\"}}");
            File.WriteAllText(Path.Combine(Root, "login", "config.toml"), "developer_instructions = \"Must not inherit this\"\n[mcp_servers.unwanted]\ncommand = \"never-run\"\n");
            var script = Path.Combine(Root, "fake-codex");
            File.WriteAllText(script, Stub);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Memory:Root"] = Root, ["Memory:CodexLoginHome"] = Path.Combine(Root, "login"), ["Memory:CodexExecutable"] = script }).Build();
            Home = new(Configuration);
            Provider = new(Configuration, Home);
        }

        public LlmRequest Request(string prompt = "normal", string operation = "extraction", string? thread = null) => new(operation, new("codex_app_server", "gpt-6-luna", "low", TimeoutSeconds: 5), prompt, operation == "chat" ? null : Schema, Path.Combine(Root, "work"), Guid.NewGuid(), thread);
        public JsonNode[] Log() => Directory.GetFiles(Home.RuntimeHome, "rpc-*.jsonl").SelectMany(File.ReadAllLines).Select(s => J.Node(s)!).ToArray();
        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            Directory.Delete(Root, true);
        }
    }

    private const string Stub = """
#!/usr/bin/python3
import json,os,pathlib,sys,time,uuid
home=pathlib.Path(os.environ['CODEX_HOME'])
log=home/('rpc-'+str(os.getpid())+'.jsonl')
def send(value): print(json.dumps(value),flush=True)
def emit(method,params): send({'method':method,'params':params})
thread_tools={};thread_totals={};ephemeral={}
for line in sys.stdin:
 r=json.loads(line)
 with log.open('a') as f:f.write(json.dumps(r)+'\n')
 method=r.get('method');p=r.get('params') or {}
 if method=='initialize':
  send({'id':r['id'],'result':{'userAgent':'scripted-codex/1.0'}})
 elif method in ['thread/start','thread/resume']:
  tid=p.get('threadId') or str(uuid.uuid4())
  if method=='thread/start':
   thread_tools[tid]=p.get('dynamicTools',[]);ephemeral[tid]=p.get('ephemeral',False)
  else:assert 'dynamicTools' not in p
  send({'id':r['id'],'result':{'thread':{'id':tid,'ephemeral':p.get('ephemeral',False)},'model':p.get('model'),'reasoningEffort':p['config']['model_reasoning_effort'],'serviceTier':'default','sandbox':{'type':'readOnly'}}})
 elif method=='turn/start':
  tid=p['threadId'];turn=str(uuid.uuid4());text=p['input'][0]['text']
  send({'id':r['id'],'result':{'turn':{'id':turn,'status':'inProgress'}}})
  emit('turn/started',{'threadId':tid,'turn':{'id':turn}})
  emit('account/updated',{'accessToken':'DO_NOT_FORWARD_ACCOUNT_STATE'})
  emit('item/agentMessage/delta',{'threadId':'foreign-thread','itemId':'foreign','delta':'DO_NOT_FORWARD_OTHER_THREAD'})
  if text=='hang':time.sleep(30)
  if text=='crash':sys.exit(9)
  if text=='approval':
   send({'id':'server-approval','method':'item/commandExecution/requestApproval','params':{'threadId':tid}})
   continue
  if text.startswith('dynamic'):
   assert [t['name'] for t in thread_tools[tid]]==['search_knowledge','load_evidence']
   assert all(t['type']=='function' and not t['deferLoading'] for t in thread_tools[tid])
   def invoke(rid,cid,target=tid,tturn=turn,name='search_knowledge',arguments=None,success=False):
    send({'id':rid,'method':'item/tool/call','params':{'threadId':target,'turnId':tturn,'callId':cid,'tool':name,'arguments':arguments if arguments is not None else {'query':'workshop','scope':'all','temporal':'relevance'}}})
    reply=json.loads(sys.stdin.readline())
    with log.open('a') as f:f.write(json.dumps(reply)+'\n')
    assert reply['id']==rid and reply['result']['success']==success
   if text=='dynamic-checks':
    invoke('foreign','foreign',target='DO_NOT_FORWARD_OTHER_THREAD')
    invoke('old-turn','old-turn',tturn='old-turn')
    invoke('unknown','unknown',name='execute_shell')
    invoke('invalid','invalid',arguments={'query':'workshop'})
   invoke('valid','valid',success=True)
   if text=='dynamic-checks':invoke('duplicate','valid')
   cumulative=thread_totals.get(tid,0)+30;thread_totals[tid]=cumulative
   last={'inputTokens':20,'outputTokens':6,'cachedInputTokens':4,'reasoningOutputTokens':0,'totalTokens':26}
   total={'inputTokens':cumulative,'outputTokens':cumulative//30*9,'cachedInputTokens':cumulative//30*4,'reasoningOutputTokens':0,'totalTokens':cumulative//30*39}
   emit('thread/tokenUsage/updated',{'threadId':tid,'turnId':turn,'tokenUsage':{'last':last,'total':total}})
   if not ephemeral[tid]:
    folder=home/'sessions';folder.mkdir(exist_ok=True)
    (folder/('rollout-test-'+tid+'.jsonl')).write_text(json.dumps({'type':'event_msg','payload':{'type':'token_count','info':{'last_token_usage':{'total_tokens':26},'total_token_usage':{'input_tokens':total['inputTokens'],'output_tokens':total['outputTokens'],'cached_input_tokens':total['cachedInputTokens'],'reasoning_output_tokens':0}}}})+'\n')
   emit('item/completed',{'threadId':tid,'turnId':turn,'item':{'id':'assistant','type':'agentMessage','text':'{"ok":true}'}})
   emit('turn/completed',{'threadId':tid,'turn':{'id':turn,'status':'completed'}})
   continue
  if text=='failure':
   emit('turn/completed',{'threadId':tid,'turn':{'id':turn,'status':'failed','error':{'message':'Rate limited sk-test-never-expose'}}})
   continue
  value='hello' if 'outputSchema' not in p else ('{"bad":true}' if text=='invalid' else '{"ok":true}')
  if text=='no-output':
   emit('turn/completed',{'threadId':tid,'turn':{'id':turn,'status':'completed'}})
   continue
  emit('item/agentMessage/delta',{'threadId':tid,'turnId':turn,'itemId':'assistant','delta':value})
  emit('item/completed',{'threadId':tid,'turnId':turn,'item':{'id':'assistant','type':'agentMessage','text':value}})
  if text=='tool':emit('item/completed',{'threadId':tid,'turnId':turn,'item':{'id':'tool','type':'commandExecution','status':'completed','command':'pwd','aggregatedOutput':'/work'}})
  if text!='no-usage':emit('thread/tokenUsage/updated',{'threadId':tid,'tokenUsage':{'last':{'inputTokens':12,'outputTokens':4,'cachedInputTokens':0,'reasoningOutputTokens':0}}})
  emit('turn/completed',{'threadId':tid,'turn':{'id':turn,'status':'completed'}})
 elif method=='thread/unsubscribe':send({'id':r['id'],'result':{}})
""";
    [UnixProcessFact]
    public async Task Web_search_toggles_independently_of_coding_and_background_requests_are_rejected()
    {
        await using var f = new Fixture();
        foreach (var enabled in new[]
        {
            true,
            false
        }

        )
        {
            var request = f.Request("dynamic", "agent-chat")with
            {
                Config = new("codex_app_server", "gpt-6-luna", WebSearch: enabled),
                Tools = AgentMemoryTools.Definitions,
                ClientTools = new(false, (_, _, _, _) => Task.FromResult(new JsonObject { ["ok"] = true }))
            };
            var result = await f.Provider.ExecuteStructuredAsync(request, default);
            Assert.True(result.Status == "completed", result.Error);
            var start = f.Log().Last(r => r["method"]?.ToString() == "thread/start")["params"]!;
            Assert.Equal(enabled ? "live" : "disabled", start["config"]!["web_search"]!.ToString());
            Assert.Equal("read-only", start["sandbox"]!.ToString());
            Assert.False(start["config"]!["features.shell_tool"]!.GetValue<bool>());
        }

        var rejected = await f.Provider.ExecuteStructuredAsync(f.Request()with { Config = new("codex_app_server", "gpt-6-luna", WebSearch: true) }, default);
        Assert.Equal("failed", rejected.Status);
        Assert.Contains("background", rejected.Error);
    }

    [UnixProcessFact]
    public async Task Parallel_chat_runners_isolate_cancellation_and_keep_the_other_process_usable()
    {
        await using var f = new Fixture();
        using var cancelled = new CancellationTokenSource();
        var hanging = f.Provider.ExecuteStructuredAsync(f.Request("hang", "chat")with { Config = new("codex_app_server", "gpt-6-luna", TimeoutSeconds: 15) }, cancelled.Token);
        for (var i = 0; i < 100 && (!Directory.Exists(f.Home.RuntimeHome) || !f.Log().Any(x => x["method"]?.ToString() == "turn/start")); i++)
            await Task.Delay(30);
        var other = await f.Provider.ExecuteStructuredAsync(f.Request("normal", "chat"), default);
        Assert.Equal("completed", other.Status);
        Assert.False(hanging.IsCompleted);
        cancelled.Cancel();
        Assert.Equal("cancelled", (await hanging).Status);
        var again = await f.Provider.ExecuteStructuredAsync(f.Request("normal", "chat"), default);
        Assert.Equal("completed", again.Status);
        Assert.DoesNotContain("DO_NOT_FORWARD", J.Write(other));
    }

    [UnixProcessFact]
    public async Task Reuses_process_but_isolates_reasoning_threads_and_preserves_exact_prompt_schema_and_events()
    {
        await using var f = new Fixture();
        var received = new List<LlmEvent>();
        await foreach (var e in f.Provider.ExecuteStreamingAsync(f.Request(), default))
            received.Add(e);
        var first = J.Read<LlmResult>(received[^1].Data!.ToJsonString());
        var second = await f.Provider.ExecuteStructuredAsync(f.Request("again"), default);
        Assert.Equal("completed", first.Status);
        Assert.Equal("completed", second.Status);
        Assert.NotEqual(first.ThreadId, second.ThreadId);
        Assert.False(first.ProviderMetadata!["processReused"]!.GetValue<bool>());
        Assert.True(second.ProviderMetadata!["processReused"]!.GetValue<bool>());
        Assert.Equal(1, second.ProviderMetadata!["processGeneration"]!.GetValue<int>());
        Assert.Equal(12, first.Usage!["input_tokens"]!.GetValue<int>());
        Assert.Null(first.ExitCode);
        Assert.Contains(received, e => e.Type == "app_server.item/agentMessage/delta");
        Assert.DoesNotContain("DO_NOT_FORWARD", J.Write(first));
        var log = f.Log();
        Assert.Single(log, r => r["method"]?.ToString() == "initialize");
        Assert.All(log.Where(r => r["method"]?.ToString() == "thread/start"), r =>
        {
            Assert.True(r["params"]!["ephemeral"]!.GetValue<bool>());
            Assert.Equal("never", r["params"]!["approvalPolicy"]!.ToString());
            Assert.False(r["params"]!["config"]!["features.shell_tool"]!.GetValue<bool>());
        });
        var turn = log.First(r => r["method"]?.ToString() == "turn/start")["params"]!;
        Assert.Equal("normal", turn["input"]![0]!["text"]!.ToString());
        Assert.True(JsonNode.DeepEquals(J.Node(Schema), turn["outputSchema"]));
        Assert.DoesNotContain("unwanted", File.ReadAllText(Path.Combine(f.Home.RuntimeHome, "config.toml")));
    }

    [UnixProcessFact]
    public async Task Chat_resumes_persistent_thread_and_tool_activity_remains_observable()
    {
        await using var f = new Fixture();
        var first = await f.Provider.ExecuteStructuredAsync(f.Request("normal", "chat"), default);
        var second = await f.Provider.ExecuteStructuredAsync(f.Request("tool", "chat", first.ThreadId), default);
        Assert.Equal(first.ThreadId, second.ThreadId);
        Assert.Equal("hello", second.Output);
        Assert.Contains(second.RawEvents, e => e?["type"]?.ToString() == "app_server.item/completed" && e?["data"]?["item"]?["type"]?.ToString() == "commandExecution");
        var log = f.Log();
        Assert.False(log.First(r => r["method"]?.ToString() == "thread/start")["params"]!["ephemeral"]!.GetValue<bool>());
        Assert.Single(log, r => r["method"]?.ToString() == "thread/resume");
        Assert.Equal(2, log.Count(r => r["method"]?.ToString() == "thread/unsubscribe"));
    }

    [UnixProcessFact]
    public async Task Native_tools_bind_to_current_thread_turn_and_allowlist_with_reuse_and_observed_usage()
    {
        await using var f = new Fixture();
        var invocations = 0;
        var tools = new ClientToolSession(true, (_, name, arguments, _) =>
        {
            invocations++;
            Assert.Equal("search_knowledge", name);
            Assert.Equal("workshop", arguments["query"]!.ToString());
            return Task.FromResult(new JsonObject { ["evidence"] = "Authorized synthetic source" });
        });
        var request = f.Request("dynamic-checks", "agent-chat")with
        {
            Tools = AgentMemoryTools.Definitions,
            ClientTools = tools
        };
        var first = await f.Provider.ExecuteStructuredAsync(request, default);
        Assert.True(first.Status == "completed", first.Error);
        Assert.Equal(1, invocations);
        Assert.Equal(30, first.Usage!["input_tokens"]!.GetValue<int>());
        Assert.Equal(26, first.ProviderMetadata!["sessionContextTokens"]!.GetValue<int>());
        Assert.Equal("chat", first.ProviderMetadata["lane"]!.ToString());
        Assert.False(first.ProviderMetadata["ephemeral"]!.GetValue<bool>());
        Assert.DoesNotContain("DO_NOT_FORWARD", J.Write(first));
        var second = await f.Provider.ExecuteStructuredAsync(request with { ThreadId = first.ThreadId, Prompt = "dynamic" }, default);
        Assert.True(second.Status == "completed", second.Error);
        Assert.Equal(2, invocations);
        Assert.Equal(first.ThreadId, second.ThreadId);
        Assert.True(second.ProviderMetadata!["processReused"]!.GetValue<bool>());
        Assert.Equal(30, second.Usage!["input_tokens"]!.GetValue<int>());
        var fresh = await f.Provider.ExecuteStructuredAsync(request with { ClientTools = new(false, tools.Execute) }, default);
        Assert.True(fresh.Status == "completed", fresh.Error);
        Assert.NotEqual(first.ThreadId, fresh.ThreadId);
        Assert.True(fresh.ProviderMetadata!["ephemeral"]!.GetValue<bool>());
        Assert.True(fresh.ProviderMetadata["processReused"]!.GetValue<bool>());
        Assert.Equal(3, invocations);
        var log = f.Log();
        Assert.All(log.Where(r => r["method"]?.ToString()is "thread/start" or "thread/resume"), r =>
        {
            Assert.False(r["params"]!["config"]!["features.shell_tool"]!.GetValue<bool>());
            Assert.Equal("disabled", r["params"]!["config"]!["web_search"]!.ToString());
        });
    }

    [UnixProcessFact]
    public async Task Cancelling_a_native_tool_kills_the_runner_and_never_reuses_its_callback()
    {
        await using var f = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var request = f.Request("dynamic", "agent-chat")with
        {
            Tools = AgentMemoryTools.Definitions,
            ClientTools = new(false, async (_, _, _, ct) =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return new JsonObject();
            })
        };
        var task = f.Provider.ExecuteStructuredAsync(request, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        Assert.Equal("cancelled", (await task).Status);
        var fresh = await f.Provider.ExecuteStructuredAsync(request with { ClientTools = new(false, (_, _, _, _) => Task.FromResult(new JsonObject())) }, default);
        Assert.True(fresh.Status == "completed", fresh.Error);
        Assert.Equal(2, fresh.ProviderMetadata!["processGeneration"]!.GetValue<int>());
    }

    [UnixProcessFact]
    public async Task Coding_enables_sandboxed_shell_alongside_memory_tools_and_read_only_turns_disable_it()
    {
        await using var f = new Fixture();
        var request = f.Request("dynamic", "agent-chat")with
        {
            Tools = AgentMemoryTools.Definitions,
            ClientTools = new(false, (_, _, _, _) => Task.FromResult(new JsonObject { ["ok"] = true }))
        };
        foreach (var mode in new[]
        {
            "workspace_write",
            "read_only"
        }

        )
        {
            var result = await f.Provider.ExecuteStructuredAsync(request with { Config = request.Config with { ExecutionMode = mode } }, default);
            Assert.True(result.Status == "completed", result.Error);
            var start = f.Log().Last(r => r["method"]?.ToString() == "thread/start")["params"]!;
            Assert.Equal(mode == "workspace_write" ? "workspace-write" : "read-only", start["sandbox"]!.ToString());
            Assert.Equal(mode == "workspace_write", start["config"]!["features.shell_tool"]!.GetValue<bool>());
            Assert.Equal(mode == "workspace_write", start["config"]!["features.unified_exec"]!.GetValue<bool>());
            Assert.Equal("never", start["approvalPolicy"]!.ToString());
            Assert.Equal("disabled", start["config"]!["web_search"]!.ToString());
        }
    }

    [UnixProcessFact]
    public async Task Invalid_output_refusal_process_death_and_unsupported_approvals_do_not_fallback_or_replay()
    {
        await using var f = new Fixture();
        foreach (var mode in new[]
        {
            "invalid",
            "failure",
            "no-output",
            "crash",
            "approval"
        }

        )
        {
            var result = await f.Provider.ExecuteStructuredAsync(f.Request(mode), default);
            Assert.Equal("failed", result.Status);
            Assert.NotEmpty(result.Error!);
            Assert.DoesNotContain("sk-test", J.Write(result));
        }

        var next = await f.Provider.ExecuteStructuredAsync(f.Request(), default);
        Assert.Equal("completed", next.Status);
        Assert.Equal(6, f.Log().Count(r => r["method"]?.ToString() == "turn/start"));
    }

    [UnixProcessFact]
    public async Task Timeout_kills_runner_and_next_request_starts_a_new_process()
    {
        await using var f = new Fixture();
        var request = f.Request("hang");
        var result = await f.Provider.ExecuteStructuredAsync(request with { Config = request.Config with { TimeoutSeconds = 1 } }, default);
        Assert.Equal("timed_out", result.Status);
        var next = await f.Provider.ExecuteStructuredAsync(f.Request("no-usage"), default);
        Assert.Equal("completed", next.Status);
        Assert.Null(next.Usage);
        Assert.Equal(2, next.ProviderMetadata!["processGeneration"]!.GetValue<int>());
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var stopped = await f.Provider.ExecuteStructuredAsync(f.Request(), cancel.Token);
        Assert.Equal("cancelled", stopped.Status);
    }

    [UnixProcessFact]
    public async Task Background_lane_does_not_block_chat_and_permissions_cannot_escalate()
    {
        await using var f = new Fixture();
        var request = f.Request("hang");
        using var cancel = new CancellationTokenSource();
        var background = f.Provider.ExecuteStructuredAsync(request, cancel.Token);
        var chat = await f.Provider.ExecuteStructuredAsync(f.Request("normal", "chat"), default).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("completed", chat.Status);
        cancel.Cancel();
        await background;
        foreach (var invalid in new[]
        {
            f.Request()with
            {
                Config = request.Config with
                {
                    ExecutionMode = "workspace_write"
                }
            },
            f.Request("normal", "chat")with
            {
                Config = request.Config with
                {
                    ExecutionMode = "full_access"
                }
            },
            f.Request(thread: "must-not-resume")
        }

        )
            Assert.Equal("failed", (await f.Provider.ExecuteStructuredAsync(invalid, default)).Status);
    }

    [Fact]
    public async Task Runtime_login_refresh_preserves_config_isolation_and_private_file_permissions()
    {
        await using var f = new Fixture();
        await f.Home.PrepareAsync(default);
        var auth = Path.Combine(f.Home.RuntimeHome, "auth.json");
        await File.WriteAllTextAsync(auth, "runtime-refreshed");
        await f.Home.PrepareAsync(default);
        Assert.Equal("runtime-refreshed", await File.ReadAllTextAsync(auth));
        await File.WriteAllTextAsync(Path.Combine(f.Home.SourceHome, "auth.json"), "new-source-login");
        await f.Home.PrepareAsync(default);
        Assert.Equal("new-source-login", await File.ReadAllTextAsync(auth));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(auth));
        File.Delete(Path.Combine(f.Home.SourceHome, "auth.json"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Home.PrepareAsync(default));
    }

    [Fact]
    public void Switching_transports_invalidates_stale_thread_before_execution_and_keeps_other_metadata()
    {
        var c = new Conversation
        {
            CodexThreadId = "cli-old",
            MetadataJson = "{\"custom\":true}"
        };
        Assert.False(LlmProviders.PrepareSession(c, "codex_cli"));
        Assert.True(LlmProviders.PrepareSession(c, "codex_app_server"));
        Assert.Null(c.CodexThreadId);
        LlmProviders.RecordSession(c, "codex_app_server", "app-new");
        Assert.False(LlmProviders.PrepareSession(c, "codex_app_server"));
        Assert.Equal("app-new", c.CodexThreadId);
        Assert.True(LlmProviders.PrepareSession(c, "openai_api"));
        Assert.Null(c.CodexThreadId);
        LlmProviders.RecordSession(c, "codex_app_server", "app-newer");
        Assert.True(LlmProviders.PrepareSession(c, "codex_cli"));
        Assert.True(J.Node(c.MetadataJson)!["custom"]!.GetValue<bool>());
    }
}
