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
        // YOOKASSA: добавлено 2026-09-27 — nullable: у бесплатных и «ручных» тарифов срока нет.
        builder.Property(x => x.SubscriptionExpiresAt).IsRequired(false);
        // TOKEN_TOPUP: добавлено 2026-10-06 — купленные разово токены; у всех прежних строк 0.
        builder.Property(x => x.CoderTokenTopUp).IsRequired();
        builder.Property(x => x.CoworkTokenTopUp).IsRequired();
        // LIMIT_RESET: акция сброса лимитов (Pro+); по умолчанию нет.
        builder.Property(x => x.LimitResetAvailable).IsRequired();
        builder.Property(x => x.LimitResetUsedAt).IsRequired(false);
        // EMAIL_AUTH: добавлено 2026-09-19
        builder.Property(x => x.IsAdmin).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.LastLoginAt).IsRequired();

        // Postgres unique indexes treat NULLs as distinct, so multiple OAuth users without
        // an email can coexist while a populated email/GitHubId is guaranteed unique.
        builder.HasIndex(x => x.Email).IsUnique();
        builder.HasIndex(x => x.GitHubId).IsUnique();

        // --- TOKEN_REVOCATION: добавлено 2026-09-24 (ревью M19) — версия сессий, см. User.TokenVersion.
        // Миграция добавит NOT NULL-колонку со значением 0 для существующих строк.
        builder.Property(x => x.TokenVersion).IsRequired();
        // --- /TOKEN_REVOCATION ---

        // --- PRIVACY_POLICY: добавлено 2026-09-25 — согласие с политикой, см. User.PolicyAcceptedAt.
        // Обе колонки nullable: у ранее зарегистрированных пользователей согласия нет, и это нормально —
        // их доступ не должен пострадать от появления требования.
        builder.Property(x => x.PolicyAcceptedAt).IsRequired(false);
        builder.Property(x => x.PolicyVersion).IsRequired(false).HasMaxLength(32);
        // --- /PRIVACY_POLICY ---
    }
}
