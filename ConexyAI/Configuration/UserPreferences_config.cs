using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ConexyAI.Configuration;

// USER_PREFERENCES: добавлено 2026-09-24
public class UserPreferences_config : IEntityTypeConfiguration<UserPreferencesEntity>
{
    /// <summary>Upper bound for each free-text field (the API rejects longer input).</summary>
    public const int MaxTextLength = 1500;

    public void Configure(EntityTypeBuilder<UserPreferencesEntity> builder)
    {
        builder.ToTable("user_preferences");

        builder.HasKey(x => x.UserId);
        builder.Property(x => x.UserId).ValueGeneratedNever();
        builder.Property(x => x.AboutMe).HasMaxLength(MaxTextLength);
        builder.Property(x => x.ResponseStyle).HasMaxLength(MaxTextLength);
        builder.Property(x => x.MemoryEnabled).IsRequired().HasDefaultValue(true);
        builder.Property(x => x.UpdatedAt).IsRequired();

        builder.HasOne<User>()
            .WithOne()
            .HasForeignKey<UserPreferencesEntity>(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
