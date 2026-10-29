using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ConexyAI.Configuration;

// PHONE_AGENT: добавлено 2026-10-29
public class MobilePushToken_config : IEntityTypeConfiguration<MobilePushTokenEntity>
{
    public void Configure(EntityTypeBuilder<MobilePushTokenEntity> builder)
    {
        builder.ToTable("mobile_push_tokens");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Token).IsRequired().HasMaxLength(4096);
        builder.Property(x => x.Platform).IsRequired().HasMaxLength(20);
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.LastSeenAt).IsRequired();

        builder.HasIndex(x => x.Token).IsUnique();
        builder.HasIndex(x => x.UserId);

        builder.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
