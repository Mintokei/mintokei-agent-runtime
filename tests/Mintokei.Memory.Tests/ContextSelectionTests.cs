namespace Mintokei.Memory.Tests;
public class ContextSelectionTests
{
    private readonly SearchScope scope = new("local", "test", Guid.NewGuid(), 1, DateTimeOffset.UtcNow);
    private ArtifactVersion Version(Guid artifact, int version, string name, string status) => new()
    {
        ArtifactId = artifact,
        Version = version,
        Name = name,
        Status = status,
        Namespace = scope.Namespace,
        CreatedAt = scope.AsOf.AddMinutes(-10 + version)
    };
    [Theory]
    [InlineData("Workshop plan")]
    [InlineData("Design specification")]
    [InlineData("CV")]
    public void Version_rules_apply_to_any_document_type(string name)
    {
        var id = Guid.NewGuid();
        var original = Version(id, 1, name, "superseded");
        var approved = Version(id, 2, name, "approved");
        var draft = Version(id, 3, name, "draft");
        var versions = new[]
        {
            draft,
            original,
            approved
        };
        foreach (var(selection, expected)in new[]
        {
            ("preferred", approved),
            ("latest_approved", approved),
            ("latest", draft),
            ("latest_draft", draft),
            ("original", original)
        }

        )
        {
            var result = ContextSelection.SelectArtifacts(versions, [new(id.ToString(), selection)], scope);
            Assert.Equal(expected.Id, Assert.Single(result.Versions).Id);
            Assert.Empty(result.Missing);
        }

        Assert.Equal(original.Id, Assert.Single(ContextSelection.SelectArtifacts(versions, [new(id.ToString(), "specific", original.Id.ToString())], scope).Versions).Id);
    }

    [Fact]
    public void Draft_is_usable_by_default_but_never_invented_as_approved()
    {
        var id = Guid.NewGuid();
        var draft = Version(id, 1, "Garden plan", "draft");
        Assert.Single(ContextSelection.SelectArtifacts([draft], [new(id.ToString(), "preferred")], scope).Versions);
        var result = ContextSelection.SelectArtifacts([draft], [new(id.ToString(), "latest_approved")], scope);
        Assert.Empty(result.Versions);
        Assert.Single(result.Missing);
    }

    [Fact]
    public void Planner_ids_cannot_bypass_owner_namespace_time_or_artifact_identity()
    {
        var id = Guid.NewGuid();
        var owner = Version(id, 1, "Plan", "approved");
        owner.UserId = "other";
        var ns = Version(id, 2, "Plan", "approved");
        ns.Namespace = "other";
        var future = Version(id, 3, "Plan", "approved");
        future.CreatedAt = scope.AsOf.AddDays(1);
        var result = ContextSelection.SelectArtifacts([owner, ns, future], [new(id.ToString(), "latest")], scope);
        Assert.Empty(result.Versions);
        Assert.Single(result.Missing);
        var authorized = Version(Guid.NewGuid(), 1, "Other plan", "approved");
        Assert.Empty(ContextSelection.SelectArtifacts([authorized], [new(id.ToString(), "specific", authorized.Id.ToString())], scope).Versions);
    }

    [Theory]
    [InlineData("How does this code work?")]
    [InlineData("Help me with my dev environment")]
    [InlineData("I want to plan a vacation")]
    [InlineData("Hello, can you explain photosynthesis?")]
    public void Requests_do_not_take_the_small_talk_shortcut(string text) => Assert.False(ContextSelection.IsSmallTalk(text));
    [Fact]
    public void Legacy_plan_can_deserialize_without_new_fields()
    {
        var plan = Mintokei.Memory.Json.Read<RetrievalPlan>("{\"intent\":\"general\",\"requiredInformation\":[],\"queries\":[]}");
        Assert.Null(plan.ArtifactRequests);
        Assert.Null(plan.ProvisionalMessageIds);
    }

    [Theory]
    [InlineData("../../secrets")]
    [InlineData("/tmp")]
    public void Schema_versions_cannot_escape_the_schema_directory(string version) => Assert.Throws<ArgumentException>(() => SchemaFiles.PathFor("/app", "context-planning", version));
}
