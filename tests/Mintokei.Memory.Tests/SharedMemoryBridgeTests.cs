namespace Mintokei.Memory.Tests;
public class MemoryTurnValidationTests
{
    private static CompletedMemoryTurn Turn() => new("mintokei", Guid.NewGuid(), Guid.NewGuid(), "Integration test", null, "completed", [new(Guid.NewGuid(), "user", "I prefer quiet hotels.", DateTimeOffset.UtcNow, "user", null)]);
    [Fact]
    public void Bridge_rejects_ownership_mismatch_missing_content_and_duplicate_evidence()
    {
        var input = Turn();
        Assert.True(MemoryTurnValidation.IsValid(input, "mintokei"));
        Assert.False(MemoryTurnValidation.IsValid(input, "another-installation"));
        Assert.False(MemoryTurnValidation.IsValid(input with { Messages = [input.Messages[0], input.Messages[0]] }, "mintokei"));
        Assert.False(MemoryTurnValidation.IsValid(input with { Messages = [input.Messages[0] with { Content = null! }] }, "mintokei"));
        Assert.False(MemoryTurnValidation.IsValid(input with { Status = "running" }, "mintokei"));
        Assert.False(MemoryTurnValidation.IsValid(input with { Messages = null! }, "mintokei"));
    }

    [Fact]
    public void Mapping_is_stable_and_different_between_source_systems()
    {
        var id = Guid.Parse("9d4fbc6e-a746-4645-a61a-dfc13b0b26b8");
        Assert.Equal(Guid.Parse("bad91a80-89b2-f798-203e-38faf89be3aa"), MemoryIdentity.SourceId("mintokei", id));
        Assert.NotEqual(MemoryIdentity.SourceId("other", id), MemoryIdentity.SourceId("mintokei", id));
    }
}
