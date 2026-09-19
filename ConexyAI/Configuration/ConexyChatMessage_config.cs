using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ConexyAI.Configuration;

public class ConexyChatMessage_config : IEntityTypeConfiguration<ConexyChatMessageEntity>
{
    public void Configure(EntityTypeBuilder<ConexyChatMessageEntity> builder)
    {
        builder.ToTable("conexy_chat_message");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.ChatId)
            .IsRequired();

        // GITHUB_OAUTH: добавлено 2026-09-19 — owning user + real FK to users.
        builder.Property(x => x.UserId)
            .IsRequired();

        builder.Property(x => x.Role)
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(x => x.Content)
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .IsRequired();

        // GITHUB_OAUTH: добавлено 2026-09-19 — real FK to users.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.ChatId, x.CreatedAt });
    }
}
