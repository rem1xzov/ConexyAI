using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ConexyAI.Configuration;

// USER_INTEGRATIONS: добавлено 2026-10-10 — отдельная таблица под креды агента. Текстовые колонки
// без ограничения длины: зашифрованные значения и JSON крупнее обычных настроек.
public class UserIntegrations_config : IEntityTypeConfiguration<UserIntegrationsEntity>
{
    public void Configure(EntityTypeBuilder<UserIntegrationsEntity> builder)
    {
        builder.ToTable("user_integrations");

        builder.HasKey(x => x.UserId);

        builder.Property(x => x.GitHubToken).HasColumnType("text").IsRequired(false);
        builder.Property(x => x.McpServers).HasColumnType("text").IsRequired(false);
        builder.Property(x => x.UpdatedAt).IsRequired();

        builder.HasOne<User>()
            .WithOne()
            .HasForeignKey<UserIntegrationsEntity>(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
