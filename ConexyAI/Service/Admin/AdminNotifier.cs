using System.Globalization;
using ConexyAI.Entity;
using ConexyAI.Repository;
using ConexyAI.Service.Push;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ConexyAI.Service.Admin;

// ADMIN_ALERTS: добавлено 2026-11-09 — владелец получает push в мобильное приложение, когда
// пользователь оплатил подписку/токены или позвал оператора в поддержке. Отправка никогда не
// ломает сам платёж или эскалацию: ошибки только логируются.
public interface IAdminNotifier
{
    /// <summary>Успешная оплата: подписка или разовое пополнение токенов.</summary>
    Task PaymentAsync(Guid buyerId, PaymentEntity payment, string? grantedTier, CancellationToken ct = default);

    /// <summary>Пользователь нажал «Не помогло» и ждёт оператора.</summary>
    Task SupportEscalatedAsync(Guid userId, Guid ticketId, string? subject, string? lastMessage, CancellationToken ct = default);
}

public sealed class AdminNotifier : IAdminNotifier
{
    private const int BodyLimit = 180;

    private readonly IServiceScopeFactory _scopes;
    private readonly IPushService _push;
    private readonly ILogger<AdminNotifier> _logger;

    public AdminNotifier(IServiceScopeFactory scopes, IPushService push, ILogger<AdminNotifier> logger)
    {
        _scopes = scopes;
        _push = push;
        _logger = logger;
    }

    public async Task PaymentAsync(Guid buyerId, PaymentEntity payment, string? grantedTier, CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            var buyer = await DescribeAsync(users, buyerId, ct);
            var amount = Money(payment.AmountRub);

            string title;
            string body;
            if (string.Equals(payment.Kind, PaymentPlanKind.Token, StringComparison.OrdinalIgnoreCase))
            {
                title = "Покупка токенов";
                body = $"{buyer} купил {Tokens(payment.TokenAmount)} токенов ({payment.Pool ?? "Coder"}) — {amount} ₽";
            }
            else
            {
                title = "Новая оплата подписки";
                var tier = string.IsNullOrWhiteSpace(grantedTier) ? payment.Tier : grantedTier;
                body = $"{buyer} оплатил тариф {tier} — {amount} ₽ ({payment.Months} мес.)";
            }

            await SendToAdminsAsync("admin_payment", title, body, payment.Id.ToString(), users, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Admin payment notification failed for payment {PaymentId}.", payment.Id);
        }
    }

    public async Task SupportEscalatedAsync(Guid userId, Guid ticketId, string? subject, string? lastMessage, CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            var buyer = await DescribeAsync(users, userId, ct);

            const string title = "Пользователь зовёт админа";
            var topic = string.IsNullOrWhiteSpace(subject) ? "обращение в поддержку" : subject.Trim();
            var preview = Trim(lastMessage, BodyLimit);
            var body = preview.Length == 0 ? $"{buyer}: {topic}" : $"{buyer}: {topic} — «{preview}»";

            await SendToAdminsAsync("admin_support", title, body, ticketId.ToString(), users, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Admin support notification failed for ticket {TicketId}.", ticketId);
        }
    }

    private async Task SendToAdminsAsync(
        string type,
        string title,
        string body,
        string eventId,
        IUserRepository users,
        CancellationToken ct)
    {
        if (!_push.IsConfigured)
            return;

        var admins = await users.GetAdminUserIdsAsync(ct);
        if (admins.Count == 0)
        {
            _logger.LogInformation("Admin alert skipped: no administrator account is registered.");
            return;
        }

        var data = new Dictionary<string, string>
        {
            ["type"] = type,
            ["title"] = title,
            ["body"] = body,
            // Уникальный id держит уведомления разными: без него второе событие заменило бы первое.
            ["taskId"] = eventId,
        };
        foreach (var admin in admins)
        {
            try
            {
                await _push.SendAsync(admin, data, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Admin push failed for user {UserId}.", admin);
            }
        }
    }

    private static async Task<string> DescribeAsync(IUserRepository users, Guid userId, CancellationToken ct)
    {
        var user = await users.GetByIdAsync(userId, ct);
        if (user is null)
            return $"Пользователь {userId.ToString()[..8]}";
        return !string.IsNullOrWhiteSpace(user.Email)
            ? user.Email
            : !string.IsNullOrWhiteSpace(user.GitHubUsername)
                ? user.GitHubUsername
                : $"Пользователь {user.Id.ToString()[..8]}";
    }

    private static string Money(int rub) => rub.ToString("N0", CultureInfo.GetCultureInfo("ru-RU"));

    private static string Tokens(long tokens) => tokens.ToString("N0", CultureInfo.GetCultureInfo("ru-RU"));

    private static string Trim(string? text, int limit)
    {
        var clean = string.Join(' ', (text ?? string.Empty)
            .Split(new[] { '\r', '\n', '\u2028', '\u2029' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0));
        return clean.Length <= limit ? clean : clean[..limit].TrimEnd() + "…";
    }
}
