using ConexyAI.Entity;
using ConexyAI.Contract;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ConexyAI.Configuration;

// GOAL: добавлено 2026-11-20 — таблица целей чата (см. ChatGoalEntity).
public class ChatGoal_config : IEntityTypeConfiguration<ChatGoalEntity>
{
    public void Configure(EntityTypeBuilder<ChatGoalEntity> builder)
    {
        builder.ToTable("chat_goal");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.ChatId).IsRequired();
        builder.Property(x => x.UserId).IsRequired();
        builder.Property(x => x.Objective).IsRequired().HasMaxLength(4000);
        builder.Property(x => x.Status).IsRequired().HasMaxLength(20);
        builder.Property(x => x.MaxRounds).IsRequired();
        builder.Property(x => x.RoundsStarted).IsRequired();
        builder.Property(x => x.BlockedReason).HasMaxLength(600);
        builder.Property(x => x.LastNote).HasMaxLength(2000);
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.UpdatedAt).IsRequired();

        // Цель удаляется вместе с чатом; пользователь в FK не участвует отдельно (владелец — тот же).
        builder.HasOne<ChatEntity>()
            .WithMany()
            .HasForeignKey(x => x.ChatId)
            .OnDelete(DeleteBehavior.Cascade);

        // Драйвер ищет активные цели, API — цель конкретного чата.
        builder.HasIndex(x => new { x.ChatId, x.Status });
        builder.HasIndex(x => x.Status);
    }
}
