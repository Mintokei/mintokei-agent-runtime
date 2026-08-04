using System.Text.Json;

using Mintokei.Hermod;

using Xunit;

namespace Mintokei.Hermod.Tests;

/// <summary>
/// Summarising has two independent axes — when it happens, and who writes it — and the point of
/// splitting them was that every combination works. So the matrix is asserted rather than the two
/// cases that happen to be interesting.
/// </summary>
public class SummaryConfigTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static SummarySettings Parse(string json) =>
        JsonSerializer.Deserialize<SummarySettings>(json, Json)!;

    // ── the when axis ────────────────────────────────────────────────────

    [Theory]
    [InlineData("\"always\"", SummaryWhen.Always, 0)]
    [InlineData("\"never\"", SummaryWhen.Never, 0)]
    [InlineData("\"Always\"", SummaryWhen.Always, 0)]
    [InlineData("400", SummaryWhen.Over, 400)]
    [InlineData("\"400\"", SummaryWhen.Over, 400)]
    public void When_reads_a_keyword_or_a_count(string raw, SummaryWhen expected, int threshold)
    {
        var settings = Parse($$"""{"when": {{raw}}}""");

        Assert.Equal(expected, settings.When.When);
        Assert.Equal(threshold, settings.When.Threshold);
    }

    [Theory]
    [InlineData("\"sometimes\"")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("true")]
    public void An_unusable_when_is_an_error_rather_than_a_default(string raw)
    {
        // Falling back to "never" would move a whole transcript into a target that cannot hold it,
        // which reads as the tool losing the conversation rather than as a config mistake.
        Assert.Throws<JsonException>(() => Parse($$"""{"when": {{raw}}}"""));
    }

    [Theory]
    [InlineData(SummaryWhen.Never, 0, 5, false)]
    [InlineData(SummaryWhen.Never, 0, 5000, false)]
    [InlineData(SummaryWhen.Always, 0, 1, true)]
    [InlineData(SummaryWhen.Always, 0, 5000, true)]
    [InlineData(SummaryWhen.Over, 400, 400, false)]   // "over" is strict
    [InlineData(SummaryWhen.Over, 400, 401, true)]
    public void Applies_is_decided_by_the_trigger_alone(
        SummaryWhen when, int threshold, int messages, bool expected)
    {
        var trigger = new SummaryTrigger(when, threshold);

        Assert.Equal(expected, trigger.Applies(messages));
    }

    // ── the with axis ────────────────────────────────────────────────────

    [Fact]
    public void The_mechanical_summariser_is_the_default()
    {
        var settings = Parse("""{"when": "always"}""");

        Assert.True(settings.IsMechanical);
        Assert.True(settings.KeepFacts);
    }

    [Theory]
    [InlineData("\"always\"")]
    [InlineData("\"never\"")]
    [InlineData("400")]
    public void Either_summariser_can_be_triggered_either_way(string when)
    {
        // The whole point of the split: six cells, no special cases.
        foreach (var who in new[] { SummarySettings.Mechanical, "claude-fast" })
        {
            var settings = Parse($$"""{"when": {{when}}, "with": "{{who}}"}""");

            Assert.Equal(who, settings.With);
            Assert.Equal(SummaryTrigger.Parse(when.Trim('"')), settings.When);
        }
    }

    // ── the config as a whole ────────────────────────────────────────────

    [Fact]
    public void Summarising_is_off_when_nothing_says_otherwise()
    {
        // The default is to move the real transcript. A briefing is lossy, and losing a
        // conversation should never be what happens when you say nothing.
        var config = JsonSerializer.Deserialize<MoveConfig>("""{"profiles": {}}""", Json)!;

        Assert.Equal(SummaryWhen.Never, config.EffectiveSummary().When.When);
    }

    [Fact]
    public void The_old_summariseOver_still_means_what_it_meant()
    {
        var config = JsonSerializer.Deserialize<MoveConfig>("""{"summariseOver": 400}""", Json)!;

        var summary = config.EffectiveSummary();
        Assert.Equal(SummaryTrigger.Over(400), summary.When);
        Assert.True(summary.IsMechanical);
    }

    [Fact]
    public void Setting_both_shapes_is_an_error_rather_than_a_precedence_rule()
    {
        // Picking one silently leaves the user believing a threshold applied that did not.
        var config = JsonSerializer.Deserialize<MoveConfig>(
            """{"summariseOver": 400, "summary": {"when": "always"}}""", Json)!;

        var ex = Assert.Throws<InvalidOperationException>(() => config.EffectiveSummary());
        Assert.Contains("summariseOver", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_starter_config_parses_and_leaves_summarising_off()
    {
        var config = JsonSerializer.Deserialize<MoveConfig>(MoveConfig.Sample, Json)!;

        Assert.NotEmpty(config.Profiles);
        Assert.Equal(SummaryWhen.Never, config.EffectiveSummary().When.When);
    }
}
