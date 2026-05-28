using MassTransit;
using Microsoft.EntityFrameworkCore;
using SupportPoc.AiOrchestrator.Saga;

namespace SupportPoc.AiOrchestrator.Data;

public sealed class OrchestratorDbContext(DbContextOptions<OrchestratorDbContext> options) : DbContext(options)
{
    // Saga instance state - mot row mot saga dang chay.
    public DbSet<TicketSuggestionState> TicketSuggestionStates => Set<TicketSuggestionState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // MassTransit them 3 bang: InboxState, OutboxMessage, OutboxState.
        // - InboxState: dedupe MessageId per endpoint (step-level idempotency).
        // - OutboxMessage: hang doi message cho relay -> broker (fix dual-write).
        // - OutboxState: bookkeeping cua relay worker.
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();

        modelBuilder.Entity<TicketSuggestionState>(entity =>
        {
            entity.HasKey(x => x.CorrelationId);
            entity.Property(x => x.CurrentState).HasMaxLength(64);
            entity.Property(x => x.TicketId).HasMaxLength(32);
            entity.Property(x => x.EmployeeId).HasMaxLength(64);
            entity.Property(x => x.Category).HasMaxLength(32);
            entity.Property(x => x.OriginalStatus).HasMaxLength(32);
            entity.Property(x => x.TicketSagaEpoch).HasDefaultValue(0);
            // Concurrency token cho optimistic concurrency.
            entity.Property(x => x.Version).IsConcurrencyToken();
        });
    }
}
