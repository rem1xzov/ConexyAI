using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ConexyAI.Configuration;

// SUBSCRIPTION_TIERS: добавлено 2026-09-17
public class UserUsageCounter_config : IEntityTypeConfiguration<UserUsageCounterEntity>
{
    public void Configure(EntityTypeBuilder<UserUsageCounterEntity> builder)
    {
        builder.ToTable("user_usage_counter");

        builder.HasKey(x => x.UserId);

        builder.Property(x => x.Tier)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(x => x.FlashRequestsUsed).IsRequired();
        builder.Property(x => x.ProRequestsUsed).IsRequired();
        builder.Property(x => x.AgentTokensUsed).IsRequired();
        builder.Property(x => x.FlashWindowResetAt).IsRequired();
        builder.Property(x => x.ProWindowResetAt).IsRequired();
        builder.Property(x => x.AgentWindowResetAt).IsRequired();
    }
}
