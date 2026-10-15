using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ConexyAI.Configuration;

// DESKTOP_AGENT: добавлено 2026-10-15
public class DeviceApiKey_config : IEntityTypeConfiguration<DeviceApiKeyEntity>
{
    public void Configure(EntityTypeBuilder<DeviceApiKeyEntity> builder)
    {
        builder.ToTable("device_api_keys");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).IsRequired().HasMaxLength(100);
        builder.Property(x => x.KeyHash).IsRequired().HasMaxLength(64);
        builder.Property(x => x.KeyPrefix).IsRequired().HasMaxLength(16);
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.LastUsedAt).IsRequired(false);
        builder.Property(x => x.RevokedAt).IsRequired(false);

        builder.HasIndex(x => x.KeyHash).IsUnique();
        builder.HasIndex(x => x.UserId);

        // Ключи удаляются вместе с пользователем.
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
