using MassTransit;
using Microsoft.EntityFrameworkCore;
using SupportPoc.AiOrchestrator.Saga;

namespace SupportPoc.AiOrchestrator.Data;

public sealed class OrchestratorDbContext(DbContextOptions<OrchestratorDbContext> options) : DbContext(options)
{
    public DbSet<TicketSuggestionSaga> TicketSuggestionSagas => Set<TicketSuggestionSaga>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();

        modelBuilder.Entity<TicketSuggestionSaga>(entity =>
        {
            entity.HasKey(x => x.CorrelationId);
            entity.Property(x => x.CurrentState).HasMaxLength(64);
            entity.Property(x => x.TicketId).HasMaxLength(32);
            entity.Property(x => x.EmployeeId).HasMaxLength(64);
            entity.Property(x => x.OriginalCategory).HasMaxLength(32);
            entity.Property(x => x.RowVersion).IsRowVersion();
            entity.HasIndex(x => x.TicketId);
        });
    }
}
