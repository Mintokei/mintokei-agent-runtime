using System.Text.Json.Nodes;
using Json.Schema;
using J = Mintokei.Memory.Json;

namespace Mintokei.Memory.Tests;
public class ContextAnswerTests
{
    private static JsonObject Input(Message source, ReferenceRequest[]? requests = null, bool search = true) => J.Node(J.Write(new { originalMessage = new Message { Content = "When was the launch?" }, historicalEvidence = new[] { source }, referencesToResolve = requests ?? [], canSearchAgain = search }))!.AsObject();
    [Fact]
    public void Valid_answer_has_exact_original_sources_and_matching_citations()
    {
        var source = new Message
        {
            Content = "The original launch was June 1."
        };
        var output = new ContextAnswerOutput([], [], [], "answer", $"June 1. 【{source.Id}】", [new(source.Id, source.Content)]);
        Assert.Empty(ContextAnswer.Validate(output, Input(source), []));
        Assert.NotEmpty(ContextAnswer.Validate(output with { AnswerEvidence = [new(source.Id, "June 5")] }, Input(source), []));
        Assert.NotEmpty(ContextAnswer.Validate(output with { AnswerEvidence = [new(Guid.NewGuid(), source.Content)] }, Input(source), []));
        Assert.Empty(ContextAnswer.Validate(output with { Answer = "June 1" }, Input(source), []));
        Assert.Contains(source.Id.ToString(), ContextAnswer.Render(output with { Answer = "June 1" }));
        Assert.NotEmpty(ContextAnswer.Validate(output with { Answer = $"June 1. 【{Guid.NewGuid()}】" }, Input(source), []));
        Assert.NotEmpty(ContextAnswer.Validate(output with { AnswerEvidence = [] }, Input(source), []));
    }

    [Fact]
    public void Rejected_interpretation_withholds_answer_and_ambiguity_requires_clarification()
    {
        var source = new Message
        {
            Content = "Two plausible options"
        };
        var request = new ReferenceRequest("r", Guid.NewGuid(), "second", "options", "cross_conversation", "none");
        var resolved = new ResolvedReference("r", request.SourceMessageId, request.Expression, "resolved", JsonValue.Create("Blue"), [], "Proposal");
        var ambiguous = resolved with
        {
            Status = "ambiguous",
            ResolvedValue = null
        };
        var output = new ContextAnswerOutput([resolved], [], [], "answer", $"Blue. 【{source.Id}】", [new(source.Id, source.Content)]);
        Assert.NotEmpty(ContextAnswer.Validate(output, Input(source, [request]), [ambiguous]));
        var clarification = output with
        {
            Resolutions = [ambiguous],
            Disposition = "clarify",
            Answer = "Which pair of options do you mean?",
            AnswerEvidence = []
        };
        Assert.Empty(ContextAnswer.Validate(clarification, Input(source, [request]), [ambiguous]));
        Assert.NotEmpty(ContextAnswer.Validate(clarification with { Disposition = "answer" }, Input(source, [request]), [ambiguous]));
    }

    [Fact]
    public void Search_requires_budget_query_and_no_premature_answer()
    {
        var source = new Message();
        var output = new ContextAnswerOutput([], ["launch date"], ["original launch date"], "search", null, []);
        Assert.Empty(ContextAnswer.Validate(output, Input(source), []));
        Assert.NotEmpty(ContextAnswer.Validate(output, Input(source, search: false), []));
        Assert.NotEmpty(ContextAnswer.Validate(output with { Answer = "June 1" }, Input(source), []));
        Assert.NotEmpty(ContextAnswer.Validate(output with { Queries = [] }, Input(source), []));
    }

    [Fact]
    public void Schema_allows_only_supplied_original_messages_and_loaded_artifacts()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Mintokei.slnx")))
            root = root.Parent;
        var source = new Message
        {
            Content = "Message evidence"
        };
        var artifact = Guid.NewGuid();
        var input = Input(source);
        input["artifacts"] = J.Node(J.Write(new[] { new { id = artifact, content = "Approved artifact text", status = "approved" } }));
        var contract = JsonSchema.FromText(SchemaFiles.BindSourceIds(File.ReadAllText(SchemaFiles.PathFor(root!.FullName, "context-answer", "v1")), "context-answer", input));
        var output = new ContextAnswerOutput([], [], [], "answer", $"Approved text. 【{artifact}】", [new(artifact, "Approved artifact text")]);
        Assert.True(contract.Evaluate(J.Node(J.Write(output))).IsValid);
        Assert.Empty(ContextAnswer.Validate(output, input, []));
        Assert.False(contract.Evaluate(J.Node(J.Write(output with { AnswerEvidence = [new(Guid.NewGuid(), "unavailable")] }))).IsValid);
    }

    [Fact]
    public void Combined_mode_keeps_unsupported_transports_tools_and_legacy_configs_on_separate_route()
    {
        var c = new MemoryConfiguration();
        Assert.Equal("separate", c.AnswerMode);
        c.AnswerMode = "combined";
        Assert.NotNull(ContextAnswer.IneligibleReason(c, true));
        foreach (var op in new[]
        {
            "chat",
            "context-answer"
        }

        )
            c.Operations[op] = new("openai_api", "gpt-6-luna");
        Assert.Null(ContextAnswer.IneligibleReason(c, true));
        Assert.NotNull(ContextAnswer.IneligibleReason(c, false));
        c.Operations["chat"] = c.Operations["chat"] with
        {
            WebSearch = true
        };
        Assert.NotNull(ContextAnswer.IneligibleReason(c, true));
        c.Operations.Remove("context-answer");
        c.WithContextOperations();
        Assert.Equal(c.Operations["chat"].Model, c.Operations["context-answer"].Model);
        Assert.False(c.Operations["context-answer"].WebSearch);
    }
}
