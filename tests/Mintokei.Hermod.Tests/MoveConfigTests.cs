using Mintokei.Hermod;

using Xunit;

namespace Mintokei.Hermod.Tests;

public class MoveConfigTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hermod-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string json)
    {
        var path = Path.Combine(_dir, "hermod.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void A_broken_config_stops_the_run_rather_than_falling_back()
    {
        // Falling back silently would launch an agent with defaults the user believed they had
        // overridden — including permissions.
        var path = Write("{ not json");

        var ex = Assert.Throws<InvalidOperationException>(() => MoveConfig.Load(path));
        Assert.Contains(path, ex.Message);
    }

    [Fact]
    public void A_missing_explicit_config_is_an_error_not_a_default()
    {
        var missing = Path.Combine(_dir, "nope.json");
        Assert.Throws<InvalidOperationException>(() => MoveConfig.Load(missing));
    }

    [Fact]
    public void Comments_and_trailing_commas_are_allowed_because_the_starter_file_has_them()
    {
        var path = Write("""
            {
              // a profile
              "profiles": { "codex": { "tool": "codex", "config": { "sandbox": "read-only" } } },
            }
            """);

        var (config, origin) = MoveConfig.Load(path);

        Assert.Equal(path, origin);
        Assert.Equal("read-only", config.Profiles["codex"].Config["sandbox"]);
    }

    [Fact]
    public void An_absent_handoff_is_null_and_an_empty_one_is_empty()
    {
        // hermod distinguishes them: absent means "use the built-in wording", "" means send
        // nothing. HandoffPrompt.Render treats blank as the default, so the difference has to
        // survive deserialisation to be actionable.
        var absent = MoveConfig.Load(Write("""{ "profiles": {} }""")).Config;
        Assert.Null(absent.Handoff);

        var empty = MoveConfig.Load(Write("""{ "profiles": {}, "handoff": "" }""")).Config;
        Assert.Equal("", empty.Handoff);
    }

    [Fact]
    public void The_starter_file_it_writes_is_one_it_can_read()
    {
        var path = Path.Combine(_dir, "starter.json");
        File.WriteAllText(path, MoveConfig.Sample);

        var (config, _) = MoveConfig.Load(path);

        Assert.NotEmpty(config.Profiles);
        foreach (var (name, profile) in config.Profiles)
        {
            var tool = profile.ToolKey;   // throws on an unknown tool name
            var unknown = Backends.Unknown(tool, profile.Config);
            Assert.True(unknown.Count == 0,
                $"the starter config's '{name}' profile sets {string.Join(", ", unknown.Select(u => u.Key))}, "
                + $"which {profile.Tool} does not understand.");
            foreach (var key in profile.Config.Keys)
                Assert.False(Backends.Unsupported(key, out _),
                    $"the starter config's '{name}' profile sets '{key}', which hermod refuses.");
        }
    }

    [Fact]
    public void The_built_in_profiles_are_ones_hermod_will_accept()
    {
        // These apply when there is no config file at all, so a bad key here breaks the tool for
        // anyone who has not configured it.
        foreach (var (name, profile) in MoveConfig.Fallback.Profiles)
        {
            var unknown = Backends.Unknown(profile.ToolKey, profile.Config);
            Assert.True(unknown.Count == 0,
                $"built-in profile '{name}' sets {string.Join(", ", unknown.Select(u => u.Key))}, "
                + $"which {profile.Tool} does not understand.");
        }
    }

    [Fact]
    public void A_profile_naming_an_unknown_tool_says_which_ones_exist()
    {
        var profile = new Profile { Tool = "cloud" };
        var ex = Assert.Throws<InvalidOperationException>(() => profile.ToolKey);
        Assert.Contains("claude", ex.Message);
    }

    [Fact]
    public void Permission_settings_are_listed_for_the_confirmation()
    {
        var profile = new Profile { Tool = "codex" };
        profile.Config["sandbox"] = "read-only";
        profile.Config["model"] = "gpt-5.5";

        // The model is not a permission; the sandbox is, and is what gets announced before acting.
        Assert.Equal(["sandbox=read-only"], profile.PermissionSettings());
    }
}
