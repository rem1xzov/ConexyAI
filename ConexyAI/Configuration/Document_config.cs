using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ConexyAI.Configuration;

// RAG: добавлено 2026-09-17
public class Document_config : IEntityTypeConfiguration<Document>
{
    public void Configure(EntityTypeBuilder<Document> builder)
    {
        builder.ToTable("document");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.UserId).IsRequired();
        builder.Property(x => x.Title).IsRequired().HasMaxLength(500);
        builder.Property(x => x.FileName).IsRequired().HasMaxLength(500);
        builder.Property(x => x.ContentType).IsRequired().HasMaxLength(100);
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.FilePath).IsRequired();

        // GITHUB_OAUTH: добавлено 2026-09-19 — real FK to users.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // RAG isolation: list + search are always filtered by owner.
        builder.HasIndex(x => new { x.UserId, x.CreatedAt });
    }
}

// RAG: добавлено 2026-09-17
public class DocumentChunk_config : IEntityTypeConfiguration<DocumentChunk>
{
    public void Configure(EntityTypeBuilder<DocumentChunk> builder)
    {
        builder.ToTable("document_chunk");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.DocumentId).IsRequired();
        builder.Property(x => x.ChunkIndex).IsRequired();
        builder.Property(x => x.Content).IsRequired();
        builder.Property(x => x.TokensCount).IsRequired();

        builder.HasOne(x => x.Document)
            .WithMany(d => d.Chunks)
            .HasForeignKey(x => x.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.DocumentId, x.ChunkIndex });
    }
}
