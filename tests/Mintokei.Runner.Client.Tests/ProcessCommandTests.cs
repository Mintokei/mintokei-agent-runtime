using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mintokei.AgentEngine.CommandRunner;
using Mintokei.Runner.Contracts.Messages;
using Xunit;

namespace Mintokei.Runner.Client.Tests;

public sealed class ProcessCommandTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Failed_launch_persists_terminal_result_and_allows_the_next_command()
    {
        await WithRunner(async (service, outbox) =>
        {
            var correlation = Guid.NewGuid();
            var missing = new StartProcessMessage(correlation, "/missing/mintokei-test-executable", null,
                null, null, true, true);
            await service.HandleStartProcessAsync(JsonSerializer.Serialize(missing, JsonOptions));
            var reports = await outbox.GetPendingAsync();
            Assert.Equal(new[] { "ReportProcessOutput", "ReportProcessCompleted" }, reports.Select(r => r.Item2));
            Assert.All(reports, r => Assert.Equal(correlation, r.Item4));
            var failure = JsonSerializer.Deserialize<ProcessCompletedReport>(reports[1].Item3, JsonOptions)!;
            Assert.Equal(-1, failure.ExitCode);
            Assert.Contains("Process launch failed", reports[0].Item3);

            // The handler returned normally, so dispatch can acknowledge the failed start and continue.
            var next = missing with { CorrelationId = Guid.NewGuid() };
            await service.HandleStartProcessAsync(JsonSerializer.Serialize(next, JsonOptions));
            Assert.Equal(4, (await outbox.GetPendingAsync()).Count);
        });
    }

    [Fact]
    public async Task Remote_stdin_delivers_large_unicode_content_and_eof_to_a_real_process()
    {
        if (!OperatingSystem.IsLinux()) return;
        await WithRunner(async (service, outbox) =>
        {
            var correlation = Guid.NewGuid();
            var start = new StartProcessMessage(correlation, "/bin/cat", null, null, null, true, true);
            await service.HandleStartProcessAsync(JsonSerializer.Serialize(start, JsonOptions));
            var text = string.Concat(Enumerable.Repeat("€日本語", 40000));
            await service.HandleWriteStdinAsync(JsonSerializer.Serialize(
                new WriteStdinMessage(correlation, text, false, Close: true), JsonOptions));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!(await outbox.GetPendingAsync()).Any(r => r.Item2 == "ReportProcessCompleted"))
                await Task.Delay(10, timeout.Token);
            var reports = await outbox.GetPendingAsync();
            var output = JsonSerializer.Deserialize<ProcessOutputReport>(
                Assert.Single(reports, r => r.Item2 == "ReportProcessOutput").Item3, JsonOptions)!;
            Assert.Equal(text, output.Line);
            Assert.Equal(0, JsonSerializer.Deserialize<ProcessCompletedReport>(
                Assert.Single(reports, r => r.Item2 == "ReportProcessCompleted").Item3, JsonOptions)!.ExitCode);
        });
    }

    private static async Task WithRunner(Func<RunnerHostedService, LocalOutbox, Task> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"runner-process-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var options = Options.Create(new RunnerOptions { BackendUrl = "http://localhost", DataDir = directory });
            var outbox = new LocalOutbox(options);
            await outbox.InitializeAsync();
            var streams = new GrpcTaskStreamManager(NullLogger<GrpcTaskStreamManager>.Instance, outbox);
            using var service = new RunnerHostedService(options, new CommandLineRunner(), outbox,
                null!, null!, null!, streams, null!, null!, null!, null!, null!, NullLogger<RunnerHostedService>.Instance);
            await test(service, outbox);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}
