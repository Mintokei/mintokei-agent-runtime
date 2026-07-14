using Microsoft.EntityFrameworkCore;

namespace Mintokei.Runner.Host.Persistence;

/// <summary>
/// Read/write OVERLAY context over the 5 runner-infra tables, sharing the same SQLite file as
/// <see cref="AppDbContext"/> — which remains the schema/migrations owner. This context NEVER
/// creates schema (no <c>EnsureCreated</c>/<c>Migrate</c> is ever called on it); it exists so the
/// runner transport/outbox/enrollment code can depend on a runner-scoped context with no product
/// or ASP.NET Identity coupling, ahead of extracting that code into <c>Mintokei.Runner.Host</c>.
///
/// The mapping + value conversions below MUST match <see cref="AppDbContext"/> exactly, or reads
/// and writes against the shared file diverge (e.g. an enum stored as int here but string there).
/// See <c>docs/runner-host-extraction-plan.md</c> §5-6. The cross-boundary relationships
/// (Workspace / Agent / RunnerMachineCli → RunnerMachine) live in <see cref="AppDbContext"/> only;
/// here the navigations that reach those product tables are ignored.
/// </summary>
public sealed class RunnerHostDbContext(DbContextOptions<RunnerHostDbContext> options) : DbContext(options)
{
    public DbSet<RunnerMachine> RunnerMachines => Set<RunnerMachine>();
    public DbSet<EnrollmentToken> EnrollmentTokens => Set<EnrollmentToken>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<RunnerOutboxChannel> RunnerOutboxChannels => Set<RunnerOutboxChannel>();
    public DbSet<InboundRunnerMessage> InboundRunnerMessages => Set<InboundRunnerMessage>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Matches AppDbContext: SQLite has no native DateTimeOffset — store as ISO 8601 strings.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<string>();
        configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<string>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<RunnerMachine>(entity =>
        {
            entity.HasIndex(m => m.SecretHash).IsUnique().HasFilter("\"SecretHash\" IS NOT NULL");
            entity.Property(m => m.Status).HasConversion<string>();
        });

        modelBuilder.Entity<EnrollmentToken>(entity =>
        {
            entity.HasIndex(e => e.TokenHash).IsUnique();
            entity.HasIndex(e => e.ExpiresAt);
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.HasOne(m => m.RunnerMachine)
                .WithMany(r => r.OutboxMessages)
                .HasForeignKey(m => m.RunnerMachineId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(m => new { m.RunnerMachineId, m.SequenceNumber }).IsUnique();
            entity.HasIndex(m => new { m.RunnerMachineId, m.Status });
            entity.Property(m => m.MessageType).HasConversion<string>();
            entity.Property(m => m.Status).HasConversion<string>();
            entity.Property(m => m.ExpiresAt).HasConversion<string>();
            entity.Property(m => m.AckedAt).HasConversion<string>();
            entity.Property(m => m.CreatedAt).HasConversion<string>();
            entity.Property(m => m.SentAt).HasConversion<string>();
            entity.Property(m => m.DeliverAfterUtc).HasConversion<string>();
        });

        modelBuilder.Entity<InboundRunnerMessage>(entity =>
        {
            entity.HasOne(m => m.RunnerMachine)
                .WithMany()
                .HasForeignKey(m => m.RunnerMachineId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(m => new { m.RunnerMachineId, m.SequenceNumber }).IsUnique();
            entity.Property(m => m.MessageType).HasConversion<string>();
            entity.Property(m => m.ReceivedAt).HasConversion<string>();
        });

        modelBuilder.Entity<RunnerOutboxChannel>(entity =>
        {
            entity.HasOne(c => c.RunnerMachine)
                .WithMany(m => m.OutboxChannels)
                .HasForeignKey(c => c.RunnerMachineId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(c => new { c.RunnerMachineId, c.CorrelationId }).IsUnique();
        });
    }
}
