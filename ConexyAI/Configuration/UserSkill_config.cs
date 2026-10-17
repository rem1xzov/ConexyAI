using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ConexyAI.Configuration;

// AGENT_SKILLS: добавлено 2026-10-18
public class UserSkill_config : IEntityTypeConfiguration<UserSkill>
{
    public void Configure(EntityTypeBuilder<UserSkill> builder)
    {
        builder.ToTable("user_skill");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.UserId).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(1024).IsRequired();
        builder.Property(x => x.Content).IsRequired();
        builder.Property(x => x.Enabled).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.UpdatedAt).IsRequired();
        builder.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => new { x.UserId, x.Name }).IsUnique();
    }
}
