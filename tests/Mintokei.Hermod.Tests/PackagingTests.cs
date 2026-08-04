using System.Xml.Linq;

using Xunit;

namespace Mintokei.Hermod.Tests;

/// <summary>
/// The install instructions name two different strings — the package you ask for and the command
/// you get — and neither is checked by building. Both come from the csproj, and one of them already
/// went wrong: <c>PackageId</c> follows <c>AssemblyName</c> unless it is set, so the first pack
/// produced <c>hermod</c>, outside the reserved <c>Mintokei.</c> prefix and unlike every other
/// package in the family. A build cannot notice that, and a README that names the wrong id is only
/// discovered by someone whose install fails.
/// </summary>
public class PackagingTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly XElement Project =
        XDocument.Load(Path.Combine(RepoRoot, "tools/Mintokei.Hermod/Mintokei.Hermod.csproj")).Root!;

    private static string PackageId => Property("PackageId");
    private static string Command => Property("ToolCommandName");

    [Fact]
    public void The_project_packs_as_a_dotnet_tool()
    {
        // Without these the csproj still builds and still runs from a clone — it just quietly stops
        // producing anything anyone can install as a command. IsPackable=false is how it shipped
        // for months, and it makes PackAsTool a no-op rather than an error.
        Assert.True(Is("true", Property("PackAsTool")), "the project no longer sets <PackAsTool>true</PackAsTool>");
        Assert.False(Is("false", Property("IsPackable", fallback: "true")), "<IsPackable>false</IsPackable> would leave the tool unpublished");
    }

    [Fact]
    public void The_command_is_the_executable_it_ships()
    {
        // A ToolCommandName that differs from the assembly is legal and works, but then the binary
        // in the package is named one thing and the README promises another.
        Assert.Equal(Property("AssemblyName"), Command);
    }

    [Fact]
    public void The_package_id_stays_inside_the_family_prefix()
    {
        // nuget.org badges a package as the owner's only when its id matches a reserved prefix, and
        // every other package here is Mintokei.*.
        Assert.StartsWith("Mintokei.", PackageId, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tools/README.md")]
    [InlineData("tools/Mintokei.Hermod/README.md")]
    public void The_readmes_tell_you_to_install_the_package_that_exists(string path)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot, path));

        Assert.Contains($"dotnet tool install -g {PackageId}", text, StringComparison.Ordinal);
        Assert.Contains(Command, text, StringComparison.Ordinal);
    }

    private static bool Is(string expected, string actual) =>
        string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

    private static string Property(string name, string? fallback = null) =>
        Project.Descendants(name).FirstOrDefault()?.Value
        ?? fallback
        ?? throw new Xunit.Sdk.XunitException($"the csproj sets no <{name}>");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mintokei.slnx")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("no Mintokei.slnx above the test binary");
    }
}
