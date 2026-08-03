using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.Contracts;

using Xunit;

namespace Mintokei.AgentTranscripts.Tests;

public sealed class TranscriptSummarisingTests
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

    private static AgentMessage Command(string command) => new()
    {
        Id = Guid.NewGuid(), Role = MessageRole.Assistant,
        Type = MessageType.CommandExecution, Status = MessageStatus.Completed,
        CommandExecution = new CommandExecutionData
        {
            Id = Guid.NewGuid(), Command = command, Cwd = "/repo", ExitCode = 0,
        },
    };

    private static AgentMessage Edit(string path) => new()
    {
        Id = Guid.NewGuid(), Role = MessageRole.Assistant, Type = MessageType.ToolCall,
        Status = MessageStatus.Completed,
        ToolCall = new ToolCallData
        {
            Id = Guid.NewGuid(), ToolName = "Edit",
            Arguments = $$"""{"file_path":"{{path}}","old_string":"a","new_string":"b"}""",
            Result = "ok",
        },
    };

    private static StoredTranscript Sample => new()
    {
        Tool = AgentToolKey.ClaudeCodeCli,
        SessionId = "sess-1",
        Cwd = "/repo",
        Model = "claude-opus-5",
        SourcePath = "/home/me/.claude/projects/-repo/sess-1.jsonl",
        CreatedAt = DateTimeOffset.UtcNow,
        Messages =
        [
            User("set up the service"),
            Command("git status"),
            Edit("/repo/a.yaml"),
            Assistant("done a.yaml"),
            User("now bump the port everywhere"),
            Edit("/repo/b.yaml"),
            Assistant("all five files are at 9090"),
        ],
    };

    [Fact]
    public void A_summary_is_one_exchange_regardless_of_how_long_the_original_was()
    {
        var s = Sample.Summarise();

        Assert.Equal(2, s.Messages.Count);
        Assert.Equal(MessageRole.User, s.Messages[0].Role);
        Assert.Equal(MessageRole.Assistant, s.Messages[1].Role);
    }

    [Fact]
    public void The_briefing_carries_what_the_next_agent_needs()
    {
        var text = Sample.Summarise().Messages[0].Content!;

        Assert.Contains("Claude Code", text);                 // where it came from
        Assert.Contains("sess-1", text);                      // which session
        Assert.Contains("sess-1.jsonl", text);                // where the full record is
        Assert.Contains("/repo", text);                       // working directory
        Assert.Contains("set up the service", text);          // the requests, in order
        Assert.Contains("now bump the port everywhere", text);
        Assert.Contains("/repo/a.yaml", text);                // files touched
        Assert.Contains("git status", text);                  // commands run
        Assert.Contains("all five files are at 9090", text);  // where it left off
    }

    [Fact]
    public void It_ends_on_an_assistant_turn_so_it_does_not_read_as_an_unanswered_question()
    {
        // A transcript ending on a user turn looks unanswered — both to the next CLI and to
        // TrimIncompleteTail, which would then strip the briefing we just built.
        var s = Sample.Summarise();

        Assert.Equal(MessageType.AgentMessage, s.Messages[^1].Type);
        var trimmed = s.TrimIncompleteTail();
        Assert.Null(trimmed.DroppedRequest);
        Assert.Equal(2, trimmed.Transcript.Messages.Count);
    }

    [Fact]
    public void Metadata_survives_so_the_target_still_files_it_correctly()
    {
        var s = Sample.Summarise();

        Assert.Equal(AgentToolKey.ClaudeCodeCli, s.Tool);
        Assert.Equal("/repo", s.Cwd);
        Assert.Equal("claude-opus-5", s.Model);
        Assert.Equal("sess-1", s.SessionId);
    }

    [Fact]
    public void Older_requests_are_counted_rather_than_silently_dropped()
    {
        var many = Sample with
        {
            Messages = Enumerable.Range(1, 30).Select(i => User($"request {i}")).ToList(),
        };

        var text = many.Summarise(new SummaryOptions { MaxRequests = 5 }).Messages[0].Content!;

        Assert.Contains("25 earlier request(s) omitted", text);
        Assert.Contains("request 30", text);      // the recent ones are the ones kept
        Assert.DoesNotContain("request 1 ", text);
    }

    [Fact]
    public void Tool_activity_can_be_left_out_entirely()
    {
        var text = Sample.Summarise(new SummaryOptions { IncludeToolActivity = false }).Messages[0].Content!;

        Assert.DoesNotContain("Files touched", text);
        Assert.DoesNotContain("Recent commands", text);
        Assert.Contains("set up the service", text);
    }

    [Fact]
    public void The_header_and_acknowledgement_are_configurable()
    {
        var s = Sample.Summarise(new SummaryOptions
        {
            Header = "Previously, on this task:",
            Acknowledgement = "Got it.",
        });

        Assert.StartsWith("Previously, on this task:", s.Messages[0].Content);
        Assert.Equal("Got it.", s.Messages[1].Content);
    }

    [Fact]
    public void SummariseIfLonger_leaves_a_short_conversation_alone()
    {
        var s = Sample.SummariseIfLonger(maxMessages: 100);
        Assert.Equal(Sample.Messages.Count, s.Messages.Count);
    }

    [Fact]
    public void SummariseIfLonger_compresses_once_the_conversation_is_big()
    {
        var s = Sample.SummariseIfLonger(maxMessages: 3);
        Assert.Equal(2, s.Messages.Count);
    }

    [Fact]
    public void A_conversation_with_nothing_in_it_still_produces_a_usable_briefing()
    {
        var empty = Sample with { Messages = [] };
        var s = empty.Summarise();

        Assert.Equal(2, s.Messages.Count);
        Assert.False(string.IsNullOrWhiteSpace(s.Messages[0].Content));
        Assert.Contains("Carry on from here", s.Messages[0].Content);
    }

    [Fact]
    public void The_closing_message_is_truncated_rather_than_pasted_whole()
    {
        var big = Sample with { Messages = [User("go"), Assistant(new string('x', 10_000))] };
        var text = big.Summarise(new SummaryOptions { MaxClosingChars = 200 }).Messages[0].Content!;

        Assert.Contains("…", text);
        Assert.True(text.Length < 3_000, $"briefing was {text.Length} chars");
    }

    // ── a briefing somebody else wrote ───────────────────────────────────

    [Fact]
    public void A_narrative_is_added_to_the_facts_rather_than_replacing_them()
    {
        // Whoever wrote it can be wrong about what they read; the file list cannot be. So the
        // extraction stays underneath by default.
        var text = Sample.Summarise(new SummaryOptions
        {
            Narrative = "The port bump is done in a.yaml only; b.yaml was opened and not saved.",
        }).Messages[0].Content!;

        Assert.Contains("b.yaml was opened and not saved", text, StringComparison.Ordinal);
        Assert.Contains("/repo/a.yaml", text, StringComparison.Ordinal);
        Assert.Contains("now bump the port everywhere", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_narrative_is_marked_as_somebody_elses_reading()
    {
        // Unattributed, a mistaken summary reads as something that happened.
        var text = Sample.Summarise(new SummaryOptions { Narrative = "nothing was finished" })
            .Messages[0].Content!;

        Assert.Contains("written by an agent that read the transcript", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_narrative_stands_in_for_the_closing_message_rather_than_sitting_beside_it()
    {
        // Two accounts of where the work got to, disagreeing, is worse than either alone.
        var text = Sample.Summarise(new SummaryOptions { Narrative = "one file left" })
            .Messages[0].Content!;

        Assert.DoesNotContain("all five files are at 9090", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Where the previous agent left off", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Dropping_the_facts_leaves_the_narrative_and_the_metadata()
    {
        var text = Sample.Summarise(new SummaryOptions
        {
            Narrative = "one file left",
            IncludeFacts = false,
        }).Messages[0].Content!;

        Assert.Contains("one file left", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Files touched", text, StringComparison.Ordinal);
        Assert.DoesNotContain("What was asked", text, StringComparison.Ordinal);
        // The pointer back to the original survives whatever else is dropped: conversion is lossy,
        // and this is how anything missing can still be read.
        Assert.Contains("/home/me/.claude/projects/-repo/sess-1.jsonl", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_narrative_nothing_changes()
    {
        Assert.Equal(Sample.Summarise().Messages[0].Content, Sample.Summarise(new SummaryOptions()).Messages[0].Content);
    }
}
