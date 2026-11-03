using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Mintokei.AgentEngine.Claude;
using Mintokei.AgentEngine.Contracts;
using Xunit;

namespace Mintokei.AgentEngine.Tests;

public sealed class ClaudeInputCorrelationTests
{
    private static readonly Guid First = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Second = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static ClaudeStreamParser Parser() => new(NullLogger.Instance, Guid.NewGuid());
    private static List<AgentStreamOutput> Read(ClaudeStreamParser parser, object frame)
        => parser.Consume(JsonSerializer.SerializeToElement(frame), false).ToList();

    [Fact]
    public void Capability_is_conservative_until_verified_protocol_evidence_arrives()
    {
        var parser = Parser();
        Assert.False(parser.SupportsInputCorrelation);
        Read(parser, new { type = "system", subtype = "init", claude_code_version = "2.1.100" });
        Assert.False(parser.SupportsInputCorrelation);
        Read(parser, new { type = "system", subtype = "init", capabilities = new[] { "request_marker_lists_v1" } });
        Assert.False(parser.SupportsInputCorrelation); // Markers alone cannot identify hook-dropped inputs.
        Read(parser, new { type = "system", subtype = "init", capabilities = new[] { "msg_lifecycle_v1" } });
        Assert.True(parser.SupportsInputCorrelation);
    }

    [Fact]
    public void Request_markers_are_normalized_before_any_assistant_output()
    {
        var output = Read(Parser(), new { type = "assistant", user_message_uuid = First, user_message_uuids = new[] { First, Second },
            message = new { id = "msg-1", role = "assistant", content = new[] { new { type = "text", text = "Combined answer" } } } });
        Assert.Equal(new[] { First, Second }, Assert.IsType<TurnInputsObserved>(output[0]).InputIds);
        Assert.Contains(output, o => o is MessageOutput);
    }

    [Fact]
    public void Result_identifies_all_consumed_inputs_not_just_the_initial_or_latest_message()
    {
        var output = Read(Parser(), new { type = "result", subtype = "success", result = "done", result_index = 0,
            user_message_uuid = First, user_message_uuids = new[] { First, Second } });
        Assert.Equal(new[] { First, Second }, Assert.IsType<TurnInputsObserved>(output[0]).InputIds);
        Assert.Equal(new[] { First, Second }, Assert.Single(output.OfType<TurnEnded>()).AnsweredInputIds);
    }

    [Fact]
    public void Replayed_result_index_does_not_complete_a_newer_run()
    {
        var parser = Parser();
        var result = new { type = "result", subtype = "success", result_index = 1, user_message_uuid = First };
        Assert.Single(Read(parser, result).OfType<TurnEnded>());
        Assert.Empty(Read(parser, result));
        Assert.Empty(Read(parser, new { type = "result", subtype = "success", result_index = 0, user_message_uuid = First }));
    }

    [Fact]
    public void Tool_result_uuid_is_not_a_user_input_acknowledgement()
    {
        var outputs = Read(Parser(), new { type = "user", uuid = First,
            message = new { role = "user", content = new[] { new { type = "tool_result", tool_use_id = "tool-1", content = "done" } } } });
        Assert.Empty(outputs.OfType<ExternalMessageIdAssigned>());
    }

    [Fact]
    public void User_replay_acknowledges_the_exact_input_not_the_newest_user_row()
    {
        var outputs = Read(Parser(), new { type = "user", uuid = First, isReplay = true,
            message = new { role = "user", content = "initial input" } });
        var ack = Assert.Single(outputs.OfType<ExternalMessageIdAssigned>());
        Assert.Equal(First, ack.InputId);
        Assert.Equal(First.ToString(), ack.ExternalId);
    }

    [Fact]
    public void Subagent_correlation_cannot_rebind_the_parent_run()
    {
        var outputs = Read(Parser(), new { type = "assistant", parent_tool_use_id = "child-tool", user_message_uuid = Second,
            message = new { id = "child-msg", role = "assistant", content = new[] { new { type = "text", text = "child response" } } } });
        Assert.Empty(outputs.OfType<TurnInputsObserved>());
    }

    [Fact]
    public void Finished_command_reports_its_exact_input_without_claiming_it_was_answered()
    {
        var outputs = Read(Parser(), new { type = "command_lifecycle", command_uuid = Second, state = "completed" });
        Assert.Equal(Second, Assert.IsType<InputCommandFinished>(Assert.Single(outputs)).InputId);
        Assert.Empty(outputs.OfType<TurnEnded>());
    }

    [Fact]
    public async Task Protocol_writes_the_callers_stable_uuid_and_negotiates_busy_input_semantics()
    {
        var fake = new FakeProcessHandle();
        var protocol = new ClaudeSessionProtocol(NullLogger.Instance);
        await using var session = new AgentSession(Guid.NewGuid(), fake, fake.Output, protocol,
            new ClaudeInteractionReplyBuilder(), new AgentSessionOptions(), new CancellationTokenSource(), NullLogger.Instance);
        Assert.Equal(InputDeliveryMode.QueueWhileBusy, session.InputDeliveryMode);
        var start = session.StartAsync(false, TestContext.Current.CancellationToken);
        var init = await fake.WaitForWriteAsync(l => l.Contains("initialize"), TimeSpan.FromSeconds(5));
        using var doc = JsonDocument.Parse(init);
        fake.FeedStdout(JsonSerializer.Serialize(new { type = "control_response", response = new {
            request_id = doc.RootElement.GetProperty("request_id").GetString(), subtype = "success", response = new { claude_code_version = "2.1.310" } } }));
        await start;
        Assert.Equal(InputDeliveryMode.CorrelatedStreaming, session.InputDeliveryMode);
        await session.SendTurnAsync(new SessionTurn("correction", InputId: Second), TestContext.Current.CancellationToken);
        var line = await fake.WaitForWriteAsync(l => l.Contains("correction"), TimeSpan.FromSeconds(5));
        using var sent = JsonDocument.Parse(line);
        Assert.Equal(Second, sent.RootElement.GetProperty("uuid").GetGuid());
    }
}
