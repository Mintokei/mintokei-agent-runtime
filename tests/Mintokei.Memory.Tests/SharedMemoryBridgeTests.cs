namespace Mintokei.Memory.Tests;
public class SharedMemoryBridgeTests
{
    private static SharedMemoryEndpoints.SourceTurn Turn() => new("mintokei", Guid.NewGuid(), Guid.NewGuid(), "Integration test", null, "completed", [new(Guid.NewGuid(), "user", "I prefer quiet hotels.", DateTimeOffset.UtcNow, "user", null)]);
    [Fact]
    public void Bridge_rejects_ownership_mismatch_missing_content_and_duplicate_evidence()
    {
        var input = Turn();
        Assert.True(SharedMemoryEndpoints.ValidTurn(input, "mintokei"));
        Assert.False(SharedMemoryEndpoints.ValidTurn(input, "another-installation"));
        Assert.False(SharedMemoryEndpoints.ValidTurn(input with { Messages = [input.Messages[0], input.Messages[0]] }, "mintokei"));
        Assert.False(SharedMemoryEndpoints.ValidTurn(input with { Messages = [input.Messages[0] with { Content = null! }] }, "mintokei"));
        Assert.False(SharedMemoryEndpoints.ValidTurn(input with { Status = "running" }, "mintokei"));
        Assert.False(SharedMemoryEndpoints.ValidTurn(input with { Messages = null! }, "mintokei"));
    }

    [Fact]
    public void Mapping_is_stable_and_different_between_source_systems()
    {
        var id = Guid.Parse("9d4fbc6e-a746-4645-a61a-dfc13b0b26b8");
        Assert.Equal(Guid.Parse("bad91a80-89b2-f798-203e-38faf89be3aa"), SharedMemoryEndpoints.SourceId("mintokei", id));
        Assert.NotEqual(SharedMemoryEndpoints.SourceId("other", id), SharedMemoryEndpoints.SourceId("mintokei", id));
    }
}
