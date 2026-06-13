using MassTransit;
using Microsoft.EntityFrameworkCore;
using SupportPoc.AiOrchestrator.Saga;

namespace SupportPoc.AiOrchestrator.Data;

public sealed class OrchestratorDbContext(DbContextOptions<OrchestratorDbContext> options) : DbContext(options)
{
    public DbSet<TicketSuggestionSaga> TicketSuggestionSagas => Set<TicketSuggestionSaga>();
    public DbSet<AiGenerationAttemptEntity> AiGenerationAttempts => Set<AiGenerationAttemptEntity>();
    public DbSet<SagaReconciliationItem> SagaReconciliationItems => Set<SagaReconciliationItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();

        modelBuilder.Entity<AiGenerationAttemptEntity>(entity =>
        {
            entity.HasKey(x => x.AttemptId);
            entity.Property(x => x.TicketId).HasMaxLength(32);
            entity.Property(x => x.Question).HasMaxLength(4000);
            entity.Property(x => x.RequestedCategory).HasMaxLength(32);
            entity.Property(x => x.Status).HasMaxLength(16);
            entity.Property(x => x.Category).HasMaxLength(32);
            entity.Property(x => x.LeaseOwner).HasMaxLength(64);
            entity.Property(x => x.RelatedDocumentsJson).HasDefaultValue("[]");
            entity.Property(x => x.RowVersion).IsRowVersion().HasDefaultValue(new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 });
            entity.HasIndex(x => new { x.Status, x.NextRunAt, x.LeaseUntil });
        });

        modelBuilder.Entity<TicketSuggestionSaga>(entity =>
        {
            entity.HasKey(x => x.CorrelationId);
            entity.Property(x => x.CurrentState).HasMaxLength(64);
            entity.Property(x => x.TicketId).HasMaxLength(32);
            entity.Property(x => x.EmployeeId).HasMaxLength(64);
            entity.Property(x => x.OriginalCategory).HasMaxLength(32);
            entity.Property(x => x.RowVersion).IsRowVersion().HasDefaultValue(new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 });
            entity.HasIndex(x => x.TicketId);
        });

        modelBuilder.Entity<SagaReconciliationItem>(entity =>
        {
            entity.HasKey(x => x.SagaId);
            entity.Property(x => x.TicketId).HasMaxLength(32);
            entity.Property(x => x.Reason).HasMaxLength(2000);
            entity.Property(x => x.Resolution).HasMaxLength(64);
            entity.HasIndex(x => x.ResolvedAt);
        });
    }
}
