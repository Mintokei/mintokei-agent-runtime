using Xunit;

namespace Mintokei.AgentEngine.Tests;

/// <summary>
/// Verifies the normalized views over the interactive user-interaction leaves — questions and
/// permission suggestions. Sample shapes are taken from real prod rows (Claude questions +
/// addRules/setMode/addDirectories suggestions, Codex mcpSessionScope).
/// </summary>
public class InteractionNormalizersTests
{
    // ── Questions ──

    [Fact]
    public void Questions_ParsesQuestionHeaderOptionsAndMultiSelect()
    {
        var raw = """
            [{"question":"What is your TP URL?","header":"TP URL","multiSelect":false,
              "options":[{"label":"Will provide now","description":"I'll paste it"},
                         {"label":"Check skill","description":"maybe configured","preview":"code"}]}]
            """;
        var q = Assert.Single(QuestionNormalizer.Parse(raw));
        Assert.Equal("What is your TP URL?", q.Question);
        Assert.Equal("TP URL", q.Header);
        Assert.False(q.MultiSelect);
        Assert.Equal(2, q.Options.Count);
        Assert.Equal(new QuestionOption("Will provide now", "I'll paste it", null), q.Options[0]);
        Assert.Equal(new QuestionOption("Check skill", "maybe configured", "code"), q.Options[1]);
    }

    [Fact]
    public void Questions_MultiSelectTrue_IsHonored()
    {
        var q = Assert.Single(QuestionNormalizer.Parse(
            """[{"question":"Pick features","header":"Feat","multiSelect":true,"options":[{"label":"A","description":"a"}]}]"""));
        Assert.True(q.MultiSelect);
    }

    [Fact]
    public void Questions_NullOrInvalid_IsEmpty()
    {
        Assert.Empty(QuestionNormalizer.Parse(null));
        Assert.Empty(QuestionNormalizer.Parse("not json"));
        Assert.Empty(QuestionNormalizer.Parse("{}"));
    }

    [Fact]
    public void UserInteractionData_QuestionList_DerivesFromQuestions()
    {
        var ui = new UserInteractionData
        {
            RequestId = "0",
            Questions = """[{"question":"Q?","header":"H","multiSelect":false,"options":[]}]""",
        };
        Assert.Equal("Q?", Assert.Single(ui.QuestionList).Question);
    }

    // ── Suggestions ──

    [Fact]
    public void Suggestions_AddRules_ParsesBehaviorDestinationAndRules()
    {
        var raw = """[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"grep:*"},{"toolName":"WebSearch"}],"behavior":"allow","destination":"localSettings"}]""";
        var s = Assert.Single(SuggestionNormalizer.Parse(raw));
        Assert.Equal("addRules", s.Type);
        Assert.Equal("allow", s.Behavior);
        Assert.Equal("localSettings", s.Destination);
        Assert.Equal(new SuggestionRule("Bash", "grep:*"), s.Rules[0]);
        Assert.Equal(new SuggestionRule("WebSearch", null), s.Rules[1]);
        Assert.Empty(s.Directories);
    }

    [Fact]
    public void Suggestions_SetMode_And_AddDirectories()
    {
        var setMode = Assert.Single(SuggestionNormalizer.Parse(
            """[{"type":"setMode","mode":"acceptEdits","destination":"session"}]"""));
        Assert.Equal("setMode", setMode.Type);
        Assert.Equal("acceptEdits", setMode.Mode);

        var addDirs = Assert.Single(SuggestionNormalizer.Parse(
            """[{"type":"addDirectories","directories":["/a","/b"],"destination":"localSettings"}]"""));
        Assert.Equal(new[] { "/a", "/b" }, addDirs.Directories);
    }

    [Fact]
    public void Suggestions_CodexBareMcpSessionScope_HasTypeOnly()
    {
        var s = Assert.Single(SuggestionNormalizer.Parse("""[{"type":"mcpSessionScope"}]"""));
        Assert.Equal("mcpSessionScope", s.Type);
        Assert.Null(s.Behavior);
        Assert.Null(s.Mode);
        Assert.Empty(s.Rules);
        Assert.Empty(s.Directories);
    }

    [Fact]
    public void UserInteractionData_SuggestionList_DerivesFromSuggestions()
    {
        var ui = new UserInteractionData
        {
            RequestId = "0",
            Suggestions = """[{"type":"mcpSessionScope"}]""",
        };
        Assert.Equal("mcpSessionScope", Assert.Single(ui.SuggestionList).Type);
        Assert.Empty(new UserInteractionData { RequestId = "0" }.SuggestionList);
    }
}
