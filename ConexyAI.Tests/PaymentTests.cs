using System.Net;
using System.Runtime.CompilerServices;
using ConexyAI.Configuration;
using ConexyAI.DbContext;
using ConexyAI.Entity;
using ConexyAI.Model;
using ConexyAI.Repository;
using ConexyAI.Service;
using ConexyAI.Service.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

// YOOKASSA: добавлено 2026-09-27
/// <summary>
/// Приём платежей: проверка источника вебхука по IP, выдача тарифа только после подтверждения через
/// API и учёт срока оплаты. Логика проверяется на подставном клиенте ЮKassa и БД в памяти, без сети.
/// </summary>
internal static class PaymentTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("payments: YooKassa notification IPs are matched by subnet", NotificationIpsAsync);
        TestRegistry.Add("payments: a confirmed payment grants the tier and extends the expiry", GrantsTierAsync);
        TestRegistry.Add("payments: an unconfirmed payment grants nothing", UnconfirmedGrantsNothingAsync);
        TestRegistry.Add("payments: an expired paid tier falls back to Free", ExpiredTierAsync);
        TestRegistry.Add("payments: the summary returns only successful payments inside the period", SummaryPeriodAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static Task NotificationIpsAsync()
    {
        var settings = new YooKassaSettings { NotificationIpRanges = "185.32.187.0/24, 10.0.0.5" };

        Assert(settings.IsNotificationIpAllowed(IPAddress.Parse("185.32.187.10")), "an address inside the /24 must pass");
        Assert(!settings.IsNotificationIpAllowed(IPAddress.Parse("185.32.188.10")), "an address outside the /24 must fail");
        Assert(settings.IsNotificationIpAllowed(IPAddress.Parse("10.0.0.5")), "a single address without a prefix must match exactly");
        Assert(!settings.IsNotificationIpAllowed(IPAddress.Parse("10.0.0.6")), "a neighbouring single address must not match");
        // IPv4-mapped IPv6 is what a dual-stack listener reports; it must be unwrapped, not rejected.
        Assert(settings.IsNotificationIpAllowed(IPAddress.Parse("::ffff:185.32.187.10")), "an IPv4-mapped address must be unwrapped");

        var empty = new YooKassaSettings { NotificationIpRanges = "" };
        Assert(empty.IsNotificationIpAllowed(IPAddress.Parse("1.2.3.4")), "an empty allowlist disables the check");

        return Task.CompletedTask;
    }

    private static async Task GrantsTierAsync()
    {
        var dbName = "payments_" + Guid.NewGuid().ToString("N");
        await using var context = NewContext(dbName);
        var userId = Guid.NewGuid();
        context.Users.Add(new User { Id = userId, SubscriptionTier = SubscriptionTier.Free });
        await context.SaveChangesAsync();

        var payments = new PaymentRepository(context);
        var payment = new PaymentEntity
        {
            UserId = userId,
            PlanId = "Pro",
            Tier = "Pro",
            Months = 1,
            AmountRub = 990,
            ProviderPaymentId = "pay_1",
            Status = "pending",
            IdempotenceKey = "key_1",
        };
        await payments.AddAsync(payment);

        var service = NewService(context, new FakeYooKassa { Status = "succeeded" });
        await service.HandleNotificationAsync("payment.succeeded", "pay_1", "succeeded");

        var user = await context.Users.FindAsync(userId);
        Assert(user!.SubscriptionTier == SubscriptionTier.Pro, $"the tier must be granted, got {user.SubscriptionTier}");
        Assert(user.SubscriptionExpiresAt is not null, "the expiry must be set");

        var firstExpiry = user.SubscriptionExpiresAt!.Value;
        Assert(firstExpiry > DateTime.UtcNow.AddDays(27), $"a one-month plan must last ~a month, got {firstExpiry:u}");

        var stored = await payments.GetByProviderIdAsync("pay_1");
        Assert(stored!.ActivatedAt is not null, "the payment must be marked activated");

        // Повторная доставка того же уведомления не должна продлевать срок второй раз.
        await service.HandleNotificationAsync("payment.succeeded", "pay_1", "succeeded");
        var afterRepeat = (await context.Users.FindAsync(userId))!.SubscriptionExpiresAt;
        Assert(afterRepeat == firstExpiry, "a repeated notification must not extend the subscription again");
    }

    private static async Task UnconfirmedGrantsNothingAsync()
    {
        var dbName = "payments_" + Guid.NewGuid().ToString("N");
        await using var context = NewContext(dbName);
        var userId = Guid.NewGuid();
        context.Users.Add(new User { Id = userId, SubscriptionTier = SubscriptionTier.Free });
        await context.SaveChangesAsync();

        var payments = new PaymentRepository(context);
        await payments.AddAsync(new PaymentEntity
        {
            UserId = userId,
            PlanId = "ProMax",
            Tier = "ProMax",
            Months = 1,
            AmountRub = 1590,
            ProviderPaymentId = "pay_2",
            Status = "pending",
            IdempotenceKey = "key_2",
        });

        // Вебхук утверждает "succeeded", но API отвечает иначе — верить вебхуку нельзя.
        var service = NewService(context, new FakeYooKassa { Status = "pending" });
        await service.HandleNotificationAsync("payment.succeeded", "pay_2", "succeeded");

        var user = await context.Users.FindAsync(userId);
        Assert(user!.SubscriptionTier == SubscriptionTier.Free, "an unconfirmed payment must not grant a tier");
        Assert(user.SubscriptionExpiresAt is null, "an unconfirmed payment must not set an expiry");
    }

    private static async Task ExpiredTierAsync()
    {
        var dbName = "payments_" + Guid.NewGuid().ToString("N");
        await using var context = NewContext(dbName);
        var userId = Guid.NewGuid();
        context.Users.Add(new User
        {
            Id = userId,
            SubscriptionTier = SubscriptionTier.Pro,
            SubscriptionExpiresAt = DateTime.UtcNow.AddDays(-1),
        });
        await context.SaveChangesAsync();

        var users = new UserRepository(context);
        var limits = new SubscriptionLimitsOptions
        {
            Free = new TierLimits { FlashRequestsPerWindow = 100, ProRequestsPerWindow = 20, AgentTokenBudget = 200_000 },
            Pro = new TierLimits { FlashRequestsPerWindow = 250, ProRequestsPerWindow = 150, AgentTokenBudget = 1_000_000, CoworkEnabled = true },
        };
        var service = new SubscriptionService(
            new UsageRepository(context),
            users,
            Options.Create(limits));

        var usage = await service.GetUsageAsync(userId);
        Assert(usage.Tier == "Free", $"an expired paid tier must read as Free, got {usage.Tier}");
        Assert(usage.AgentLimit == 200_000, $"the free agent budget must apply after expiry, got {usage.AgentLimit}");
    }

    private static async Task SummaryPeriodAsync()
    {
        var dbName = "payments_" + Guid.NewGuid().ToString("N");
        await using var context = NewContext(dbName);
        var userId = Guid.NewGuid();
        context.Users.Add(new User { Id = userId, Email = "buyer@example.com" });
        await context.SaveChangesAsync();

        var payments = new PaymentRepository(context);
        var from = new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc);
        var toExclusive = from.AddDays(1);

        async Task Add(string providerId, DateTime? paidAt, int amount)
        {
            await payments.AddAsync(new PaymentEntity
            {
                UserId = userId,
                PlanId = "Pro",
                Tier = "Pro",
                Months = 1,
                AmountRub = amount,
                ProviderPaymentId = providerId,
                Status = paidAt is null ? "pending" : "succeeded",
                IdempotenceKey = "key_" + providerId,
                PaidAt = paidAt,
            });
        }

        await Add("pay_lower", from, 990);                        // ровно начало периода — входит
        await Add("pay_middle", from.AddHours(12), 1590);         // внутри периода — входит
        await Add("pay_upper", toExclusive, 590);                // ровно конец (исключающий) — не входит
        await Add("pay_unpaid", null, 590);                      // не оплачен — не входит

        var rows = await payments.GetSucceededForPeriodAsync(from, toExclusive);
        Assert(rows.Count == 2, $"only the two successful payments inside the period must return, got {rows.Count}");
        Assert(rows.Sum(r => r.AmountRub) == 2580, $"the total must add up, got {rows.Sum(r => r.AmountRub)}");
        Assert(rows[0].PaidAt <= rows[1].PaidAt, "rows must be ordered by payment time");
        Assert(rows.All(r => r.Email == "buyer@example.com"), "the buyer email must be resolved for every row");
    }

    private static DbConexy NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<DbConexy>().UseInMemoryDatabase(dbName).Options);

    private static PaymentService NewService(DbConexy context, IYooKassaClient client) =>
        new(
            client,
            new PaymentRepository(context),
            new UserRepository(context),
            Options.Create(new PaymentPlansOptions
            {
                Plans = new Dictionary<string, PaymentPlan>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Pro"] = new() { Tier = "Pro", AmountRub = 990, Months = 1, Title = "Тариф Pro на 1 месяц" },
                    ["ProMax"] = new() { Tier = "ProMax", AmountRub = 1590, Months = 1, Title = "Тариф ProMax на 1 месяц" },
                },
            }),
            Options.Create(new YooKassaSettings { ShopId = "shop", SecretKey = "secret" }),
            NullLogger<PaymentService>.Instance);

    /// <summary>Подставной клиент ЮKassa: создаёт платёж и отдаёт заранее заданный статус.</summary>
    private sealed class FakeYooKassa : IYooKassaClient
    {
        public string Status { get; set; } = "succeeded";

        public bool IsConfigured => true;

        public Task<YooKassaPayment> CreatePaymentAsync(
            YooKassaPaymentRequest request, string idempotenceKey, CancellationToken ct = default) =>
            Task.FromResult(new YooKassaPayment("pay_created", "pending", "https://pay.example/1"));

        public Task<YooKassaPayment?> GetPaymentAsync(string paymentId, CancellationToken ct = default) =>
            Task.FromResult<YooKassaPayment?>(new YooKassaPayment(paymentId, Status, null));
    }
}
