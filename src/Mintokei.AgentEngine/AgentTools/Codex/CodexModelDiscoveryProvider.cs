using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mintokei.AgentEngine.CommandRunner;

namespace Mintokei.AgentEngine.AgentTools.Codex;

public sealed class CodexModelDiscoveryProvider : IModelDiscoveryProvider
{
    private static readonly Lazy<string> ExecutablePath = new(CodexJsonRpcHelper.ResolveCodexExecutablePath);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private readonly ICommandLineRunner _runner;
    private readonly ILogger<CodexModelDiscoveryProvider> _logger;

    public AgentToolKey AgentToolKey => AgentToolKey.CodexCli;

    public CodexModelDiscoveryProvider(
        ICommandLineRunner runner,
        ILogger<CodexModelDiscoveryProvider> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    public async Task<AgentToolModelList> DiscoverModelsAsync(CancellationToken ct = default)
    {
        if (!_semaphore.Wait(0))
        {
            await _semaphore.WaitAsync(ct);
            _semaphore.Release();
            return EmptyList();
        }

        try
        {
            using var timeoutCts = new CancellationTokenSource(Timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            var token = linkedCts.Token;

            string executablePath;
            try
            {
                executablePath = ExecutablePath.Value;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Codex CLI not found");
                return EmptyList();
            }

            var options = new CommandLineOptions
            {
                Executable = executablePath,
                Arguments = new Dictionary<string, string?> { ["app-server"] = null },
                WorkingDirectory = null,
                RedirectStdIn = true,
                CaptureStdErr = true,
            };

            var cts = new CancellationTokenSource();
            var (handle, output) = _runner.Start(options, cts.Token);

            try
            {
                var enumerator = output.GetAsyncEnumerator(token);
                var nextId = 0;

                var initId = ++nextId;
                await CodexJsonRpcHelper.SendRequestAsync(handle, initId, "initialize", new
                {
                    protocolVersion = "2025-01-01",
                    capabilities = new { },
                    clientInfo = new { name = "mintokei-model-discovery", version = "0.1.0" },
                }, token);

                await CodexJsonRpcHelper.ReadResponseAsync(enumerator, initId, token);

                await CodexJsonRpcHelper.SendNotificationAsync(handle, "initialized", null, token);

                var allModels = new List<AgentToolModel>();
                string? cursor = null;

                do
                {
                    var modelListId = ++nextId;
                    await CodexJsonRpcHelper.SendRequestAsync(handle, modelListId, "model/list", new
                    {
                        includeHidden = false,
                        cursor,
                    }, token);

                    var response = await CodexJsonRpcHelper.ReadResponseAsync(enumerator, modelListId, token);

                    if (response.TryGetProperty("result", out var result))
                    {
                        if (result.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var modelJson in data.EnumerateArray())
                            {
                                var model = ParseModel(modelJson);
                                if (model is not null)
                                    allModels.Add(model);
                            }
                        }

                        cursor = result.TryGetProperty("nextCursor", out var nc)
                            && nc.ValueKind == JsonValueKind.String
                            ? nc.GetString()
                            : null;
                    }
                    else
                    {
                        cursor = null;
                    }
                } while (cursor is not null);

                _logger.LogInformation("Discovered {Count} models from Codex app-server", allModels.Count);

                return new AgentToolModelList
                {
                    AgentToolKey = AgentToolKey.CodexCli,
                    Models = MergeCurated(allModels),
                    Source = "dynamic",
                    FetchedAt = DateTimeOffset.UtcNow,
                };
            }
            finally
            {
                try
                {
                    handle.Kill();
                    await handle.DisposeAsync();
                }
                catch
                {
                    // Best effort cleanup
                }

                cts.Cancel();
                cts.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Codex model discovery timed out after {Timeout}s", Timeout.TotalSeconds);
            return EmptyList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Codex model discovery failed");
            return EmptyList();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private static AgentToolModel? ParseModel(JsonElement json)
    {
        var id = json.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
        var displayName = json.TryGetProperty("displayName", out var dnProp) ? dnProp.GetString() : null;

        if (id is null || displayName is null)
            return null;

        var description = json.TryGetProperty("description", out var descProp) ? descProp.GetString() : null;
        var isDefault = json.TryGetProperty("isDefault", out var defProp) && defProp.GetBoolean();
        var hidden = json.TryGetProperty("hidden", out var hidProp) && hidProp.GetBoolean();

        var fieldOverrides = new Dictionary<string, ModelFieldOverride>();

        if (json.TryGetProperty("supportedReasoningEfforts", out var effortsArray)
            && effortsArray.ValueKind == JsonValueKind.Array)
        {
            var efforts = new List<string>();
            foreach (var item in effortsArray.EnumerateArray())
            {
                if (item.TryGetProperty("reasoningEffort", out var re) && re.ValueKind == JsonValueKind.String)
                    efforts.Add(re.GetString()!);
            }

            if (efforts.Count > 0)
            {
                string? defaultEffort = null;
                if (json.TryGetProperty("defaultReasoningEffort", out var dre) && dre.ValueKind == JsonValueKind.String)
                    defaultEffort = dre.GetString();

                fieldOverrides["effort"] = new ModelFieldOverride
                {
                    AllowedValues = efforts,
                    Default = defaultEffort,
                };
            }
        }

        if (json.TryGetProperty("supportsPersonality", out var persProp) && !persProp.GetBoolean())
        {
            fieldOverrides["personality"] = new ModelFieldOverride { Visible = false };
        }

        return new AgentToolModel
        {
            Id = id,
            DisplayName = displayName,
            Description = description,
            IsDefault = isDefault,
            Hidden = hidden,
            FieldOverrides = fieldOverrides.Count > 0 ? fieldOverrides : null,
        };
    }

    private static readonly IReadOnlyList<AgentToolModel> CuratedModels =
    [
        new() { Id = "o4-mini", DisplayName = "O4 Mini" },
        new() { Id = "o3", DisplayName = "O3" },
        new() { Id = "gpt-4.1", DisplayName = "GPT-4.1" },
        new() { Id = "gpt-4.1-mini", DisplayName = "GPT-4.1 Mini" },
        new() { Id = "gpt-4.1-nano", DisplayName = "GPT-4.1 Nano" },
        new() { Id = "codex-mini-latest", DisplayName = "Codex Mini Latest" },
    ];

    private static List<AgentToolModel> MergeCurated(List<AgentToolModel> dynamicModels)
    {
        var existingIds = new HashSet<string>(dynamicModels.Select(m => m.Id), StringComparer.Ordinal);
        foreach (var curated in CuratedModels)
        {
            if (existingIds.Add(curated.Id))
                dynamicModels.Add(curated with { IsDefault = false });
        }
        return dynamicModels;
    }

    private static AgentToolModelList EmptyList() => new()
    {
        AgentToolKey = AgentToolKey.CodexCli,
        Models = [],
        Source = "dynamic",
        FetchedAt = DateTimeOffset.UtcNow,
    };
}
