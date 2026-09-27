using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ConexyAI.Configuration;

// YOOKASSA: добавлено 2026-09-27
public class Payment_config : IEntityTypeConfiguration<PaymentEntity>
{
    public void Configure(EntityTypeBuilder<PaymentEntity> builder)
    {
        builder.ToTable("payments");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.PlanId).IsRequired().HasMaxLength(40);
        builder.Property(x => x.Tier).IsRequired().HasMaxLength(20);
        builder.Property(x => x.Months).IsRequired();
        builder.Property(x => x.AmountRub).IsRequired();
        builder.Property(x => x.ProviderPaymentId).IsRequired().HasMaxLength(64);
        builder.Property(x => x.Status).IsRequired().HasMaxLength(32);
        builder.Property(x => x.IdempotenceKey).IsRequired().HasMaxLength(64);
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.PaidAt).IsRequired(false);
        builder.Property(x => x.ActivatedAt).IsRequired(false);

        // Один платёж ЮKassa — одна запись: по этому идентификатору вебхук находит строку.
        builder.HasIndex(x => x.ProviderPaymentId).IsUnique();
        // Статус последнего платежа пользователя читается при поллинге и поддержкой.
        builder.HasIndex(x => x.UserId);
    }
}
