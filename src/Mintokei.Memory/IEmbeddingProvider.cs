using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mintokei.Memory;

public interface IEmbeddingProvider
{
    string Model { get; }

    int Dimensions { get; }

    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken);
}
