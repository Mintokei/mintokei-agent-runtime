using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Mintokei.Memory.Postgres;

public class OllamaEmbeddingProvider(HttpClient http, IConfiguration config) : IEmbeddingProvider
{
    public string Model => config["Memory:EmbeddingModel"] ?? "nomic-embed-text";
    public int Dimensions => config.GetValue("Memory:EmbeddingDimensions", 768);

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync((config["Memory:EmbeddingUrl"] ?? "http://localhost:11435") + "/api/embed", new { model = Model, input = text, truncate = true }, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonNode>(ct);
        var vector = body!["embeddings"]![0]!.AsArray().Select(x => x!.GetValue<float>()).ToArray();
        if (vector.Length != Dimensions || vector.Any(x => !float.IsFinite(x)))
            throw new InvalidOperationException($"Embedding model {Model} returned incompatible dimensions or nonfinite values.");
        return vector;
    }
}
