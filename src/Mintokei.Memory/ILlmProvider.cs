using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mintokei.Memory;

public interface ILlmProvider
{
    Task<LlmResult> ExecuteStructuredAsync(LlmRequest request, CancellationToken cancellationToken);
    IAsyncEnumerable<LlmEvent> ExecuteStreamingAsync(LlmRequest request, CancellationToken cancellationToken);
}
