using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ConexyAI.Configuration;

// CHAT_OWNERSHIP: добавлено 2026-09-24
public class Chat_config : IEntityTypeConfiguration<ChatEntity>
{
    public void Configure(EntityTypeBuilder<ChatEntity> builder)
    {
        builder.ToTable("chat");

        // The id comes from the client (it is the chat id), never generated here.
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.UserId).IsRequired();
        builder.Property(x => x.Kind).HasMaxLength(20);
        builder.Property(x => x.Model).HasMaxLength(50);
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.UpdatedAt).IsRequired();
        // SHARE_PUBLIC: добавлено 2026-10-01 — уникальный токен публичной ссылки. В Postgres NULL в
        // уникальном индексе не конфликтует сам с собой, поэтому нерасшаренные чаты друг другу не мешают.
        builder.Property(x => x.ShareToken).HasMaxLength(64);
        builder.HasIndex(x => x.ShareToken).IsUnique();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => x.UserId);
    }
}
