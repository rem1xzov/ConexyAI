using System.Runtime.CompilerServices;
using ConexyAI.Configuration;
using ConexyAI.Contract;
using ConexyAI.DbContext;
using ConexyAI.Entity;
using ConexyAI.Model;
using ConexyAI.Repository;
using ConexyAI.Service;
using ConexyAI.Service.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

// TOKEN_TOPUP: добавлено 2026-10-06
/// <summary>
/// Разовая докупка токенов («Token»): начисление по оплате, раздельные пулы Coder/Cowork, повторные
/// покупки и переживание смены тарифа. Проверяется на подставном клиенте ЮKassa и БД в памяти.
/// </summary>
internal static class TokenTopUpTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("token top-up: a paid Coder purchase credits the coding pool once", CoderCreditedAsync);
        TestRegistry.Add("token top-up: a repeated purchase credits the pool again", RepeatsStackAsync);
        TestRegistry.Add("token top-up: the Coder and Cowork pools stay separate", PoolsSeparateAsync);
        TestRegistry.Add("token top-up: the credit raises the limit and survives a tier change", LimitAndTierChangeAsync);
        TestRegistry.Add("token top-up: bought Cowork tokens open Cowork on Free and close it when spent", CoworkUnlockedAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static async Task CoderCreditedAsync()
    {
        var dbName = "topup_" + Guid.NewGuid().ToString("N");
        await using var context = NewContext(dbName);
        var userId = Guid.NewGuid();
        context.Users.Add(new User { Id = userId, SubscriptionTier = SubscriptionTier.Free });
        await context.SaveChangesAsync();

        await AddTokenPaymentAsync(context, userId, "pay_t1", "TokenCoder");
        var service = NewPaymentService(context, "succeeded");
        await service.HandleNotificationAsync("payment.succeeded", "pay_t1", "succeeded");

        var user = (await context.Users.FindAsync(userId))!;
        Assert(user.CoderTokenTopUp == 1_000_000, $"the Coder pool must gain 1M, got {user.CoderTokenTopUp}");
        Assert(user.CoworkTokenTopUp == 0, "a Coder purchase must not touch the Cowork pool");
        // Это докупка, а не подписка: тариф и срок не меняются.
        Assert(user.SubscriptionTier == SubscriptionTier.Free, "the tier must stay Free");
        Assert(user.SubscriptionExpiresAt is null, "the expiry must stay unset");
    }

    private static async Task RepeatsStackAsync()
    {
        var dbName = "topup_" + Guid.NewGuid().ToString("N");
        await using var context = NewContext(dbName);
        var userId = Guid.NewGuid();
        context.Users.Add(new User { Id = userId });
        await context.SaveChangesAsync();

        var service = NewPaymentService(context, "succeeded");
        await AddTokenPaymentAsync(context, userId, "pay_t1", "TokenCoder");
        await service.HandleNotificationAsync("payment.succeeded", "pay_t1", "succeeded");
        await AddTokenPaymentAsync(context, userId, "pay_t2", "TokenCoder");
        await service.HandleNotificationAsync("payment.succeeded", "pay_t2", "succeeded");

        var user = (await context.Users.FindAsync(userId))!;
        Assert(user.CoderTokenTopUp == 2_000_000, $"two purchases must stack to 2M, got {user.CoderTokenTopUp}");

        // Повторная доставка того же вебхука ничего не добавляет (идемпотентность по ActivatedAt).
        await service.HandleNotificationAsync("payment.succeeded", "pay_t2", "succeeded");
        var afterRepeat = (await context.Users.FindAsync(userId))!;
        Assert(afterRepeat.CoderTokenTopUp == 2_000_000, "a repeated notification must not credit twice");
    }

    private static async Task PoolsSeparateAsync()
    {
        var dbName = "topup_" + Guid.NewGuid().ToString("N");
        await using var context = NewContext(dbName);
        var userId = Guid.NewGuid();
        context.Users.Add(new User { Id = userId });
        await context.SaveChangesAsync();

        var service = NewPaymentService(context, "succeeded");
        await AddTokenPaymentAsync(context, userId, "pay_coder", "TokenCoder");
        await service.HandleNotificationAsync("payment.succeeded", "pay_coder", "succeeded");
        await AddTokenPaymentAsync(context, userId, "pay_cowork", "TokenCowork");
        await service.HandleNotificationAsync("payment.succeeded", "pay_cowork", "succeeded");

        var user = (await context.Users.FindAsync(userId))!;
        Assert(user.CoderTokenTopUp == 1_000_000, $"the Coder pool must be 1M, got {user.CoderTokenTopUp}");
        Assert(user.CoworkTokenTopUp == 1_000_000, $"the Cowork pool must be 1M, got {user.CoworkTokenTopUp}");
    }

    private static async Task LimitAndTierChangeAsync()
    {
        var dbName = "topup_" + Guid.NewGuid().ToString("N");
        await using var context = NewContext(dbName);
        var userId = Guid.NewGuid();
        context.Users.Add(new User { Id = userId, SubscriptionTier = SubscriptionTier.Free });
        await context.SaveChangesAsync();

        await AddTokenPaymentAsync(context, userId, "pay_t1", "TokenCoder");
        await NewPaymentService(context, "succeeded")
            .HandleNotificationAsync("payment.succeeded", "pay_t1", "succeeded");

        var subscription = NewSubscriptionService(context);

        var free = await subscription.GetUsageAsync(userId);
        Assert(free.AgentLimit == 400_000 + 1_000_000, $"Free limit = 400k + 1M credit, got {free.AgentLimit}");
        Assert(free.Tier == "Free", $"the tier must read Free, got {free.Tier}");

        // Смена тарифа обнуляет счётчики (GetOrCreateAsync), но докупленное живёт на пользователе.
        var user = (await context.Users.FindAsync(userId))!;
        user.SubscriptionTier = SubscriptionTier.Pro;
        user.SubscriptionExpiresAt = DateTime.UtcNow.AddDays(30);
        await new UserRepository(context).UpdateAsync(user);

        var pro = await subscription.GetUsageAsync(userId);
        Assert(pro.Tier == "Pro", $"the tier must read Pro, got {pro.Tier}");
        Assert(pro.AgentLimit == 2_000_000 + 1_000_000, $"Pro limit = 2M + 1M credit, got {pro.AgentLimit}");
        Assert(pro.CoworkLimit == 1_000_000, $"only the tier budget applies to Cowork, got {pro.CoworkLimit}");
    }

    // TOKEN_TOPUP: изменено 2026-10-06 — покупка Cowork-токенов открывает режим на Free, а когда они
    // израсходованы — закрывает; купленное конечно и не возобновляется сбросом окна.
    private static async Task CoworkUnlockedAsync()
    {
        var dbName = "topup_" + Guid.NewGuid().ToString("N");
        await using var context = NewContext(dbName);
        var userId = Guid.NewGuid();
        context.Users.Add(new User { Id = userId, SubscriptionTier = SubscriptionTier.Free });
        await context.SaveChangesAsync();

        var subscription = NewSubscriptionService(context);

        // Free без покупки: Cowork заперт по тарифу.
        var locked = await subscription.CheckBeforeRunAsync(userId, ConexyModelType.ConexyCowork);
        Assert(locked.Kind == UsageDecisionKind.LimitExceeded && locked.LimitName == "cowork_plan",
            $"Cowork must be plan-locked on Free without a purchase, got {locked.Kind}/{locked.LimitName}");

        // Покупка Cowork-токенов открывает режим и на Free.
        var user = (await context.Users.FindAsync(userId))!;
        user.CoworkTokenTopUp = 1_000_000;
        await new UserRepository(context).UpdateAsync(user);

        Assert((await subscription.CheckBeforeRunAsync(userId, ConexyModelType.ConexyCowork)).Kind == UsageDecisionKind.Allowed,
            "bought Cowork tokens must open the mode on Free");
        var usage = await subscription.GetUsageAsync(userId);
        Assert(usage.CoworkLimit == 1_000_000, $"the cowork limit must be the purchased amount, got {usage.CoworkLimit}");

        // Расходуем всё купленное — режим снова закрыт.
        await subscription.RecordAgentTokensAsync(userId, ConexyModelType.ConexyCowork, new LlmTokenUsage(UncachedInput: 1_000_000));
        var spent = await subscription.CheckBeforeRunAsync(userId, ConexyModelType.ConexyCowork);
        Assert(spent.Kind == UsageDecisionKind.LimitExceeded && spent.LimitName == "cowork",
            $"Cowork must close once the purchased tokens are spent, got {spent.Kind}/{spent.LimitName}");

        // Сброс окна НЕ возвращает купленное: расход купленных не привязан к окну тарифа.
        var counter = await context.UserUsageCounters.FirstAsync(c => c.UserId == userId);
        counter.CoworkWindowResetAt = DateTime.UtcNow.AddDays(-1);
        await context.SaveChangesAsync();

        var afterReset = await subscription.GetUsageAsync(userId);
        Assert(afterReset.CoworkUsed == 1_000_000, $"the purchased spend must survive a window reset, got {afterReset.CoworkUsed}");
        Assert((await subscription.CheckBeforeRunAsync(userId, ConexyModelType.ConexyCowork)).Kind == UsageDecisionKind.LimitExceeded,
            "a window reset must not hand back spent purchased tokens");
    }

    private static async Task AddTokenPaymentAsync(DbConexy context, Guid userId, string providerId, string planId)
    {
        var pool = planId == "TokenCowork" ? "Cowork" : "Coder";
        await new PaymentRepository(context).AddAsync(new PaymentEntity
        {
            UserId = userId,
            PlanId = planId,
            Tier = string.Empty,
            Months = 1,
            AmountRub = 199,
            Kind = PaymentPlanKind.Token,
            Pool = pool,
            TokenAmount = 1_000_000,
            ProviderPaymentId = providerId,
            Status = "pending",
            IdempotenceKey = "key_" + providerId,
        });
    }

    private static DbConexy NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<DbConexy>().UseInMemoryDatabase(dbName).Options);

    private static PaymentService NewPaymentService(DbConexy context, string status) =>
        new(
            new FakeYooKassa { Status = status },
            new PaymentRepository(context),
            new UserRepository(context),
            Options.Create(new PaymentPlansOptions
            {
                Plans = new Dictionary<string, PaymentPlan>(StringComparer.OrdinalIgnoreCase)
                {
                    ["TokenCoder"] = new()
                    {
                        Kind = PaymentPlanKind.Token, Pool = "Coder", TokenAmount = 1_000_000,
                        AmountRub = 199, Months = 1, Title = "1 000 000 токенов для агента Coder",
                    },
                    ["TokenCowork"] = new()
                    {
                        Kind = PaymentPlanKind.Token, Pool = "Cowork", TokenAmount = 1_000_000,
                        AmountRub = 199, Months = 1, Title = "1 000 000 токенов для режима Cowork",
                    },
                },
            }),
            Options.Create(new YooKassaSettings { ShopId = "shop", SecretKey = "secret" }),
            NullLogger<PaymentService>.Instance);

    private static SubscriptionService NewSubscriptionService(DbConexy context) =>
        new(
            new UsageRepository(context),
            new UserRepository(context),
            Options.Create(new SubscriptionLimitsOptions
            {
                Free = new TierLimits { FlashRequestsPerWindow = 100, ProRequestsPerWindow = 20, AgentTokenBudget = 400_000, CoworkTokenBudget = 0, CoworkEnabled = false },
                Pro = new TierLimits { FlashRequestsPerWindow = 250, ProRequestsPerWindow = 150, AgentTokenBudget = 2_000_000, CoworkTokenBudget = 1_000_000, CoworkEnabled = true },
            }));

    /// <summary>Подставной клиент ЮKassa: отдаёт заранее заданный статус проверки платежа.</summary>
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
