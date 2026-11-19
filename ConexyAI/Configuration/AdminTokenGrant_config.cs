using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ConexyAI.Configuration;

// ADMIN_TOKEN_CREDIT: добавлено 2026-11-19
public class AdminTokenGrant_config : IEntityTypeConfiguration<AdminTokenGrantEntity>
{
    public void Configure(EntityTypeBuilder<AdminTokenGrantEntity> builder)
    {
        builder.ToTable("admin_token_grant");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.UserId).IsRequired();
        builder.Property(x => x.AdminId).IsRequired();
        builder.Property(x => x.Pool).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Reason).HasMaxLength(500);
        builder.Property(x => x.CreatedAt).IsRequired();

        builder.HasIndex(x => new { x.UserId, x.CreatedAt });

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
