using ConexyAI.DbContext;
using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;

namespace ConexyAI.Repository;

// YOOKASSA: добавлено 2026-09-27
public interface IPaymentRepository
{
    Task AddAsync(PaymentEntity payment, CancellationToken ct = default);

    Task<PaymentEntity?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Платёж по его идентификатору в ЮKassa — так вебхук находит нашу строку.</summary>
    Task<PaymentEntity?> GetByProviderIdAsync(string providerPaymentId, CancellationToken ct = default);

    /// <summary>
    /// Незавершённый платёж пользователя по этому плану — чтобы двойной клик не создавал второй
    /// платёж, а вернул уже созданную ссылку на оплату.
    /// </summary>
    Task<PaymentEntity?> GetLatestPendingAsync(Guid userId, string planId, CancellationToken ct = default);

    Task UpdateAsync(PaymentEntity payment, CancellationToken ct = default);
}

public class PaymentRepository : IPaymentRepository
{
    private readonly DbConexy _context;

    public PaymentRepository(DbConexy context)
    {
        _context = context;
    }

    public async Task AddAsync(PaymentEntity payment, CancellationToken ct = default)
    {
        await _context.Payments.AddAsync(payment, ct);
        await _context.SaveChangesAsync(ct);
    }

    public Task<PaymentEntity?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _context.Payments.FirstOrDefaultAsync(p => p.Id == id, ct);

    public Task<PaymentEntity?> GetByProviderIdAsync(string providerPaymentId, CancellationToken ct = default) =>
        _context.Payments.FirstOrDefaultAsync(p => p.ProviderPaymentId == providerPaymentId, ct);

    public Task<PaymentEntity?> GetLatestPendingAsync(Guid userId, string planId, CancellationToken ct = default) =>
        _context.Payments
            .Where(p => p.UserId == userId && p.PlanId == planId && p.Status == "pending" && p.ActivatedAt == null)
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync(ct);

    public async Task UpdateAsync(PaymentEntity payment, CancellationToken ct = default)
    {
        _context.Payments.Update(payment);
        await _context.SaveChangesAsync(ct);
    }
}
