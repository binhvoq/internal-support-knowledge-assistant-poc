using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace SupportPoc.TicketService.Data;

public sealed class TicketDbContext(DbContextOptions<TicketDbContext> options) : DbContext(options)
{
    public DbSet<TicketEntity> Tickets => Set<TicketEntity>();
    public DbSet<IdempotencyRecordEntity> IdempotencyRecords => Set<IdempotencyRecordEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();

        modelBuilder.Entity<TicketEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(32);
            entity.Property(x => x.EmployeeId).HasMaxLength(64);
            entity.Property(x => x.OwnerOid).HasMaxLength(64);
            entity.Property(x => x.Category).HasMaxLength(32);
            entity.Property(x => x.Status).HasMaxLength(32);
        });

        modelBuilder.Entity<IdempotencyRecordEntity>(entity =>
        {
            entity.HasKey(x => new { x.Scope, x.Key });
            entity.Property(x => x.Scope).HasMaxLength(128);
            entity.Property(x => x.Key).HasMaxLength(128);
            entity.Property(x => x.RequestHash).HasMaxLength(128);
        });
    }
}
