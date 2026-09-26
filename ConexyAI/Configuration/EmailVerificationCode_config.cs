using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ConexyAI.Configuration;

public class EmailVerificationCode_config : IEntityTypeConfiguration<EmailVerificationCodeEntity>
{
    public void Configure(EntityTypeBuilder<EmailVerificationCodeEntity> builder)
    {
        builder.ToTable("email_verification_codes");

        builder.HasKey(x => x.Id);

        // The users table caps an address at 320 characters; the same bound applies here.
        builder.Property(x => x.Email)
            .HasMaxLength(320)
            .IsRequired();

        // PASSWORD_RESET: добавлено 2026-09-26 — какой поток владеет кодом (signup/reset).
        builder.Property(x => x.Purpose)
            .HasMaxLength(16)
            .IsRequired();

        // BCrypt hashes are 60 characters today; the column is wider so a stronger cost or a
        // different scheme does not need a migration.
        builder.Property(x => x.CodeHash)
            .HasMaxLength(200)
            .IsRequired();

        // PASSWORD_RESET: nullable с 2026-09-26 — у строки сброса пароля хэша ещё нет.
        builder.Property(x => x.PasswordHash)
            .HasMaxLength(200)
            .IsRequired(false);

        builder.Property(x => x.LastSentIp)
            .HasMaxLength(64);

        // Lookups are always "newest code for this address" / "newest code from this IP".
        builder.HasIndex(x => x.Email);
        builder.HasIndex(x => x.LastSentIp);
    }
}
