using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Mintokei.Memory.Postgres;

public class MemoryDbContext(DbContextOptions<MemoryDbContext> options) : DbContext(options)
{
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MemoryRecord> Memories => Set<MemoryRecord>();
    public DbSet<MemorySource> MemorySources => Set<MemorySource>();
    public DbSet<MemoryRevision> MemoryRevisions => Set<MemoryRevision>();
    public DbSet<StateSnapshot> States => Set<StateSnapshot>();
    public DbSet<KnowledgeRecord> Knowledge => Set<KnowledgeRecord>();
    public DbSet<ArtifactVersion> Artifacts => Set<ArtifactVersion>();
    public DbSet<ResolutionRecord> Resolutions => Set<ResolutionRecord>();
    public DbSet<TraceSpan> Traces => Set<TraceSpan>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<PromptVersion> Prompts => Set<PromptVersion>();
    public DbSet<Experiment> Experiments => Set<Experiment>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Message>().HasIndex(x => new { x.ConversationId, x.SequenceNumber }).IsUnique();
        b.Entity<Message>().HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<MemoryRecord>().HasIndex(x => new { x.UserId, x.Namespace, x.CanonicalHash }).IsUnique();
        b.Entity<MemorySource>().HasIndex(x => new { x.MemoryId, x.SourceId, x.SourceRole }).IsUnique();
        b.Entity<MemorySource>().HasOne<MemoryRecord>().WithMany().HasForeignKey(x => x.MemoryId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<MemoryRevision>().HasOne<MemoryRecord>().WithMany().HasForeignKey(x => x.MemoryId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<StateSnapshot>().HasIndex(x => new { x.ConversationId, x.Version }).IsUnique();
        b.Entity<StateSnapshot>().HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Job>().HasIndex(x => x.Key).IsUnique();
        b.Entity<Job>().HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Setting>().HasIndex(x => x.Key).IsUnique();
        b.Entity<PromptVersion>().HasIndex(x => new { x.Operation, x.Version }).IsUnique();
        b.Entity<ArtifactVersion>().HasIndex(x => new { x.UserId, x.Namespace, x.ArtifactId, x.Version }).IsUnique();
        foreach (var entity in b.Model.GetEntityTypes())
        {
            foreach (var p in entity.GetProperties())
                if (p.Name.EndsWith("Json"))
                    p.SetColumnType("jsonb");
        }
    }
}
