using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Mintokei.Memory.Postgres;

public class LocalArtifactStore(IConfiguration config) : IArtifactStore
{
    private readonly string root = Path.Combine(Path.GetFullPath(config["Memory:Root"] ?? "../.."), ".data", "artifacts");
    private string SafePath(string name)
    {
        if (Path.GetFileName(name) != name)
            throw new InvalidOperationException("Invalid artifact path");
        return Path.Combine(root, name);
    }

    public async Task<string> WriteAsync(Guid id, string content, CancellationToken ct)
    {
        Directory.CreateDirectory(root);
        var name = id + ".md";
        await File.WriteAllTextAsync(SafePath(name), content, ct);
        return name;
    }

    public Task<string> ReadAsync(string path, CancellationToken ct) => File.ReadAllTextAsync(SafePath(path), ct);
    public Task DeleteAsync(string path, CancellationToken ct)
    {
        File.Delete(SafePath(path));
        return Task.CompletedTask;
    }
}
