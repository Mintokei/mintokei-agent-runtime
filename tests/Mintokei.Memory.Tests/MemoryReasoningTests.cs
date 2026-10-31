using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mintokei.Memory.Tests;

public sealed class MemoryReasoningTests
{
    [MemoryPostgresFact]
    public async Task Host_callback_receives_contract_and_invalid_answers_are_traced_without_payloads()
    {
        var root = Path.Combine(Path.GetTempPath(), "memory-reasoning-test-" + Guid.NewGuid());
        var runner = new RecordingRunner();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Memory:Root"] = root }).Build());
        services.AddPostgresMemory(Environment.GetEnvironmentVariable("MEMORY_TEST_POSTGRES")!);
        services.AddSingleton<IMemoryReasoningRunner>(runner);
        await using var provider = services.BuildServiceProvider();
        try
        {
            var factory = provider.GetRequiredService<IDbContextFactory<MemoryDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            await DatabaseSetup.InitializeAsync(db, root);
            var conversation = new Conversation { Namespace = "reasoning-test-" + Guid.NewGuid() };
            db.Conversations.Add(conversation); await db.SaveChangesAsync();
            var reasoner = provider.GetRequiredService<Reasoner>();
            var settings = new MemoryConfiguration();
            var input = new { newMessages = new[] { new { id = Guid.NewGuid(), role = "user", content = "Private project detail" } } };
            async Task Run() => await reasoner.Run(conversation, Guid.NewGuid(), Guid.NewGuid(), "extraction", input, settings, default);
            await Run();
            Assert.Contains("Private project detail", runner.Request!.Prompt);
            Assert.Contains("UNTRUSTED_INPUT_JSON", runner.Request.Prompt);
            Assert.DoesNotContain("Private project detail", runner.Request.SystemPrompt);
            Assert.Contains("memoryCandidates", runner.Request.JsonSchema);
            Assert.Equal(120, runner.Request.TimeoutSeconds);
            foreach (var output in new[] { "{}", "not json", "{\"memoryCandidates\":\"not an array\"}" })
            {
                runner.Result = new(true, output, null);
                await Assert.ThrowsAnyAsync<Exception>(Run);
            }
            runner.Result = new(false, null, "Agent unavailable");
            var error = await Assert.ThrowsAsync<InvalidOperationException>(Run);
            Assert.Contains("Agent unavailable", error.Message);
            runner.Cancel = true;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(Run);
            var spans = await db.Traces.AsNoTracking().Where(t => t.ConversationId == conversation.Id).ToListAsync();
            Assert.Equal(6, spans.Count);
            Assert.Equal(5, spans.Count(s => s.Status == "failed"));
            var completed = Assert.Single(spans, s => s.Status == "completed");
            Assert.Equal("ClaudeCodeCli", completed.Provider);
            Assert.Equal("host-model", completed.RequestedModel);
            Assert.All(spans, s => { Assert.Null(s.InputJson); Assert.Null(s.OutputJson); Assert.Null(s.RenderedPrompt); Assert.NotNull(s.EndedAt); });
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class RecordingRunner : IMemoryReasoningRunner
    {
        public MemoryReasoningRequest? Request { get; private set; }
        public MemoryReasoningResult Result { get; set; } = new(true,
            "{\"memoryCandidates\":[],\"stateChanges\":[],\"unresolvedReferences\":[]}", null, "ClaudeCodeCli", "host-model");
        public bool Cancel { get; set; }
        public Task<MemoryReasoningResult> RunAsync(MemoryReasoningRequest request, CancellationToken ct)
        {
            Request = request;
            if (Cancel) throw new OperationCanceledException(ct);
            return Task.FromResult(Result);
        }
    }
}
