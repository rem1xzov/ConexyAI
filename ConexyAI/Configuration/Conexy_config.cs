using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ConexyAI.Configuration;

public class Conexy_config : IEntityTypeConfiguration<ConexyEntity>
{
    public void Configure(EntityTypeBuilder<ConexyEntity> builder)
    {
        builder.ToTable("conexy");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.UserId)
            .IsRequired();

        builder.HasIndex(x => new { x.UserId, x.CreatedAt });

        builder.Property(x => x.Model)
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(x => x.Prompt)
            .IsRequired();

        builder.Property(x => x.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .IsRequired();
    }
}