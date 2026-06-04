using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace SupportPoc.AiOrchestrator.Data;

public sealed class OrchestratorDbContext(DbContextOptions<OrchestratorDbContext> options) : DbContext(options)
{
    public DbSet<AutoSuggestionJob> AutoSuggestionJobs => Set<AutoSuggestionJob>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();

        modelBuilder.Entity<AutoSuggestionJob>(entity =>
        {
            entity.HasKey(x => x.JobId);
            entity.Property(x => x.TicketId).HasMaxLength(32);
            entity.Property(x => x.EmployeeId).HasMaxLength(64);
            entity.Property(x => x.Category).HasMaxLength(32);
            entity.Property(x => x.Status).HasMaxLength(32);
            entity.HasIndex(x => x.TicketId);
        });
    }
}
