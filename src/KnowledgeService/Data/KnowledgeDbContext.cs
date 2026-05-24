using Microsoft.EntityFrameworkCore;

namespace SupportPoc.KnowledgeService.Data;

public sealed class KnowledgeDbContext(DbContextOptions<KnowledgeDbContext> options) : DbContext(options)
{
    public DbSet<KnowledgeDocumentEntity> Documents => Set<KnowledgeDocumentEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<KnowledgeDocumentEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(32);
            entity.Property(x => x.Title).HasMaxLength(256);
            entity.Property(x => x.Category).HasMaxLength(32);
        });
    }
}
