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

        builder.Property(x => x.Role)
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(x => x.Content)
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .IsRequired();

        builder.HasIndex(x => new { x.ChatId, x.CreatedAt });
    }
}
