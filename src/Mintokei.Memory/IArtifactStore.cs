using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mintokei.Memory;

public interface IArtifactStore
{
    Task<string> WriteAsync(Guid versionId, string content, CancellationToken ct);
    Task<string> ReadAsync(string path, CancellationToken ct);
    Task DeleteAsync(string path, CancellationToken ct);
}
