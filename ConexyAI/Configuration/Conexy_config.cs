using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ConexyAI.Configuration;

public class Conexy_config : IEntityTypeConfiguration<ConexyEntity>
{
    public void Configure(EntityTypeBuilder<ConexyEntity> builder)
    {
        builder.ToTable("conexy");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.UserId)
            .IsRequired();

        builder.HasIndex(x => new { x.UserId, x.CreatedAt });

        // CHAT_OWNERSHIP: добавлено 2026-09-24 — ревью M9: строки ходов уходят вместе с пользователем
        // (FK с каскадом) и с чатом (по ChatId).
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => x.ChatId);

        builder.Property(x => x.Model)
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(x => x.Prompt)
            .IsRequired();

        builder.Property(x => x.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .IsRequired();
    }
}