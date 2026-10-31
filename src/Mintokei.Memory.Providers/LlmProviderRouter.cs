namespace Mintokei.Memory.Providers;
// Explicit routing: authentication, quota and model failures never change providers.
public sealed class LlmProviderRouter(CodexCliProvider codex, OpenAiApiProvider openai, CodexAppServerProvider appServer) : ILlmProvider
{
    private ILlmProvider Select(LlmRequest request) => request.Config.Provider switch
    {
        "codex_cli" => codex,
        "codex_app_server" => appServer,
        "openai_api" => openai,
        _ => throw new ArgumentException("Unsupported LLM provider: " + request.Config.Provider)};
    public Task<LlmResult> ExecuteStructuredAsync(LlmRequest request, CancellationToken ct) => Select(request).ExecuteStructuredAsync(request, ct);
    public IAsyncEnumerable<LlmEvent> ExecuteStreamingAsync(LlmRequest request, CancellationToken ct) => Select(request).ExecuteStreamingAsync(request, ct);
}
