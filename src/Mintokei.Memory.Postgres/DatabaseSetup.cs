using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Mintokei.Memory.Postgres;

public static class DatabaseSetup
{
    public static async Task InitializeAsync(MemoryDbContext db, string root, MemoryConfiguration? initialConfiguration = null)
    {
        MemoryAssets.EnsureFiles(root);
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE EXTENSION IF NOT EXISTS vector;
            CREATE EXTENSION IF NOT EXISTS pg_trgm;
            CREATE INDEX IF NOT EXISTS messages_fts ON "Messages" USING gin(to_tsvector('english', "Content"));
            CREATE INDEX IF NOT EXISTS messages_trgm ON "Messages" USING gin("Content" gin_trgm_ops);
            CREATE INDEX IF NOT EXISTS memories_fts ON "Memories" USING gin(to_tsvector('english', "CanonicalText"));
            CREATE TABLE IF NOT EXISTS memory_embeddings (
                memory_id uuid PRIMARY KEY REFERENCES "Memories"("Id") ON DELETE CASCADE,
                model text NOT NULL, dimensions integer NOT NULL, embedding vector NOT NULL,
                duration_ms double precision NOT NULL, created_at timestamptz NOT NULL DEFAULT now());
            CREATE INDEX IF NOT EXISTS memory_embeddings_model ON memory_embeddings(model,dimensions);
            """);
        await using var seed = await db.Database.BeginTransactionAsync();
        // Keep the original advisory key so older hosts and the runtime serialize seeding together.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended('playground:seed-prompts',0))");
        if (!await db.Settings.AnyAsync())
            db.Settings.Add(new Setting { ValueJson = Json.Write(initialConfiguration ?? new MemoryConfiguration()) });
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "prompts"), "*.txt", SearchOption.AllDirectories))
        {
            var operation = Path.GetFileNameWithoutExtension(file);
            var version = Path.GetFileName(Path.GetDirectoryName(file))!;
            if (version == "prompts")
                version = "v1"; // Support older source layouts without rewriting stored versions.
            if (!await db.Prompts.AnyAsync(x => x.Operation == operation && x.Version == version))
                db.Prompts.Add(new PromptVersion { Operation = operation, Version = version, Template = await File.ReadAllTextAsync(file) });
        }

        await db.SaveChangesAsync();
        await seed.CommitAsync();
    }
}
