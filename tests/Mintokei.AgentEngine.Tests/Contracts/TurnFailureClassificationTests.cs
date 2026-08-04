using Mintokei.AgentEngine.Contracts;

using Xunit;

namespace Mintokei.AgentEngine.Tests;

/// <summary>
/// What <see cref="TurnFailure.ClassifyFromText"/> makes of the failure strings the CLIs really
/// print. The strings below were taken from session stores on a working machine rather than from
/// provider documentation — the wording that matters is the wording that ships.
/// </summary>
public class TurnFailureClassificationTests
{
    [Theory]
    // Claude's five-hour limit. The one people hit, and the one that used to be Unknown: the
    // vocabulary matched "usage limit" and Claude says "session limit".
    [InlineData("You've hit your session limit · resets 7:40am (UTC)", TurnFailureKind.RateLimited)]
    [InlineData("API Error: Server is temporarily limiting requests (not your usage limit) · Rate limited",
        TurnFailureKind.RateLimited)]
    [InlineData("Not logged in · Please run /login", TurnFailureKind.Auth)]
    [InlineData("API Error: Unable to connect to API (ConnectionRefused)", TurnFailureKind.ApiError)]
    public void The_strings_the_clis_actually_print(string text, TurnFailureKind expected)
    {
        Assert.Equal(expected, TurnFailure.ClassifyFromText(text));
    }

    [Theory]
    [InlineData("429 Too Many Requests", TurnFailureKind.RateLimited)]
    [InlineData("You have exceeded your quota", TurnFailureKind.RateLimited)]
    [InlineData("Overloaded (529)", TurnFailureKind.Overloaded)]
    [InlineData("401 Unauthorized", TurnFailureKind.Auth)]
    [InlineData("Your credit balance is too low", TurnFailureKind.Auth)]
    [InlineData("Reached the maximum number of turns", TurnFailureKind.MaxTurns)]
    [InlineData("The context window limit was exceeded", TurnFailureKind.MaxTokens)]
    [InlineData("504 Bad Gateway", TurnFailureKind.ApiError)]
    [InlineData("The request timed out", TurnFailureKind.ApiError)]
    public void The_vocabularies_that_were_already_there_still_classify(
        string text, TurnFailureKind expected)
    {
        Assert.Equal(expected, TurnFailure.ClassifyFromText(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("The agent finished and wrote three files.")]
    public void Anything_that_is_not_a_recognised_failure_stays_unknown(string? text)
    {
        // Widening the vocabularies must not turn ordinary text into a diagnosis. Unknown is a
        // real answer here, not a gap to be filled.
        Assert.Equal(TurnFailureKind.Unknown, TurnFailure.ClassifyFromText(text));
    }

    [Fact]
    public void A_rate_limit_outranks_the_reachability_catch_all()
    {
        // Ordering, not coincidence: "Rate limited after the connection timed out" matches both
        // vocabularies, and the specific one has to win or every retryable limit reads as a
        // transport fault.
        Assert.Equal(
            TurnFailureKind.RateLimited,
            TurnFailure.ClassifyFromText("Rate limited after the connection timed out"));
    }
}
