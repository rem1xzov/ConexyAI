using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ConexyAI.Configuration;

// SUPPORT: добавлено 2026-09-19
public class SupportTicket_config : IEntityTypeConfiguration<SupportTicket>
{
    public void Configure(EntityTypeBuilder<SupportTicket> builder)
    {
        builder.ToTable("support_ticket");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.UserId).IsRequired();
        builder.Property(x => x.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.LastMessageAt).IsRequired();
        // SUPPORT_BOT: отметки эскалации/закрытия и флаги активности бота/оператора.
        builder.Property(x => x.EscalatedAt).IsRequired(false);
        builder.Property(x => x.ClosedAt).IsRequired(false);
        builder.Property(x => x.BotActive).IsRequired();
        builder.Property(x => x.AdminActive).IsRequired();
        // SUPPORT_MULTI: тема и отметка прочтения — для списка обращений в стиле мессенджера.
        builder.Property(x => x.Subject).HasMaxLength(120).IsRequired(false);
        builder.Property(x => x.UserLastReadAt).IsRequired(false);

        builder.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.Status, x.LastMessageAt });
        builder.HasIndex(x => new { x.UserId, x.LastMessageAt });
    }
}

// SUPPORT_ATTACHMENTS: добавлено 2026-10-18
public class SupportAttachment_config : IEntityTypeConfiguration<SupportAttachment>
{
    public void Configure(EntityTypeBuilder<SupportAttachment> builder)
    {
        builder.ToTable("support_attachment");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.FileName).HasMaxLength(255).IsRequired();
        builder.Property(x => x.ContentType).HasMaxLength(127).IsRequired();
        builder.Property(x => x.Size).IsRequired();
        builder.Property(x => x.Data).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.HasOne(x => x.Message)
            .WithMany(m => m.Attachments)
            .HasForeignKey(x => x.MessageId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => x.MessageId);
    }
}

// SUPPORT: добавлено 2026-09-19
public class SupportMessage_config : IEntityTypeConfiguration<SupportMessage>
{
    public void Configure(EntityTypeBuilder<SupportMessage> builder)
    {
        builder.ToTable("support_message");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.TicketId).IsRequired();
        // SUPPORT_BOT: у сообщений бота и системных нет пользователя-отправителя.
        builder.Property(x => x.SenderId).IsRequired(false);
        builder.Property(x => x.Content).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        // SUPPORT_BOT: заменяет прежний IsFromAdmin.
        builder.Property(x => x.AuthorType)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.HasOne(x => x.Ticket)
            .WithMany(t => t.Messages)
            .HasForeignKey(x => x.TicketId)
            .OnDelete(DeleteBehavior.Cascade);

        // SenderId nullable: FK сохраняется для пользовательских и админских сообщений, а у бота и
        // системы он null. Каскад на удаление пользователя по-прежнему работает для непустых значений.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.SenderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.TicketId, x.CreatedAt });
    }
}
