using System.Text.Json.Nodes;

namespace Mintokei.Memory.Tests;
public class ToolEvidenceTests
{
    [Fact]
    public void Imported_tool_text_does_not_prove_a_completed_action()
    {
        var m = new Message
        {
            Role = "tool",
            Content = "CV uploaded successfully",
            MetadataJson = "{\"originalId\":\"unverified-import\"}"
        };
        var c = new Candidate("c", "outcome", "CV uploaded successfully", "CV", "uploaded", null, "confirmed", "global", [new(m.Id, "supports", m.Content)]);
        Assert.NotEmpty(EvidenceValidator.ValidateCandidate(c, new Dictionary<Guid, Message> { { m.Id, m } }, new HashSet<Guid> { m.Id }, false));
        m.MetadataJson = "{\"verifiedExecution\":true}";
        Assert.Empty(EvidenceValidator.ValidateCandidate(c, new Dictionary<Guid, Message> { { m.Id, m } }, new HashSet<Guid> { m.Id }, false));
    }
}
