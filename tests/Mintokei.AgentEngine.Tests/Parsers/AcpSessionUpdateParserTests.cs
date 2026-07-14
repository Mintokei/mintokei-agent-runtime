using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Mintokei.AgentEngine.Tests;

/// <summary>
/// Tests for AcpSessionUpdateParser's handling of ACP session/update notifications.
/// The parser is the trickiest piece of the ACP integrations (Copilot, OpenCode) — it owns chunk dedup,
/// delta block tracking, text ↔ reasoning transitions, tool-call state machine, and
/// shell-output exit-code extraction. These tests lock in the observable behavior so
/// future edits don't silently regress the streaming UX or message ordering.
/// </summary>
public class AcpSessionUpdateParserTests
{
    private static readonly ILogger Logger = NullLogger.Instance;
    private static readonly Guid TaskId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static JsonElement Params(string sessionUpdateJson)
    {
        // The parser expects params.update.sessionUpdate shape.
        var wrapped = $$"""{"update":{{sessionUpdateJson}}}""";
        using var doc = JsonDocument.Parse(wrapped);
        return doc.RootElement.Clone();
    }

    private static AcpSessionUpdateParser NewParser(string? fallbackCwd = null)
    {
        var parser = new AcpSessionUpdateParser(Logger, fallbackCwd);
        parser.Reset();
        return parser;
    }

    // ── agent_message_chunk / agent_thought_chunk ──

    [Fact]
    public void FirstTextChunk_OpensBlock_AndEmitsDelta()
    {
        var parser = NewParser();
        var outputs = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Hello"}}""")).ToList();

        Assert.Equal(2, outputs.Count);

        var start = Assert.IsType<DeltaOutput>(outputs[0]);
        var blockStart = Assert.IsType<BlockStartPayload>(start.Payload);
        Assert.Equal(0, blockStart.BlockIndex);
        Assert.Equal("text", blockStart.BlockType);

        var deltaOut = Assert.IsType<DeltaOutput>(outputs[1]);
        var contentDelta = Assert.IsType<ContentDeltaPayload>(deltaOut.Payload);
        Assert.Equal("text", contentDelta.DeltaType);
        Assert.Equal(0, contentDelta.BlockIndex);
        Assert.Equal("Hello", contentDelta.Delta);
    }

    [Fact]
    public void SubsequentTextChunk_OnlyEmitsDelta_SameBlockIndex()
    {
        var parser = NewParser();
        _ = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"foo"}}""")).ToList();

        var outputs = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"bar"}}""")).ToList();

        var only = Assert.Single(outputs);
        var delta = Assert.IsType<ContentDeltaPayload>(Assert.IsType<DeltaOutput>(only).Payload);
        Assert.Equal("bar", delta.Delta);
        Assert.Equal(0, delta.BlockIndex);
    }

    [Fact]
    public void TypeSwitch_Text_To_Reasoning_ClosesOldBlock_AndEmitsBufferedMessage()
    {
        var parser = NewParser();

        _ = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Draft: "}}""")).ToList();
        _ = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"ready."}}""")).ToList();

        var outputs = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"agent_thought_chunk","content":{"type":"text","text":"Thinking..."}}""")).ToList();

        // Expected: BlockStop(0) delta, MessageOutput(AgentMessage="Draft: ready."),
        // BlockStart(1, "reasoning") delta, ContentDelta("reasoning", 1, "Thinking...") delta.
        Assert.Equal(4, outputs.Count);

        Assert.IsType<BlockStopPayload>(((DeltaOutput)outputs[0]).Payload);

        var message = Assert.IsType<MessageOutput>(outputs[1]).Message;
        Assert.Equal(MessageType.AgentMessage, message.Type);
        Assert.Equal("Draft: ready.", message.Content);
        Assert.Equal(MessageRole.Assistant, message.Role);

        var blockStart = Assert.IsType<BlockStartPayload>(((DeltaOutput)outputs[2]).Payload);
        Assert.Equal(1, blockStart.BlockIndex);
        Assert.Equal("reasoning", blockStart.BlockType);

        var delta = Assert.IsType<ContentDeltaPayload>(((DeltaOutput)outputs[3]).Payload);
        Assert.Equal("reasoning", delta.DeltaType);
    }

    [Fact]
    public void ContentArrayShape_IsSupported()
    {
        // ACP allows content either as an object or as an array of blocks.
        var parser = NewParser();
        var outputs = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"agent_message_chunk","content":[{"type":"text","text":"a "},{"type":"text","text":"b"}]}""")).ToList();

        var delta = outputs.OfType<DeltaOutput>()
            .Select(d => d.Payload)
            .OfType<ContentDeltaPayload>()
            .Single();
        Assert.Equal("a b", delta.Delta);
    }

    [Fact]
    public void EmptyOrMissingContent_EmitsNothing()
    {
        var parser = NewParser();
        var empty = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"agent_message_chunk"}""")).ToList();
        Assert.Empty(empty);

        var emptyText = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":""}}""")).ToList();
        Assert.Empty(emptyText);
    }

    // ── dedup ──

    [Fact]
    public void ConsecutiveIdenticalChunks_AreDeduped()
    {
        var parser = NewParser();
        _ = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"agent_thought_chunk","content":{"type":"text","text":"Running shell command"}}""")).ToList();

        var secondOutputs = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"agent_thought_chunk","content":{"type":"text","text":"Running shell command"}}""")).ToList();

        Assert.Empty(secondOutputs);
    }

    [Fact]
    public void DifferentChunkText_AfterDupe_PassesThrough()
    {
        var parser = NewParser();
        _ = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"agent_thought_chunk","content":{"type":"text","text":"Running"}}""")).ToList();
        _ = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"agent_thought_chunk","content":{"type":"text","text":"Running"}}""")).ToList();

        var outputs = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"agent_thought_chunk","content":{"type":"text","text":" shell"}}""")).ToList();

        var delta = outputs.OfType<DeltaOutput>()
            .Select(d => d.Payload)
            .OfType<ContentDeltaPayload>()
            .Single();
        Assert.Equal(" shell", delta.Delta);
    }

    // ── tool_call / tool_call_update state machine ──

    [Fact]
    public void ToolCall_ClosesOpenTextBlock_AndEmitsBufferedMessage()
    {
        var parser = NewParser();
        _ = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"about to run"}}""")).ToList();

        var outputs = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"tool_call","toolCallId":"call_1","title":"Echo","kind":"execute","status":"pending","rawInput":{"command":"echo hi"}}""")).ToList();

        Assert.Equal(2, outputs.Count);
        Assert.IsType<BlockStopPayload>(((DeltaOutput)outputs[0]).Payload);

        var msg = Assert.IsType<MessageOutput>(outputs[1]).Message;
        Assert.Equal("about to run", msg.Content);
        Assert.Equal(MessageType.AgentMessage, msg.Type);
    }

    [Fact]
    public void ToolCallUpdate_NonTerminal_EmitsNothing()
    {
        var parser = NewParser();
        _ = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"tool_call","toolCallId":"c","title":"T","kind":"execute","status":"pending","rawInput":{"command":"ls"}}""")).ToList();

        var outputs = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"tool_call_update","toolCallId":"c","status":"in_progress"}""")).ToList();

        Assert.Empty(outputs);
    }

    [Fact]
    public void ToolCallUpdate_Completed_Shell_EmitsCommandExecution_WithExitCode_AndTrimmedOutput()
    {
        var parser = NewParser("/work/dir");
        _ = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"tool_call","toolCallId":"c","title":"Echo","kind":"execute","status":"pending","rawInput":{"command":"echo hi"}}""")).ToList();

        var outputs = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"tool_call_update","toolCallId":"c","status":"completed","content":[{"type":"content","content":{"type":"text","text":"hi\n<exited with exit code 0>"}}]}""")).ToList();

        var msg = Assert.IsType<MessageOutput>(Assert.Single(outputs)).Message;
        Assert.Equal(MessageType.CommandExecution, msg.Type);
        Assert.Equal(MessageStatus.Completed, msg.Status);
        Assert.Equal(MessageRole.Tool, msg.Role);
        Assert.Equal("c", msg.ExternalId);

        var ce = msg.CommandExecution;
        Assert.NotNull(ce);
        Assert.Equal("echo hi", ce!.Command);
        Assert.Equal("/work/dir", ce.Cwd); // fallback applied because rawInput has no cwd
        Assert.Equal(0, ce.ExitCode);
        Assert.Equal("hi", ce.Output); // trailer stripped, trailing newline trimmed
    }

    [Fact]
    public void ToolCallUpdate_Completed_Shell_NonZeroExitCode_IsExtracted()
    {
        var parser = NewParser();
        _ = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"tool_call","toolCallId":"c","kind":"execute","status":"pending","rawInput":{"command":"false"}}""")).ToList();

        var outputs = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"tool_call_update","toolCallId":"c","status":"completed","content":[{"type":"content","content":{"type":"text","text":"<exited with exit code 127>"}}]}""")).ToList();

        var msg = Assert.IsType<MessageOutput>(Assert.Single(outputs)).Message;
        Assert.Equal(127, msg.CommandExecution!.ExitCode);
        // The trailer was the entire output — after stripping we get null (no observable stdout).
        Assert.Null(msg.CommandExecution.Output);
    }

    [Fact]
    public void ToolCallUpdate_Completed_Shell_NoTrailer_LeavesOutputIntact()
    {
        var parser = NewParser();
        _ = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"tool_call","toolCallId":"c","kind":"execute","status":"pending","rawInput":{"command":"ls","cwd":"/tmp"}}""")).ToList();

        var outputs = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"tool_call_update","toolCallId":"c","status":"completed","content":[{"type":"content","content":{"type":"text","text":"file1\nfile2"}}]}""")).ToList();

        var ce = Assert.IsType<MessageOutput>(Assert.Single(outputs)).Message.CommandExecution!;
        Assert.Null(ce.ExitCode);
        Assert.Equal("file1\nfile2", ce.Output);
        Assert.Equal("/tmp", ce.Cwd); // rawInput's cwd wins over fallback
    }

    [Fact]
    public void ToolCallUpdate_Failed_EmitsGenericToolCall_WithFailedStatus()
    {
        var parser = NewParser();
        _ = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"tool_call","toolCallId":"t","title":"Fetch","kind":"fetch","status":"pending","rawInput":{"url":"http://x"}}""")).ToList();

        var outputs = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"tool_call_update","toolCallId":"t","status":"failed"}""")).ToList();

        var msg = Assert.IsType<MessageOutput>(Assert.Single(outputs)).Message;
        Assert.Equal(MessageType.ToolCall, msg.Type);
        Assert.Equal(MessageStatus.Failed, msg.Status);
        Assert.NotNull(msg.ToolCall);
        Assert.Equal("Fetch", msg.ToolCall!.ToolName);
    }

    // ── file edits → FileChange (kind: edit/delete/move) ──
    // Frame shapes captured from real OpenCode + Copilot ACP runs.

    [Fact]
    public void Edit_DiffBlockOnInitialToolCall_EmitsFileChange_CopilotShape()
    {
        // Copilot ships the diff block on the pending tool_call.
        var parser = NewParser();
        _ = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"tool_call","toolCallId":"call_1","title":"Editing greeting.txt","kind":"edit","status":"pending","rawInput":{"path":"/w/greeting.txt","old_str":"hello","new_str":"goodbye"},"locations":[{"path":"/w/greeting.txt"}],"content":[{"type":"diff","path":"/w/greeting.txt","oldText":"hello","newText":"goodbye"}]}""")).ToList();

        var outputs = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"tool_call_update","toolCallId":"call_1","status":"completed"}""")).ToList();

        var msg = Assert.IsType<MessageOutput>(Assert.Single(outputs)).Message;
        Assert.Equal(MessageType.FileChange, msg.Type);
        Assert.Equal(MessageStatus.Completed, msg.Status);
        Assert.Equal(MessageRole.Tool, msg.Role);
        Assert.Equal("call_1", msg.ExternalId);
        Assert.Null(msg.ToolCall);

        var change = Assert.Single(msg.FileChanges);
        Assert.Equal("/w/greeting.txt", change.Path);
        Assert.Equal(FileChangeKind.Update, change.ChangeKind);
        Assert.Equal("@@ -1,1 +1,1 @@\n-hello\n+goodbye", change.Diff);
    }

    [Fact]
    public void Edit_DiffBlockOnCompletedUpdate_EmitsFileChange_OpenCodeShape()
    {
        // OpenCode ships the diff block on the completed tool_call_update.
        var parser = NewParser();
        _ = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"tool_call","toolCallId":"c2","title":"edit","kind":"edit","status":"pending","rawInput":{}}""")).ToList();

        var outputs = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"tool_call_update","toolCallId":"c2","status":"completed","kind":"edit","content":[{"type":"content","content":{"type":"text","text":"Edit applied successfully."}},{"type":"diff","path":"/w/greeting.txt","oldText":"hello","newText":"goodbye"}]}""")).ToList();

        var msg = Assert.IsType<MessageOutput>(Assert.Single(outputs)).Message;
        Assert.Equal(MessageType.FileChange, msg.Type);
        var change = Assert.Single(msg.FileChanges);
        Assert.Equal("/w/greeting.txt", change.Path);
        Assert.Equal(FileChangeKind.Update, change.ChangeKind);
        Assert.Equal("@@ -1,1 +1,1 @@\n-hello\n+goodbye", change.Diff);
    }

    [Fact]
    public void Edit_NewFile_NullOldText_EmitsAddChange()
    {
        var parser = NewParser();
        _ = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"tool_call","toolCallId":"c3","kind":"edit","status":"pending","content":[{"type":"diff","path":"/w/new.txt","oldText":null,"newText":"line1\nline2"}]}""")).ToList();

        var outputs = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"tool_call_update","toolCallId":"c3","status":"completed"}""")).ToList();

        var change = Assert.Single(Assert.IsType<MessageOutput>(Assert.Single(outputs)).Message.FileChanges);
        Assert.Equal(FileChangeKind.Add, change.ChangeKind);
        Assert.Equal("@@ -0,0 +1,2 @@\n+line1\n+line2", change.Diff);
    }

    [Fact]
    public void Edit_WithNoDiffBlock_FallsBackToGenericToolCall()
    {
        // Without a diff block we can't build a meaningful FileChange, so we keep the
        // generic ToolCall path rather than emitting an empty diff.
        var parser = NewParser();
        _ = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"tool_call","toolCallId":"c4","title":"edit","kind":"edit","status":"pending","rawInput":{"path":"/w/x.txt"}}""")).ToList();

        var outputs = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"tool_call_update","toolCallId":"c4","status":"completed"}""")).ToList();

        var msg = Assert.IsType<MessageOutput>(Assert.Single(outputs)).Message;
        Assert.Equal(MessageType.ToolCall, msg.Type);
        Assert.Empty(msg.FileChanges);
    }

    // ── generic ToolCall naming: keep the first (tool-name) title ──
    // OpenCode's title degrades to the argument on completion; we keep the pending tool name.

    [Fact]
    public void GenericToolCall_Read_KeepsToolName_NotCompletedFilePath()
    {
        var parser = NewParser();
        _ = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"tool_call","toolCallId":"r1","title":"read","kind":"read","status":"pending","rawInput":{"filePath":"/w/greeting.txt"}}""")).ToList();

        var outputs = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"tool_call_update","toolCallId":"r1","status":"completed","kind":"read","title":"/w/greeting.txt","content":[{"type":"content","content":{"type":"text","text":"file body"}}]}""")).ToList();

        var msg = Assert.IsType<MessageOutput>(Assert.Single(outputs)).Message;
        Assert.Equal(MessageType.ToolCall, msg.Type);
        Assert.Equal("read", msg.ToolCall!.ToolName); // not the completed path title
    }

    [Fact]
    public void GenericToolCall_Search_KeepsToolName_NotCompletedPattern()
    {
        var parser = NewParser();
        _ = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"tool_call","toolCallId":"s1","title":"grep","kind":"search","status":"pending","rawInput":{"pattern":"three"}}""")).ToList();

        var outputs = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"tool_call_update","toolCallId":"s1","status":"completed","kind":"search","title":"three"}""")).ToList();

        var msg = Assert.IsType<MessageOutput>(Assert.Single(outputs)).Message;
        Assert.Equal("grep", msg.ToolCall!.ToolName); // not the completed pattern title
    }

    [Fact]
    public void ToolCallUpdate_UnknownToolCallId_IsIgnored()
    {
        var parser = NewParser();
        var outputs = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"tool_call_update","toolCallId":"nope","status":"completed"}""")).ToList();
        Assert.Empty(outputs);
    }

    // ── plan ──

    [Fact]
    public void Plan_ClosesOpenBlock_AndEmitsPlanMessage_WithStatusMarkers()
    {
        var parser = NewParser();
        _ = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"intro"}}""")).ToList();

        var outputs = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"plan","entries":[{"content":"one","status":"completed"},{"content":"two","status":"in_progress"},{"content":"three","status":"pending"}]}""")).ToList();

        // BlockStop, prior-text MessageOutput, plan MessageOutput.
        Assert.Equal(3, outputs.Count);
        var plan = Assert.IsType<MessageOutput>(outputs[2]).Message;
        Assert.Equal(MessageType.Plan, plan.Type);
        Assert.Contains("[x] one", plan.Content);
        Assert.Contains("[~] two", plan.Content);
        Assert.Contains("[ ] three", plan.Content);
    }

    // ── replay / ignore ──

    [Fact]
    public void UserMessageChunk_Replay_IsIgnored()
    {
        var parser = NewParser();
        var outputs = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"user_message_chunk","content":{"type":"text","text":"previous user msg"}}""")).ToList();
        Assert.Empty(outputs);
    }

    [Fact]
    public void Reset_ClearsDedupAndBlockState_SoReplayedChunksDontBleed()
    {
        var parser = NewParser();

        // First turn: emit some text that leaves block state + dedup guard populated.
        _ = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"old"}}""")).ToList();

        // Mimic what the execution service does at the start of every session/prompt.
        parser.Reset();

        // A chunk with identical text that used to be the dedup-guarded "last chunk"
        // must now pass through, and must land in block index 0 of the new turn.
        var outputs = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"old"}}""")).ToList();

        Assert.Equal(2, outputs.Count);
        var block = Assert.IsType<BlockStartPayload>(((DeltaOutput)outputs[0]).Payload);
        Assert.Equal(0, block.BlockIndex);
    }

    // ── FlushPendingBlocks at turn end ──

    [Fact]
    public void FlushPendingBlocks_EmitsTrailingBlockStopAndMessage_ForOpenBlock()
    {
        var parser = NewParser();
        _ = parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"tail end"}}""")).ToList();

        var outputs = parser.FlushPendingBlocks(TaskId).ToList();

        Assert.Equal(2, outputs.Count);
        Assert.IsType<BlockStopPayload>(((DeltaOutput)outputs[0]).Payload);

        var msg = Assert.IsType<MessageOutput>(outputs[1]).Message;
        Assert.Equal("tail end", msg.Content);
        Assert.Equal(MessageType.AgentMessage, msg.Type);
    }

    [Fact]
    public void FlushPendingBlocks_NoOpen_EmitsNothing()
    {
        var parser = NewParser();
        Assert.Empty(parser.FlushPendingBlocks(TaskId).ToList());
    }

    // ── session/load replay regression ──
    //
    // Locks in the exact shape Copilot replays during session/load, verifying the
    // parser WOULD emit MessageOutputs for every historical assistant/tool event.
    // That's the observation that made the IsReplayingHistory gate in
    // CopilotCliExecutionService necessary: without it, every resume would
    // duplicate the whole transcript. Captured from a real Copilot session.

    [Fact]
    public void ReplayStream_FromSessionLoad_EmitsMessagesThatMustBeSuppressedByService()
    {
        var parser = NewParser();

        // Exact sequence Copilot emitted when session/load replayed session
        // 05201c3f-acaa-4cba-abef-33b19587c746 (an echo tool-call turn):
        //   1. user_message_chunk       (user prompt — parser ignores)
        //   2. agent_message_chunk      (pre-tool narration)
        //   3. agent_thought_chunk      (reasoning)
        //   4. tool_call                (tool starts — closes open blocks)
        //   5. tool_call_update         (completed — emits tool message)
        //   6. agent_message_chunk      (post-tool summary)
        var outputs = new List<AgentStreamOutput>();

        outputs.AddRange(parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"user_message_chunk","content":{"type":"text","text":"Run echo"}}""")));

        outputs.AddRange(parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Running the shell command."}}""")));

        outputs.AddRange(parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"agent_thought_chunk","content":{"type":"text","text":"Running shell command"}}""")));

        outputs.AddRange(parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"tool_call","toolCallId":"call_1","title":"Echo","kind":"execute","status":"pending","rawInput":{"command":"echo hi"}}""")));

        outputs.AddRange(parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"tool_call_update","toolCallId":"call_1","status":"completed","content":[{"type":"content","content":{"type":"text","text":"hi\n<exited with exit code 0>"}}]}""")));

        outputs.AddRange(parser.HandleUpdate(TaskId, Params(
            """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Output: hi"}}""")));

        var messages = outputs.OfType<MessageOutput>().Select(m => m.Message).ToList();

        // Without the service-level IsReplayingHistory gate, the parser emits at least
        // these three persisted-row MessageOutputs during replay — one per historical
        // boundary (tool_call closed the reasoning block, tool_call_update completed,
        // and any post-tool transition would close the post-tool text). Each would
        // land as a duplicate row in the DB on every resume.
        Assert.Contains(messages, m => m.Type == MessageType.Reasoning);
        Assert.Contains(messages, m => m.Type == MessageType.CommandExecution);

        // The post-tool AgentMessage ends up in the parser's open-block buffer rather
        // than as a MessageOutput (no trailing boundary in the replay stream) — but
        // the service's post-load parser.Reset() intentionally discards that too.
        Assert.NotEmpty(messages);
    }
}
