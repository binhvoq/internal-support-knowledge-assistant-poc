using Microsoft.EntityFrameworkCore;

namespace SupportPoc.AiOrchestrator.Data;

public sealed class OrchestratorDbContext(DbContextOptions<OrchestratorDbContext> options) : DbContext(options)
{
    public DbSet<InboxMessageEntity> InboxMessages => Set<InboxMessageEntity>();
    public DbSet<SagaLogEntryEntity> SagaLogEntries => Set<SagaLogEntryEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InboxMessageEntity>(entity =>
        {
            entity.HasKey(x => new { x.Consumer, x.EventId });
            entity.Property(x => x.Consumer).HasMaxLength(128);
            entity.Property(x => x.EventId).HasMaxLength(128);
            entity.Property(x => x.Status).HasMaxLength(32);
        });

        modelBuilder.Entity<SagaLogEntryEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.EventId).HasMaxLength(128);
            entity.Property(x => x.TicketId).HasMaxLength(64);
            entity.Property(x => x.Step).HasMaxLength(128);
            entity.Property(x => x.Status).HasMaxLength(32);
        });
    }
}
