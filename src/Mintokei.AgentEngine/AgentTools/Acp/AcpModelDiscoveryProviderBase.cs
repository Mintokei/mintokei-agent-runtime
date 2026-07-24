using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mintokei.AgentEngine.CommandRunner;

namespace Mintokei.AgentEngine.AgentTools.Acp;

/// <summary>
/// Discovers models for an ACP-speaking CLI by spawning it, performing
/// <c>initialize</c> + <c>session/new</c>, and reading the embedded
/// <c>result.models.availableModels</c> list. Both Copilot and OpenCode return the
/// full model catalog on every session/new response, so there's no pagination.
/// Subclasses provide the executable name and the <see cref="AgentToolKey"/>.
/// </summary>
public abstract class AcpModelDiscoveryProviderBase : IModelDiscoveryProvider
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private readonly ICommandLineRunner _runner;
    private readonly ILogger _logger;

    protected AcpModelDiscoveryProviderBase(ICommandLineRunner runner, ILogger logger)
    {
        _runner = runner;
        _logger = logger;
    }

    public abstract AgentToolKey AgentToolKey { get; }

    /// <summary>The CLI binary to spawn (e.g. <c>copilot</c>, <c>opencode</c>).</summary>
    protected abstract string ExecutableName { get; }

    /// <summary>Resolved local path to the executable. Lazy-cached by the subclass.</summary>
    protected abstract string ResolveExecutablePath();

    /// <summary>Arguments needed to put the CLI into ACP-stdio mode.</summary>
    protected abstract Dictionary<string, string?> AcpLaunchArguments { get; }

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
                executablePath = ResolveExecutablePath();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Binary} CLI not found", ExecutableName);
                return EmptyList();
            }

            var options = new CommandLineOptions
            {
                Executable = executablePath,
                Arguments = AcpLaunchArguments,
                WorkingDirectory = Path.GetTempPath(),
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
                await AcpJsonRpcHelper.SendRequestAsync(handle, initId, "initialize", new
                {
                    protocolVersion = 1,
                    clientCapabilities = new
                    {
                        fs = new { readTextFile = true, writeTextFile = true },
                    },
                }, token);
                await ReadResponseAsync(enumerator, initId, token);

                var sessionId = ++nextId;
                await AcpJsonRpcHelper.SendRequestAsync(handle, sessionId, "session/new", new
                {
                    cwd = Path.GetTempPath(),
                    mcpServers = Array.Empty<object>(),
                }, token);

                var response = await ReadResponseAsync(enumerator, sessionId, token);

                var models = ParseModelList(response);
                if (models.Count == 0)
                {
                    _logger.LogWarning("{Binary} session/new returned no models", ExecutableName);
                    return EmptyList();
                }

                AcpModelDiscoveryProviderBaseLog.DiscoveredModels(_logger, models.Count, ExecutableName);

                return new AgentToolModelList
                {
                    AgentToolKey = AgentToolKey,
                    Models = models,
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
                    // best-effort cleanup
                }

                cts.Cancel();
                cts.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("{Binary} model discovery timed out after {Timeout}s", ExecutableName, Timeout.TotalSeconds);
            return EmptyList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Binary} model discovery failed", ExecutableName);
            return EmptyList();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private static async Task<JsonElement> ReadResponseAsync(
        IAsyncEnumerator<CommandOutput> enumerator, int expectedId, CancellationToken ct)
    {
        while (await enumerator.MoveNextAsync())
        {
            var line = enumerator.Current;
            if (string.IsNullOrWhiteSpace(line.Line))
                continue;

            if (!AcpJsonRpcHelper.TryParseJsonRpc(line.Line, out var msg))
                continue;

            if (!msg.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.Number)
                continue;

            if (idProp.GetInt32() != expectedId)
                continue;

            if (msg.TryGetProperty("error", out var errorProp))
            {
                var errorMessage = errorProp.TryGetProperty("message", out var m) ? m.GetString() ?? "unknown" : "unknown";
                var errorCode = errorProp.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
                throw new AcpException(
                    $"ACP error (code {errorCode}): {errorMessage}", errorCode, errorMessage, msg);
            }

            return msg;
        }

        throw new InvalidOperationException($"ACP stream ended before response id {expectedId} was received.");
    }

    private static List<AgentToolModel> ParseModelList(JsonElement response)
    {
        var models = new List<AgentToolModel>();

        if (!response.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("models", out var modelsProp) ||
            !modelsProp.TryGetProperty("availableModels", out var avail) ||
            avail.ValueKind != JsonValueKind.Array)
        {
            return models;
        }

        var currentModelId = modelsProp.TryGetProperty("currentModelId", out var cmi) && cmi.ValueKind == JsonValueKind.String
            ? cmi.GetString()
            : null;

        foreach (var m in avail.EnumerateArray())
        {
            var model = ParseModel(m, currentModelId);
            if (model is not null)
                models.Add(model);
        }

        return models;
    }

    private static AgentToolModel? ParseModel(JsonElement json, string? currentModelId)
    {
        var id = json.TryGetProperty("modelId", out var idProp) && idProp.ValueKind == JsonValueKind.String
            ? idProp.GetString()
            : null;
        var displayName = json.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
            ? nameProp.GetString()
            : id;

        if (id is null || displayName is null)
            return null;

        var description = json.TryGetProperty("description", out var descProp) && descProp.ValueKind == JsonValueKind.String
            ? descProp.GetString()
            : null;

        return new AgentToolModel
        {
            Id = id,
            DisplayName = displayName,
            Description = description,
            IsDefault = id == currentModelId,
        };
    }

    private AgentToolModelList EmptyList() => new()
    {
        AgentToolKey = AgentToolKey,
        Models = [],
        Source = "dynamic",
        FetchedAt = DateTimeOffset.UtcNow,
    };
}

internal static partial class AcpModelDiscoveryProviderBaseLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Discovered {Count} models from {Binary} ACP")]
    public static partial void DiscoveredModels(ILogger logger, int count, string binary);
}
