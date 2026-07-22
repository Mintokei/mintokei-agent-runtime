using Mintokei.Sandbox.Docker;
using Xunit;

namespace Mintokei.Sandbox.Tests;

public class LocalCommandRunnerTests
{
    [Fact]
    public async Task Runs_a_command_and_captures_stdout_and_exit()
    {
        var r = await new LocalCommandRunner().RunAsync(Guid.NewGuid(), "/", "sh", ["-c", "printf hi; exit 0"], 5000);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("hi", r.Stdout);
        Assert.Null(r.Error);
    }

    [Fact]
    public async Task Captures_nonzero_exit_and_stderr()
    {
        var r = await new LocalCommandRunner().RunAsync(Guid.NewGuid(), "/", "sh", ["-c", "printf oops 1>&2; exit 3"], 5000);
        Assert.Equal(3, r.ExitCode);
        Assert.Contains("oops", r.Stderr);
    }

    [Fact]
    public async Task Missing_executable_returns_an_error_rather_than_throwing()
    {
        var r = await new LocalCommandRunner().RunAsync(Guid.NewGuid(), "/", "definitely-not-a-real-exe-xyz", [], 5000);
        Assert.NotEqual(0, r.ExitCode);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public async Task Passes_argv_without_a_shell_so_metacharacters_are_literal()
    {
        // If this went through a shell, "$(id)" would expand; via argv it stays literal.
        var r = await new LocalCommandRunner().RunAsync(Guid.NewGuid(), "/", "printf", ["%s", "$(id)"], 5000);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("$(id)", r.Stdout);
    }
}
