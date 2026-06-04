using Microsoft.EntityFrameworkCore;

namespace SupportPoc.KnowledgeService.Data;

public sealed class KnowledgeDbContext(DbContextOptions<KnowledgeDbContext> options) : DbContext(options)
{
    public DbSet<KnowledgeDocumentEntity> Documents => Set<KnowledgeDocumentEntity>();
    public DbSet<IdempotencyRecordEntity> IdempotencyRecords => Set<IdempotencyRecordEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<KnowledgeDocumentEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(32);
            entity.Property(x => x.Title).HasMaxLength(256);
            entity.Property(x => x.Category).HasMaxLength(32);
            entity.Property(x => x.FileName).HasMaxLength(256);
            entity.Property(x => x.ContentType).HasMaxLength(128);
            entity.Property(x => x.IngestionStatus).HasMaxLength(32);
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
