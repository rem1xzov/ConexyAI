using ConexyAI.Configuration;
using ConexyAI.Contract;
using ConexyAI.Entity;
using ConexyAI.Model;
using ConexyAI.Repository;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ConexyAI.Service.Payments;

// YOOKASSA: добавлено 2026-09-27
/// <summary>
/// Платежи за тарифы: создание платежа в ЮKassa, чтение статуса и выдача тарифа по уведомлению.
/// Сумма берётся из серверного каталога (<see cref="PaymentPlansOptions"/>), а не из запроса клиента.
/// </summary>
public interface IPaymentService
{
    /// <summary>Создаёт платёж и возвращает ссылку для редиректа пользователя.</summary>
    Task<CreatePaymentResponse> CreateAsync(Guid userId, string planId, CancellationToken ct = default);

    /// <summary>Статус своего платежа (для страницы возврата). <c>null</c> — платежа нет/чужой.</summary>
    Task<PaymentStatusResponse?> GetStatusAsync(Guid userId, Guid paymentId, CancellationToken ct = default);

    /// <summary>
    /// Обрабатывает уведомление ЮKassa. Статус из тела — только подсказка: перед выдачей тарифа
    /// платёж перепроверяется через API (источник истины).
    /// </summary>
    Task HandleNotificationAsync(string eventName, string providerPaymentId, string providerStatus, CancellationToken ct = default);
}

public class PaymentService : IPaymentService
{
    private readonly IYooKassaClient _client;
    private readonly IPaymentRepository _payments;
    private readonly IUserRepository _users;
    private readonly PaymentPlansOptions _plans;
    private readonly YooKassaSettings _settings;
    private readonly ILogger<PaymentService> _logger;

    public PaymentService(
        IYooKassaClient client,
        IPaymentRepository payments,
        IUserRepository users,
        IOptions<PaymentPlansOptions> plans,
        IOptions<YooKassaSettings> settings,
        ILogger<PaymentService> logger)
    {
        _client = client;
        _payments = payments;
        _users = users;
        _plans = plans.Value;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<CreatePaymentResponse> CreateAsync(Guid userId, string planId, CancellationToken ct = default)
    {
        var plan = _plans.Find(planId)
            ?? throw new ArgumentException($"Неизвестный тариф '{planId}'.");

        if (!_client.IsConfigured)
            throw new InvalidOperationException("Приём платежей не настроен: задайте YooKassa__ShopId и YooKassa__SecretKey.");

        // Двойной клик по «Купить» не должен плодить платежи: если незавершённый платёж на этот же
        // тариф ещё жив в ЮKassa, возвращаем его же ссылку, а новый не создаём.
        var pending = await _payments.GetLatestPendingAsync(userId, planId, ct);
        if (pending is not null && !string.IsNullOrEmpty(pending.ProviderPaymentId))
        {
            var current = await _client.GetPaymentAsync(pending.ProviderPaymentId, ct);
            if (current is { Status: "pending", ConfirmationUrl: { Length: > 0 } existingUrl })
            {
                return new CreatePaymentResponse(pending.Id, current.Status, existingUrl, pending.AmountRub, pending.PlanId);
            }
        }

        var user = await _users.GetByIdAsync(userId, ct);
        var payment = new PaymentEntity
        {
            UserId = userId,
            PlanId = planId,
            Tier = plan.Tier,
            Months = plan.Months,
            AmountRub = plan.AmountRub,
            Status = "pending",
            // Ключ идемпотентности — id самого платежа: он уникален на каждый создаваемый платёж.
            IdempotenceKey = Guid.NewGuid().ToString("N"),
        };
        await _payments.AddAsync(payment, ct);

        var request = new YooKassaPaymentRequest(
            AmountRub: plan.AmountRub,
            Description: string.IsNullOrWhiteSpace(plan.Title) ? $"Тариф {plan.Tier}" : plan.Title,
            ReturnUrl: BuildReturnUrl(payment.Id),
            Metadata: new Dictionary<string, string>
            {
                ["paymentId"] = payment.Id.ToString(),
                ["userId"] = userId.ToString(),
                ["planId"] = planId,
            },
            CustomerEmail: user?.Email);

        var created = await _client.CreatePaymentAsync(request, payment.IdempotenceKey, ct);

        payment.ProviderPaymentId = created.Id;
        payment.Status = created.Status;
        await _payments.UpdateAsync(payment, ct);

        if (string.IsNullOrEmpty(created.ConfirmationUrl))
            throw new YooKassaException("ЮKassa не вернула ссылку на оплату.");

        _logger.LogInformation(
            "YooKassa: payment {ProviderId} created for user {UserId} ({PlanId}, {Amount} RUB).",
            created.Id, userId, planId, plan.AmountRub);

        return new CreatePaymentResponse(payment.Id, created.Status, created.ConfirmationUrl, payment.AmountRub, planId);
    }

    public async Task<PaymentStatusResponse?> GetStatusAsync(Guid userId, Guid paymentId, CancellationToken ct = default)
    {
        var payment = await _payments.GetByIdAsync(paymentId, ct);
        if (payment is null || payment.UserId != userId) return null;

        return new PaymentStatusResponse(payment.Id, payment.Status, payment.PlanId, payment.Tier, payment.AmountRub);
    }

    public async Task HandleNotificationAsync(
        string eventName,
        string providerPaymentId,
        string providerStatus,
        CancellationToken ct = default)
    {
        var payment = await _payments.GetByProviderIdAsync(providerPaymentId, ct);
        if (payment is null)
        {
            // Уведомление по платежу, которого у нас нет: логируем и подтверждаем приём, иначе
            // ЮKassa будет повторять доставку.
            _logger.LogWarning(
                "YooKassa webhook: unknown payment {ProviderId} (event {Event}).", providerPaymentId, eventName);
            return;
        }

        if (payment.ActivatedAt is not null)
        {
            // Тариф уже выдан: повторную доставку просто подтверждаем.
            _logger.LogInformation(
                "YooKassa webhook: payment {ProviderId} already activated, ignoring {Event}.",
                providerPaymentId, eventName);
            return;
        }

        payment.Status = providerStatus;

        if (providerStatus != "succeeded")
        {
            // canceled и прочие промежуточные статусы: просто фиксируем состояние.
            await _payments.UpdateAsync(payment, ct);
            return;
        }

        // Источник истины — не тело вебхука, а сам платёж в ЮKassa: даже прошедший IP-фильтр запрос
        // может быть подделан, а подтверждение через API закрывает этот риск.
        var verified = await _client.GetPaymentAsync(providerPaymentId, ct);
        if (verified is null || verified.Status != "succeeded")
        {
            _logger.LogWarning(
                "YooKassa webhook: payment {ProviderId} reported succeeded but API says '{Status}'.",
                providerPaymentId, verified?.Status ?? "not found");
            await _payments.UpdateAsync(payment, ct);
            return;
        }

        payment.PaidAt = DateTime.UtcNow;
        await _payments.UpdateAsync(payment, ct);
        await ActivateAsync(payment, ct);
    }

    /// <summary>Выдаёт (продлевает) оплаченный тариф пользователю. Идемпотентно по ActivatedAt.</summary>
    private async Task ActivateAsync(PaymentEntity payment, CancellationToken ct)
    {
        if (payment.ActivatedAt is not null) return;

        if (!Enum.TryParse<SubscriptionTier>(payment.Tier, out var tier))
        {
            _logger.LogError("Payment {PaymentId}: unknown tier '{Tier}', tariff not granted.", payment.Id, payment.Tier);
            return;
        }

        var user = await _users.GetByIdAsync(payment.UserId, ct);
        if (user is null)
        {
            _logger.LogError("Payment {PaymentId}: user {UserId} not found, tariff not granted.", payment.Id, payment.UserId);
            return;
        }

        var now = DateTime.UtcNow;
        // Автопродления нет: каждый платёж — разовый и ПРОДЛЕВАЕТ доступ от текущего срока, если он
        // ещё не истёк, иначе — от сегодняшнего дня.
        var from = user.SubscriptionExpiresAt is { } expires && expires > now ? expires : now;

        // Активный более высокий тариф разовой покупкой не понижаем: иначе пользователь потерял бы
        // доступ, случайно докупив младший тариф.
        var activeExisting = user.SubscriptionExpiresAt is { } e && e > now ? user.SubscriptionTier : SubscriptionTier.Free;
        var granted = (int)activeExisting > (int)tier ? activeExisting : tier;

        user.SubscriptionTier = granted;
        user.SubscriptionExpiresAt = from.AddMonths(Math.Max(1, payment.Months));
        await _users.UpdateAsync(user, ct);

        payment.ActivatedAt = now;
        await _payments.UpdateAsync(payment, ct);

        _logger.LogInformation(
            "YooKassa: payment {ProviderId} activated — user {UserId} is on {Tier} until {Expires:u}.",
            payment.ProviderPaymentId, payment.UserId, granted, user.SubscriptionExpiresAt);
    }

    private string BuildReturnUrl(Guid paymentId)
    {
        var baseUrl = string.IsNullOrWhiteSpace(_settings.ReturnUrlBase)
            ? "https://conexyai.ru/"
            : _settings.ReturnUrlBase;
        // SPA читает эти параметры при загрузке и открывает окно статуса платежа.
        var separator = baseUrl.Contains('?') ? '&' : '?';
        return $"{baseUrl}{separator}payment=return&pid={paymentId}";
    }
}
