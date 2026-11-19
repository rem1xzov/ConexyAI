using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ConexyAI.Configuration;

// SHARE_TO_ADMIN: добавлено 2026-11-19
public class ChatAgentEvent_config : IEntityTypeConfiguration<ChatAgentEventEntity>
{
    public void Configure(EntityTypeBuilder<ChatAgentEventEntity> builder)
    {
        builder.ToTable("chat_agent_event");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.ChatId).IsRequired();
        builder.Property(x => x.Kind).HasMaxLength(30).IsRequired();
        builder.Property(x => x.Tool).HasMaxLength(80);
        // ARGS/OUTPUT: обрезаются сервисом; колонки без лимита, чтобы не терять хвост ошибки.
        builder.Property(x => x.CreatedAt).IsRequired();

        builder.HasIndex(x => new { x.ChatId, x.CreatedAt });
        builder.HasIndex(x => x.TaskId);

        builder.HasOne<ChatEntity>()
            .WithMany()
            .HasForeignKey(x => x.ChatId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
