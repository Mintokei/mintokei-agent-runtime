using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.Contracts;

using Xunit;

namespace Mintokei.AgentTranscripts.Tests;

public sealed class HandoffPromptTests
{
    private static HandoffContext Full => new()
    {
        SourceTool = AgentToolKey.ClaudeCodeCli,
        TargetTool = AgentToolKey.CodexCli,
        SourceSessionId = "abc-123",
        SourcePath = "/home/me/.claude/projects/-repo/abc-123.jsonl",
        Request = "bump the port to 9090",
        Reason = "Rate limited",
        FailureKind = "RateLimited",
        Cwd = "/repo",
        HasUnresolvedToolCall = true,
    };

    [Fact]
    public void The_default_template_carries_the_facts_that_matter()
    {
        var text = HandoffPrompt.Render(null, Full);

        Assert.Contains("Claude Code", text);              // where it came from
        Assert.Contains("Rate limited", text);             // why it moved
        Assert.Contains("bump the port to 9090", text);    // what is still outstanding
        Assert.Contains("abc-123.jsonl", text);            // where the original lives
        Assert.Contains("no result", text);                // the unresolved step
        Assert.Contains("Check the current state", text);  // the load-bearing instruction
    }

    [Fact]
    public void A_line_whose_placeholder_has_no_value_is_dropped_whole()
    {
        // Otherwise the agent reads "The original transcript is at ." — a sentence with a hole,
        // which is worse than no sentence.
        var text = HandoffPrompt.Render(null, Full with { SourcePath = null, Request = null });

        Assert.DoesNotContain("original transcript is at", text);
        Assert.DoesNotContain("Outstanding request", text);
        Assert.Contains("Rate limited", text);
    }

    [Fact]
    public void A_deliberate_move_does_not_invent_a_failure()
    {
        // agentmove moves a finished conversation on purpose. Saying "the previous turn did not
        // finish" would be a lie the next agent then acts on.
        var text = HandoffPrompt.Render(null, Full with { Reason = null, Request = null });

        Assert.Contains("moved here from Claude Code", text);
        Assert.DoesNotContain("did not finish", text);
        Assert.DoesNotContain("Outstanding request", text);
    }

    [Fact]
    public void The_moved_template_gives_no_instruction_to_finish_anything()
    {
        var text = HandoffPrompt.Render(HandoffPrompt.MovedTemplate, Full with { Reason = null });

        Assert.Contains("moved here from Claude Code", text);
        Assert.Contains("history from the previous agent", text);
        Assert.DoesNotContain("finish the", text);
        Assert.DoesNotContain("did not finish", text);
    }

    [Fact]
    public void The_unresolved_step_line_disappears_when_the_tail_was_clean()
    {
        var text = HandoffPrompt.Render(null, Full with { HasUnresolvedToolCall = false });
        Assert.DoesNotContain("no result", text);
    }

    [Fact]
    public void A_custom_template_is_used_verbatim_with_substitutions()
    {
        var text = HandoffPrompt.Render("Interrupted ({failureKind}). Continue: {request}", Full);
        Assert.Equal("Interrupted (RateLimited). Continue: bump the port to 9090", text);
    }

    [Fact]
    public void A_template_may_use_no_placeholders_at_all()
    {
        Assert.Equal("You were interrupted. Continue the work.",
            HandoffPrompt.Render(HandoffPrompt.MinimalTemplate, Full));
    }

    [Fact]
    public void An_unknown_placeholder_is_left_visible_rather_than_blanked()
    {
        // A typo that silently became empty would be found in production, not in review.
        var text = HandoffPrompt.Render("continue {reqest}", Full);
        Assert.Equal("continue {reqest}", text);
    }

    [Fact]
    public void A_template_that_renders_to_nothing_falls_back_rather_than_sending_a_blank_turn()
    {
        var text = HandoffPrompt.Render("{request}", Full with { Request = null });
        Assert.Equal(HandoffPrompt.MinimalTemplate, text);
    }

    [Fact]
    public void Every_advertised_placeholder_actually_resolves()
    {
        foreach (var name in HandoffPrompt.Placeholders)
        {
            var text = HandoffPrompt.Render($"[{{{name}}}]", Full);
            Assert.False(text.Contains('{'), $"placeholder {{{name}}} was not substituted");
        }
    }
}

public sealed class TranscriptTrimmingTests
{
    private static AgentMessage User(string text) => new()
    {
        Id = Guid.NewGuid(), Role = MessageRole.User,
        Type = MessageType.UserMessage, Content = text,
    };

    private static AgentMessage Assistant(string text) => new()
    {
        Id = Guid.NewGuid(), Role = MessageRole.Assistant,
        Type = MessageType.AgentMessage, Content = text,
    };

    private static AgentMessage Tool(MessageStatus status) => new()
    {
        Id = Guid.NewGuid(), Role = MessageRole.Assistant,
        Type = MessageType.CommandExecution, Status = status,
        CommandExecution = new CommandExecutionData
        {
            Id = Guid.NewGuid(), Command = "sed -i s/8080/9090/ service.yaml", Cwd = "/repo",
        },
    };

    private static StoredTranscript With(params AgentMessage[] messages) => new()
    {
        Tool = AgentToolKey.ClaudeCodeCli, SessionId = "s", Cwd = "/repo",
        CreatedAt = DateTimeOffset.UtcNow, Messages = messages,
    };

    [Fact]
    public void A_finished_conversation_is_left_alone()
    {
        var t = With(User("hi"), Assistant("hello"));
        var result = t.TrimIncompleteTail();

        Assert.Null(result.DroppedRequest);
        Assert.Equal(2, result.Transcript.Messages.Count);
    }

    [Fact]
    public void An_unanswered_final_turn_is_removed_and_reported()
    {
        var t = With(User("first"), Assistant("done"), User("bump the port"));
        var result = t.TrimIncompleteTail();

        Assert.Equal("bump the port", result.DroppedRequest);
        Assert.Equal(2, result.Transcript.Messages.Count);
        Assert.False(result.DroppedUnresolvedToolCall);
    }

    [Fact]
    public void A_turn_that_only_got_as_far_as_a_tool_call_counts_as_unanswered()
    {
        // This is the real failover shape: the agent started work, the provider cut it off, and
        // whether the edit landed is exactly what the transcript cannot say.
        var t = With(User("first"), Assistant("done"), User("bump the port"), Tool(MessageStatus.InProgress));
        var result = t.TrimIncompleteTail();

        Assert.Equal("bump the port", result.DroppedRequest);
        Assert.True(result.DroppedUnresolvedToolCall);
        Assert.Equal(2, result.Transcript.Messages.Count);
    }

    [Fact]
    public void A_completed_tool_call_followed_by_prose_is_a_finished_turn()
    {
        var t = With(User("bump the port"), Tool(MessageStatus.Completed), Assistant("done, it is 9090"));
        var result = t.TrimIncompleteTail();

        Assert.Null(result.DroppedRequest);
        Assert.Equal(3, result.Transcript.Messages.Count);
    }

    [Fact]
    public void A_completed_tool_call_with_no_prose_still_counts_as_unanswered()
    {
        // The tool ran, but the agent never said anything — replaying that as settled history
        // would tell the next CLI the turn was handled when it was not.
        var t = With(User("bump the port"), Tool(MessageStatus.Completed));
        var result = t.TrimIncompleteTail();

        Assert.Equal("bump the port", result.DroppedRequest);
        Assert.False(result.DroppedUnresolvedToolCall);
        Assert.Empty(result.Transcript.Messages);
    }

    [Fact]
    public void A_transcript_with_no_user_turn_is_left_alone()
    {
        var t = With(Assistant("orphaned"));
        Assert.Null(t.TrimIncompleteTail().DroppedRequest);
    }

    [Fact]
    public void A_multi_step_turn_cut_off_mid_way_keeps_the_work_it_produced()
    {
        // A five-file edit killed after the fourth has four files' worth of work worth carrying.
        // Trimming it would make the next CLI redo them.
        var t = With(
            User("edit all five files"),
            Assistant("done a.yaml"), Assistant("done b.yaml"),
            Tool(MessageStatus.Completed));

        var result = t.TrimIncompleteTail();

        Assert.Null(result.DroppedRequest);                       // nothing thrown away
        Assert.Equal(4, result.Transcript.Messages.Count);
        Assert.True(result.EndsMidTurn);                          // but it did not finish
        Assert.Equal("edit all five files", result.OutstandingRequest);
    }

    [Fact]
    public void The_outstanding_request_survives_even_when_nothing_is_trimmed()
    {
        var t = With(User("do the thing"), Assistant("all done"));
        var result = t.TrimIncompleteTail();

        Assert.Null(result.DroppedRequest);
        Assert.False(result.EndsMidTurn);
        Assert.Equal("do the thing", result.OutstandingRequest);
    }
}
