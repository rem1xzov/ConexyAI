using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ConexyAI.Configuration;

// GITHUB_OAUTH: добавлено 2026-09-19
public class User_config : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Email).IsRequired(false).HasMaxLength(320);
        builder.Property(x => x.EmailConfirmed).IsRequired();
        builder.Property(x => x.PasswordHash).IsRequired(false);
        builder.Property(x => x.GitHubId).IsRequired(false).HasMaxLength(100);
        builder.Property(x => x.GitHubUsername).IsRequired(false).HasMaxLength(100);
        builder.Property(x => x.SubscriptionTier)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();
        // EMAIL_AUTH: добавлено 2026-09-19
        builder.Property(x => x.IsAdmin).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.LastLoginAt).IsRequired();

        // Postgres unique indexes treat NULLs as distinct, so multiple OAuth users without
        // an email can coexist while a populated email/GitHubId is guaranteed unique.
        builder.HasIndex(x => x.Email).IsUnique();
        builder.HasIndex(x => x.GitHubId).IsUnique();
    }
}
