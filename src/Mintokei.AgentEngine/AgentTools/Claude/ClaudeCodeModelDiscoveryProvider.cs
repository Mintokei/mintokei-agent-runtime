using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mintokei.AgentEngine.CommandRunner;

namespace Mintokei.AgentEngine.AgentTools.Claude;

public sealed class ClaudeCodeModelDiscoveryProvider : IModelDiscoveryProvider
{
    private static readonly Lazy<string> ExecutablePath = new(ClaudeCodeHelper.ResolveExecutablePath);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private readonly ICommandLineRunner _runner;
    private readonly ILogger<ClaudeCodeModelDiscoveryProvider> _logger;

    public AgentToolKey AgentToolKey => AgentToolKey.ClaudeCodeCli;

    public ClaudeCodeModelDiscoveryProvider(
        ICommandLineRunner runner,
        ILogger<ClaudeCodeModelDiscoveryProvider> logger)
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
                _logger.LogWarning(ex, "Claude CLI not found");
                return EmptyList();
            }

            var options = new CommandLineOptions
            {
                Executable = executablePath,
                Arguments = new Dictionary<string, string?>
                {
                    ["--output-format"] = "stream-json",
                    ["--input-format"] = "stream-json",
                    ["--verbose"] = null,
                    ["--permission-prompt-tool"] = "stdio",
                },
                WorkingDirectory = null,
                RedirectStdIn = true,
                CaptureStdErr = true,
                EnvironmentVariables = new Dictionary<string, string>
                {
                    ["CLAUDECODE"] = "",
                    ["CLAUDE_CODE"] = "",
                },
            };

            var cts = new CancellationTokenSource();
            var (handle, output) = _runner.Start(options, cts.Token);

            try
            {
                var enumerator = output.GetAsyncEnumerator(token);

                var requestId = $"req_discovery_{Guid.NewGuid():N}";
                await ClaudeCodeHelper.SendControlRequestAsync(handle, requestId, new
                {
                    subtype = "initialize",
                    hooks = (object?)null,
                }, token);

                var response = await ClaudeCodeHelper.ReadControlResponseWithBodyAsync(enumerator, requestId, token);

                var models = ParseModelsFromInitResponse(response);

                if (models.Count == 0)
                {
                    _logger.LogWarning("Claude Code initialize returned no models");
                    return EmptyList();
                }

                ClaudeCodeModelDiscoveryProviderLog.DiscoveredModels(_logger, models.Count);

                return new AgentToolModelList
                {
                    AgentToolKey = AgentToolKey.ClaudeCodeCli,
                    Models = MergeCurated(models),
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
            _logger.LogWarning("Claude Code model discovery timed out after {Timeout}s", Timeout.TotalSeconds);
            return EmptyList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Claude Code model discovery failed");
            return EmptyList();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private List<AgentToolModel> ParseModelsFromInitResponse(JsonElement root)
    {
        var models = new List<AgentToolModel>();

        if (!root.TryGetProperty("response", out var outerResponse))
            return models;

        if (!outerResponse.TryGetProperty("response", out var innerResponse))
            return models;

        if (!innerResponse.TryGetProperty("models", out var modelsArray)
            || modelsArray.ValueKind != JsonValueKind.Array)
            return models;

        var isFirst = true;
        foreach (var modelJson in modelsArray.EnumerateArray())
        {
            var model = ParseModel(modelJson, isFirst);
            if (model is not null)
            {
                models.Add(model);
                isFirst = false;
            }
        }

        return models;
    }

    private AgentToolModel? ParseModel(JsonElement json, bool isFirst)
    {
        var id = json.TryGetProperty("value", out var valueProp) ? valueProp.GetString() : null;
        var displayName = json.TryGetProperty("displayName", out var dnProp) ? dnProp.GetString() : null;

        if (id is null || displayName is null)
            return null;

        var description = json.TryGetProperty("description", out var descProp) ? descProp.GetString() : null;

        var resolvedDisplayName = ResolveDisplayName(id, displayName, description);

        var fieldOverrides = new Dictionary<string, ModelFieldOverride>();

        if (json.TryGetProperty("supportsEffort", out var effortProp))
        {
            var supportsEffort = effortProp.GetBoolean();

            if (json.TryGetProperty("supportedEffortLevels", out var levelsArray)
                && levelsArray.ValueKind == JsonValueKind.Array)
            {
                var levels = new List<string>();
                foreach (var item in levelsArray.EnumerateArray())
                {
                    var level = item.GetString();
                    if (level is not null)
                        levels.Add(level);
                }

                if (levels.Count > 0)
                {
                    fieldOverrides["effort"] = new ModelFieldOverride
                    {
                        AllowedValues = levels,
                    };
                }
            }

            if (!supportsEffort)
            {
                fieldOverrides["effort"] = new ModelFieldOverride { Visible = false };
            }
        }

        return new AgentToolModel
        {
            Id = id,
            DisplayName = resolvedDisplayName,
            Description = description,
            IsDefault = isFirst,
            FieldOverrides = fieldOverrides.Count > 0 ? fieldOverrides : null,
        };
    }

    private static readonly char[] DescriptionSeparators = ['·'];

    internal static string ResolveDisplayName(string id, string displayName, string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return displayName;

        var modelSegment = description.Split(DescriptionSeparators, 2)[0].Trim();
        if (modelSegment.Length == 0 || !LooksLikeModelName(modelSegment))
            return displayName;

        if (string.Equals(id, "default", StringComparison.Ordinal))
            return $"Default ({modelSegment})";

        return modelSegment;
    }

    private static bool LooksLikeModelName(string segment)
    {
        if (!segment.Contains(' '))
            return false;

        var family = segment.AsSpan(0, segment.IndexOf(' '));
        if (!family.Equals("Opus", StringComparison.Ordinal)
            && !family.Equals("Sonnet", StringComparison.Ordinal)
            && !family.Equals("Haiku", StringComparison.Ordinal)
            && !family.Equals("Claude", StringComparison.Ordinal))
            return false;

        foreach (var c in segment)
        {
            if (char.IsDigit(c))
                return true;
        }
        return false;
    }

    internal static readonly IReadOnlyList<AgentToolModel> CuratedModels =
    [
        new() { Id = "claude-opus-4-6", DisplayName = "Claude Opus 4.6" },
        new() { Id = "claude-sonnet-4-6", DisplayName = "Claude Sonnet 4.6" },
        new() { Id = "claude-haiku-4-5-20251001", DisplayName = "Claude Haiku 4.5" },
        new() { Id = "claude-fable-5", DisplayName = "Claude Fable 5" },
        new() { Id = "claude-mythos-5", DisplayName = "Claude Mythos 5" },
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
        AgentToolKey = AgentToolKey.ClaudeCodeCli,
        Models = [],
        Source = "dynamic",
        FetchedAt = DateTimeOffset.UtcNow,
    };
}

internal static partial class ClaudeCodeModelDiscoveryProviderLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Discovered {Count} models from Claude Code CLI")]
    public static partial void DiscoveredModels(ILogger logger, int count);
}
