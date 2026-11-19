using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ConexyAI.Configuration;

// SHARE_TO_ADMIN: добавлено 2026-11-19
public class ChatShareReport_config : IEntityTypeConfiguration<ChatShareReportEntity>
{
    public void Configure(EntityTypeBuilder<ChatShareReportEntity> builder)
    {
        builder.ToTable("chat_share_report");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.ChatId).IsRequired();
        builder.Property(x => x.UserId).IsRequired();
        builder.Property(x => x.ChatTitle).HasMaxLength(200);
        builder.Property(x => x.Comment).HasMaxLength(2000);
        builder.Property(x => x.Status).HasMaxLength(20).IsRequired();
        builder.Property(x => x.AdminNote).HasMaxLength(2000);
        builder.Property(x => x.ScreenshotContentType).HasMaxLength(127);
        builder.Property(x => x.ScreenshotFileName).HasMaxLength(255);
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.UpdatedAt).IsRequired();

        // SHARE_TO_ADMIN: один активный отчёт на чат — повторный шаринг обновляет запись.
        builder.HasIndex(x => x.ChatId).IsUnique();
        builder.HasIndex(x => new { x.Status, x.CreatedAt });

        builder.HasOne<ChatEntity>()
            .WithMany()
            .HasForeignKey(x => x.ChatId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
