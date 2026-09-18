using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ConexyAI.Configuration;

// DANGEROUS_CMD_CONFIRM: добавлено 2026-09-17
public class PendingAction_config : IEntityTypeConfiguration<PendingActionEntity>
{
    public void Configure(EntityTypeBuilder<PendingActionEntity> builder)
    {
        builder.ToTable("conexy_pending_action");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.ChatId).IsRequired();
        builder.Property(x => x.TaskId).IsRequired();
        builder.Property(x => x.Command).IsRequired();
        builder.Property(x => x.WorkingDirectory).IsRequired();
        builder.Property(x => x.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.ResolvedAt).IsRequired(false);

        builder.HasIndex(x => new { x.ChatId, x.CreatedAt });
    }
}
