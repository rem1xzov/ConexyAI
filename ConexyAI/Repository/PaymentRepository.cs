using ConexyAI.DbContext;
using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;

namespace ConexyAI.Repository;

// YOOKASSA: добавлено 2026-09-27
/// <summary>
/// Один успешный платёж для админской сводки (для ручного пробития чеков в «Мой налог»).
/// Email/имя берём из пользователя; оба могут быть <c>null</c> — например, у аккаунта только с GitHub.
/// </summary>
public record AdminPaymentRow(
    Guid PaymentId,
    DateTime PaidAt,
    int AmountRub,
    string PlanId,
    string Tier,
    Guid UserId,
    string? Email,
    string? GitHubUsername);

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

    /// <summary>
    /// Успешные платежи за период (для админской сводки). Диапазон — <paramref name="fromUtc"/> включительно,
    /// <paramref name="toUtcExclusive"/> исключительно.
    /// </summary>
    Task<IReadOnlyList<AdminPaymentRow>> GetSucceededForPeriodAsync(
        DateTime fromUtc, DateTime toUtcExclusive, CancellationToken ct = default);

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

    public async Task<IReadOnlyList<AdminPaymentRow>> GetSucceededForPeriodAsync(
        DateTime fromUtc, DateTime toUtcExclusive, CancellationToken ct = default)
    {
        // «Успешный» = PaidAt проставлен, и только после подтверждения через API (см. PaymentService).
        var payments = await _context.Payments
            .AsNoTracking()
            .Where(p => p.PaidAt != null && p.PaidAt >= fromUtc && p.PaidAt < toUtcExclusive)
            .OrderBy(p => p.PaidAt)
            .ToListAsync(ct);

        if (payments.Count == 0) return Array.Empty<AdminPaymentRow>();

        // Один запрос на всех пользователей вместо обращения к БД на каждую строку.
        var userIds = payments.Select(p => p.UserId).Distinct().ToList();
        var users = await _context.Users
            .AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Email, u.GitHubUsername })
            .ToListAsync(ct);
        var byId = users.ToDictionary(u => u.Id);

        return payments
            .Select(p =>
            {
                byId.TryGetValue(p.UserId, out var user);
                return new AdminPaymentRow(
                    p.Id, p.PaidAt!.Value, p.AmountRub, p.PlanId, p.Tier, p.UserId, user?.Email, user?.GitHubUsername);
            })
            .ToList();
    }

    public async Task UpdateAsync(PaymentEntity payment, CancellationToken ct = default)
    {
        _context.Payments.Update(payment);
        await _context.SaveChangesAsync(ct);
    }
}
