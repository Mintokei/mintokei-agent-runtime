using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mintokei.Memory;

public interface IHistorySearch
{
    Task<IReadOnlyList<SearchCandidate>> SearchAsync(SearchScope scope, string query, int limit, string temporalHint, CancellationToken ct);
}
