using Microsoft.EntityFrameworkCore;

namespace Mintokei.Runner.Host.Persistence;

/// <summary>
/// Read/write context over the five runner-infrastructure tables used by enrollment, runner presence,
/// and the durable outbox. A host can point this at its own database or at a standalone store; the
/// important constraint is that every code path touching these tables uses the same mapping and value
/// conversions.
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
        // SQLite has no native DateTimeOffset type, so persist these as ISO 8601 strings.
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
