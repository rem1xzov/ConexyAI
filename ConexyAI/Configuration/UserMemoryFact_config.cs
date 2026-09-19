using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ConexyAI.Configuration;

// SUBSCRIPTION_TIERS: добавлено 2026-09-17
public class UserMemoryFact_config : IEntityTypeConfiguration<UserMemoryFactEntity>
{
    public void Configure(EntityTypeBuilder<UserMemoryFactEntity> builder)
    {
        builder.ToTable("user_memory_fact");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.UserId).IsRequired();
        builder.Property(x => x.FactText).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.UpdatedAt).IsRequired();

        // GITHUB_OAUTH: добавлено 2026-09-19 — real FK to users.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => x.UserId);
    }
}
