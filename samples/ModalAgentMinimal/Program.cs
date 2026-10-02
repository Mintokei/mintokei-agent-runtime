using System.Security.Cryptography;
using System.Text;
using Mintokei.AgentEngine.AgentTools;
using Mintokei.Sandbox.Hosting;
using Mintokei.Sandbox.Modal;

var builder = WebApplication.CreateBuilder(args);
builder.AddSandboxAgentHost().AddClaude();
var config = builder.Configuration;
string Required(string key) => !string.IsNullOrWhiteSpace(config[key]) ? config[key]! : throw new InvalidOperationException($"Configure {key}.");
var backend = new Uri(Required("Sandbox:BackendUrl"));
var grpc = new Uri(Required("Sandbox:GrpcBackendUrl"));
if (backend.Scheme != "https" || grpc.Scheme != "https") throw new InvalidOperationException("The sample requires public HTTPS runner endpoints.");
var demoToken = SHA256.HashData(Encoding.UTF8.GetBytes(Required("Demo:AccessToken")));
var client = new ModalClient(new()
{
    AppName = Required("Modal:AppName"), OwnerId = Required("Modal:OwnerId"),
    Environment = config["Modal:Environment"] ?? "main",
    Python = config["Modal:Python"] ?? "python3", Bundle = Required("Modal:Bundle"), SandboxTimeoutSeconds = 1200
}, _ => Task.FromResult(new ModalCredentials(Required("Modal:TokenId"), Required("Modal:TokenSecret"))));
var runtime = new ModalSandboxRuntime(client, new(new[] { backend.Host, grpc.Host, "api.anthropic.com" }.Distinct().ToArray()));

var app = builder.Build();
app.MapSandboxAgentHost();
app.MapPost("/demo/infer", async (HttpRequest http, Input input, IAgentJsonExecutor executor, CancellationToken ct) =>
{
    var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(http.Headers["X-Demo-Token"].ToString()));
    if (!CryptographicOperations.FixedTimeEquals(demoToken, supplied)) return Results.Unauthorized();
    if (input.Prompt.Length is < 1 or > 48000) return Results.BadRequest();
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
    deadline.CancelAfter(TimeSpan.FromMinutes(10));
    var result = await executor.ExecuteAsync(new()
    {
        Runtime = runtime, Profile = "modal", Prompt = input.Prompt,
        Session = new()
        {
            Tool = AgentToolKey.ClaudeCodeCli, WorkingDirectory = "/workspace",
            SystemPrompt = "Answer the request using a single JSON object. No markdown or surrounding prose.",
            Config = new() { ["model"] = Required("Demo:Model") },
            EnvironmentVariables = new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = Required("Demo:AnthropicApiKey") }
        }
    }, deadline.Token);
    return Results.Content(result, "application/json");
});
app.Run();

internal sealed record Input(string Prompt);
