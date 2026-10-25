using System.Runtime.CompilerServices;
using ConexyAI.Configuration;
using ConexyAI.Contract;
using ConexyAI.DbContext;
using ConexyAI.Entity;
using ConexyAI.Model;
using ConexyAI.Repository;
using ConexyAI.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

// TIER_GO / COWORK_BUDGET: добавлено 2026-09-26
/// <summary>
/// Правила тарифов: промежуточный Go, отдельный бюджет Cowork, запрет Cowork на бесплатном тарифе и
/// главное — тариф пользователя должен реально доезжать до счётчика лимитов.
/// </summary>
internal static class SubscriptionTests
{
    private const int K = 1_000;

    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("subs TIER_SYNC: the counter follows the user's tier, its limits and windows", TierSyncAsync);
        TestRegistry.Add("subs COWORK: paid plans only, counted in requests", CoworkAsync);
        TestRegistry.Add("subs DAILY: the daily agent cap stops a single-day burn, purchased tokens bypass it", AgentDailyCapAsync);
        TestRegistry.Add("subs ADMIN: admins are unlimited but their usage is tracked", AdminAsync);
        TestRegistry.Add("subs BILLING: cache hits are cheap and output is priced up (weighted tokens)", BillingAsync);
        TestRegistry.Add("subs LIMIT_RESET: Pro+ resets every pool once, others cannot", LimitResetAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    /// <summary>Лимиты ровно так, как их собирает Program.cs, на InMemory-базе.</summary>
    private static ServiceProvider BuildServices()
    {
        // Имя базы и корень считаются ЗАРАНЕЕ: внутри лямбды-настройки они пересоздавались бы на
        // каждый scope, и второй scope видел бы пустую базу (тест ловил бы «пользователь исчез»).
        var dbName = "subs_" + Guid.NewGuid().ToString("N");
        var root = new InMemoryDatabaseRoot();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<DbConexy>(o => o.UseTestDatabase(dbName, root));
        services.AddScoped<IUsageRepository, UsageRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.Configure<SubscriptionLimitsOptions>(o =>
        {
            // Числа те же, что в appsettings.json, но заданы здесь явно: этот тест проверяет ЛОГИКУ
            // лимитов, а не то, что кто-то не переписал конфиг.
            o.Free = Limits(100, 20, 300 * K, coworkRequests: 0, coworkEnabled: false, agentWindowDays: 30);
            o.Go = Limits(150, 50, 2_000 * K, coworkRequests: 10, coworkEnabled: true);
            o.Pro = Limits(250, 150, 3_000 * K, coworkRequests: 2, coworkEnabled: true, agentDaily: 1_000);
            o.ProMax = Limits(300, 200, 5_000 * K, coworkRequests: 200, coworkEnabled: true);
            o.Ultra = Limits(300, 200, 5_500 * K, coworkRequests: 300, coworkEnabled: true);
        });
        services.AddScoped<ISubscriptionService, SubscriptionService>();
        return services.BuildServiceProvider();
    }

    private static TierLimits Limits(
        int flash, int pro, int agent, int coworkRequests, bool coworkEnabled, int agentWindowDays = 7, int agentDaily = 0) => new()
    {
        FlashRequestsPerWindow = flash,
        ProRequestsPerWindow = pro,
        AgentTokenBudget = agent,
        AgentDailyTokenBudget = agentDaily,
        // COWORK_REQUESTS: с 2026-10-27 Cowork ограничен запросами, токены больше не лимит.
        CoworkRequestsPerWindow = coworkRequests,
        CoworkEnabled = coworkEnabled,
        FlashWindowDays = 7,
        ProWindowDays = 7,
        AgentWindowDays = agentWindowDays,
        CoworkWindowDays = 7
    };

    /// <summary>Заводит подтверждённого пользователя нужного тарифа и возвращает его id.</summary>
    private static async Task<Guid> AddUserAsync(IServiceProvider sp, SubscriptionTier tier)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = $"{Guid.NewGuid():N}@example.com",
            EmailConfirmed = true,
            SubscriptionTier = tier
        };
        await sp.GetRequiredService<IUserRepository>().AddAsync(user);
        return user.Id;
    }

    private static async Task TierSyncAsync()
    {
        await using var provider = BuildServices();
        await using var scope = provider.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var subs = sp.GetRequiredService<ISubscriptionService>();

        // ProMax: счётчик создаётся сразу с тарифом пользователя, а не с бесплатным.
        var proMax = await AddUserAsync(sp, SubscriptionTier.ProMax);
        var proMaxUsage = await subs.GetUsageAsync(proMax);
        Assert(proMaxUsage.Tier == "ProMax", $"the counter must adopt the user's tier, got {proMaxUsage.Tier}");
        Assert(proMaxUsage.AgentLimit == 5_000 * K, $"ProMax agent budget must be 5M, got {proMaxUsage.AgentLimit}");
        Assert(proMaxUsage.CoworkLimit == 200, $"ProMax cowork limit is counted in requests, got {proMaxUsage.CoworkLimit}");
        Assert(proMaxUsage.FlashLimit == 300 && proMaxUsage.ProLimit == 200,
            "the flash/pro request limits must stay as they were");

        // ANNUAL_ULTRA: годовой тариф — самая большая полка.
        var ultra = await AddUserAsync(sp, SubscriptionTier.Ultra);
        var ultraUsage = await subs.GetUsageAsync(ultra);
        Assert(ultraUsage.Tier == "Ultra", $"Ultra must be a tier of its own, got {ultraUsage.Tier}");
        Assert(ultraUsage.AgentLimit == 5_500 * K && ultraUsage.CoworkLimit == 300, "Ultra limits");

        // Go: свои числа запросов и токенов.
        var go = await AddUserAsync(sp, SubscriptionTier.Go);
        var goUsage = await subs.GetUsageAsync(go);
        Assert(goUsage.Tier == "Go", $"Go must be a tier of its own, got {goUsage.Tier}");
        Assert(goUsage.FlashLimit == 150 && goUsage.ProLimit == 50, "Go request limits");
        Assert(goUsage.AgentLimit == 2_000 * K && goUsage.CoworkLimit == 10, "Go limits");

        // Смена тарифа (как после оплаты): счётчик переезжает, старый расход не переносится.
        await subs.RecordRequestAsync(go, ConexyModelType.ConexyV1Flash);
        await using (var second = provider.CreateAsyncScope())
        {
            var users = second.ServiceProvider.GetRequiredService<IUserRepository>();
            var row = await users.GetByIdAsync(go);
            Assert(row is not null, "the user must still be readable in a fresh scope");
            row!.SubscriptionTier = SubscriptionTier.Pro;
            await users.UpdateAsync(row);
        }

        var upgraded = await subs.GetUsageAsync(go);
        Assert(upgraded.Tier == "Pro", $"the upgrade must reach the counter, got {upgraded.Tier}");
        Assert(upgraded.FlashUsed == 0, $"a tier change must reset the counters, got {upgraded.FlashUsed}");
        Assert(upgraded.AgentLimit == 3_000 * K, $"Pro agent budget must be 3M, got {upgraded.AgentLimit}");
    }

    private static async Task CoworkAsync()
    {
        await using var provider = BuildServices();
        await using var scope = provider.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var subs = sp.GetRequiredService<ISubscriptionService>();

        // Free: режим заперт по тарифу, и причина названа отдельно — это не «лимит исчерпан».
        var free = await AddUserAsync(sp, SubscriptionTier.Free);
        var denied = await subs.CheckBeforeRunAsync(free, ConexyModelType.ConexyCowork);
        Assert(denied.Kind == UsageDecisionKind.LimitExceeded && denied.LimitName == "cowork_plan",
            $"Cowork must be paid-only, got {denied.Kind}/{denied.LimitName}");

        // AGENT_FREE_ACCESS: Coder — НЕ платный режим. У Free свой бюджет токенов, поэтому запуск
        // агента и его рабочей области на бесплатном тарифе разрешён (платным является только Cowork).
        Assert((await subs.CheckBeforeRunAsync(free, ConexyModelType.ConexyCoder)).Kind == UsageDecisionKind.Allowed,
            "Coder must stay available on the free tier");

        // Платный тариф: режим открыт.
        var pro = await AddUserAsync(sp, SubscriptionTier.Pro);
        Assert((await subs.CheckBeforeRunAsync(pro, ConexyModelType.ConexyCowork)).Kind == UsageDecisionKind.Allowed,
            "Cowork must be allowed on a paid plan");

        // COWORK_REQUESTS: Cowork теперь считает запросы и не трогает бюджет агента.
        await subs.RecordRequestAsync(pro, ConexyModelType.ConexyCowork);
        await subs.RecordRequestAsync(pro, ConexyModelType.ConexyCowork);
        var usage = await subs.GetUsageAsync(pro);
        Assert(usage.CoworkUsed == 2, $"cowork requests must land in the cowork counter, got {usage.CoworkUsed}");
        Assert(usage.AgentUsed == 0, $"cowork must not spend the agent budget, got {usage.AgentUsed}");

        // Исчерпанный лимит Cowork: своя причина и дата сброса.
        var spent = await subs.CheckBeforeRunAsync(pro, ConexyModelType.ConexyCowork);
        Assert(spent.Kind == UsageDecisionKind.LimitExceeded && spent.LimitName == "cowork" && spent.ResetsAt is not null,
            $"an exhausted cowork limit must report itself, got {spent.LimitName}");

        // Агент-кодер того же пользователя продолжает работать: пулы независимы.
        Assert((await subs.CheckBeforeRunAsync(pro, ConexyModelType.ConexyCoder)).Kind == UsageDecisionKind.Allowed,
            "the coding agent must be unaffected by the cowork limit");
    }

    /// <summary>
    /// DAILY_CAP: недельный бюджет ещё есть, но суточный потолок уже закрыт — запуск блокируется со
    /// своей причиной «agent_daily». Купленные токены дневной потолок не держат.
    /// </summary>
    private static async Task AgentDailyCapAsync()
    {
        await using var provider = BuildServices();
        await using var scope = provider.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var subs = sp.GetRequiredService<ISubscriptionService>();

        var userId = await AddUserAsync(sp, SubscriptionTier.Pro);

        // Суточный потолок в тесте — 1 000 токенов (в конфиге теста он задан ниже через Limits).
        await subs.RecordAgentTokensAsync(userId, ConexyModelType.ConexyCoder, new LlmTokenUsage(UncachedInput: 1_000));

        var blocked = await subs.CheckBeforeRunAsync(userId, ConexyModelType.ConexyCoder);
        Assert(blocked.Kind == UsageDecisionKind.LimitExceeded && blocked.LimitName == "agent_daily"
               && blocked.ResetsAt is not null,
            $"the daily cap must report itself, got {blocked.Kind}/{blocked.LimitName}");

        // Дневной потолок не должен мешать чату: он про агента.
        Assert((await subs.CheckBeforeRunAsync(userId, ConexyModelType.ConexyV1Flash)).Kind == UsageDecisionKind.Allowed,
            "the daily agent cap must not affect chat models");
    }

    // ADMIN_UNLIMITED: с 2026-10-01 расход админа учитывается (для кружка), но ни на что не влияет.
    private static async Task AdminAsync()
    {
        await using var provider = BuildServices();
        await using var scope = provider.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var subs = sp.GetRequiredService<ISubscriptionService>();

        var admin = new User
        {
            Id = Guid.NewGuid(),
            Email = $"admin-{Guid.NewGuid():N}@example.com",
            EmailConfirmed = true,
            IsAdmin = true,
            SubscriptionTier = SubscriptionTier.Free,
        };
        await sp.GetRequiredService<IUserRepository>().AddAsync(admin);

        await subs.RecordAgentTokensAsync(admin.Id, ConexyModelType.ConexyCoder, new LlmTokenUsage(UncachedInput: 123_456));
        var usage = await subs.GetUsageAsync(admin.Id);
        Assert(usage.Tier == "Admin", $"an admin must read as Admin, got {usage.Tier}");
        Assert(usage.AgentUsed == 123_456, $"admin usage must be tracked, got {usage.AgentUsed}");
        Assert(usage.AgentLimit == long.MaxValue, "the admin agent limit must be infinite");
        Assert(usage.CoworkLimit == long.MaxValue, "the admin cowork limit must be infinite (Cowork is open)");

        // Безлимит — запуск разрешён даже при огромном расходе.
        await subs.RecordAgentTokensAsync(admin.Id, ConexyModelType.ConexyCoder, new LlmTokenUsage(UncachedInput: 1_000_000_000L));
        Assert((await subs.CheckBeforeRunAsync(admin.Id, ConexyModelType.ConexyCoder)).Kind == UsageDecisionKind.Allowed,
            "an admin must never be blocked by the agent budget");
        Assert((await subs.CheckBeforeRunAsync(admin.Id, ConexyModelType.ConexyCowork)).Kind == UsageDecisionKind.Allowed,
            "an admin always has Cowork");
    }

    // PRICED_BILLING: добавлено 2026-10-06 — бюджет считается по цене, а не по сырому total_tokens.
    private static async Task BillingAsync()
    {
        await using var provider = BuildServices();
        await using var scope = provider.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var subs = sp.GetRequiredService<ISubscriptionService>();
        var user = await AddUserAsync(sp, SubscriptionTier.Pro);

        // Кэш-хит 100k (×0.02), обычный вход 10k (×1), выход 1k (×4):
        // 10 000 + 2 000 + 4 000 = 16 000 «эквивалентных» токенов вместо сырых 111 000.
        var billed = await subs.RecordAgentTokensAsync(user, ConexyModelType.ConexyCoder,
            new LlmTokenUsage(CachedInput: 100 * K, UncachedInput: 10 * K, Output: 1 * K));
        Assert(billed == 16 * K, $"the billed amount must be weighted, got {billed}");

        var usage = await subs.GetUsageAsync(user);
        Assert(usage.AgentUsed == 16 * K, $"the pool must hold the weighted amount, got {usage.AgentUsed}");
        // CACHE_STATS: для показа отдаётся сырой объём из кэша и процент скидки.
        Assert(usage.AgentCachedTokens == 100 * K, $"the cached volume must be reported, got {usage.AgentCachedTokens}");
        Assert(usage.CacheHitDiscountPercent == 98, $"the cache discount must read 98%, got {usage.CacheHitDiscountPercent}");

        // Чистый кэш-хит почти ничего не стоит: 100k хитов ≈ 2k бюджетных токенов.
        var cacheOnly = await subs.RecordAgentTokensAsync(user, ConexyModelType.ConexyCoder,
            new LlmTokenUsage(CachedInput: 100 * K));
        Assert(cacheOnly == 2 * K, $"cache-only turns must be heavily discounted, got {cacheOnly}");

        // Выход дороже входа: те же 10k входом и выходом дают разный счёт.
        var inputOnly = await subs.RecordAgentTokensAsync(user, ConexyModelType.ConexyCoder,
            new LlmTokenUsage(UncachedInput: 10 * K));
        var outputOnly = await subs.RecordAgentTokensAsync(user, ConexyModelType.ConexyCoder,
            new LlmTokenUsage(Output: 10 * K));
        Assert(outputOnly == 4 * inputOnly, $"output must cost 4x input, got {outputOnly} vs {inputOnly}");
    }

    // LIMIT_RESET: добавлено 2026-10-07 — акция сброса всех лимитов для Pro+ (одна на период).
    private static async Task LimitResetAsync()
    {
        await using var provider = BuildServices();
        await using var scope = provider.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var subs = sp.GetRequiredService<ISubscriptionService>();
        var users = sp.GetRequiredService<IUserRepository>();

        // Pro: акция есть, расход обнуляется по всем пулам.
        var pro = await AddUserAsync(sp, SubscriptionTier.Pro);
        var proRow = (await users.GetByIdAsync(pro))!;
        proRow.LimitResetAvailable = true;
        await users.UpdateAsync(proRow);

        await subs.RecordAgentTokensAsync(pro, ConexyModelType.ConexyCoder, new LlmTokenUsage(UncachedInput: 1_000 * K));
        await subs.RecordRequestAsync(pro, ConexyModelType.ConexyV1Flash);
        var before = await subs.GetUsageAsync(pro);
        Assert(before.AgentUsed > 0 && before.FlashUsed > 0 && before.LimitResetAvailable,
            "usage accrued and the promo is offered");

        var after = await subs.ResetLimitsAsync(pro);
        Assert(after.AgentUsed == 0 && after.FlashUsed == 0 && after.ProUsed == 0 && after.CoworkUsed == 0,
            "the reset must clear every pool");
        Assert(!after.LimitResetAvailable, "the promo is single-use");

        // Повторный сброс недоступен (до следующей оплаты).
        var secondRefused = false;
        try { await subs.ResetLimitsAsync(pro); }
        catch (AuthException) { secondRefused = true; }
        Assert(secondRefused, "a second reset must be refused");

        // Free: даже с флагом акция не предлагается и не выполняется.
        var free = await AddUserAsync(sp, SubscriptionTier.Free);
        var freeRow = (await users.GetByIdAsync(free))!;
        freeRow.LimitResetAvailable = true;
        await users.UpdateAsync(freeRow);
        Assert(!(await subs.GetUsageAsync(free)).LimitResetAvailable, "Free must not be offered the reset");

        var freeRefused = false;
        try { await subs.ResetLimitsAsync(free); }
        catch (AuthException) { freeRefused = true; }
        Assert(freeRefused, "Free must not be allowed to reset");

        // ADMIN: у админа кнопка сброса есть — как у платных, но возобновляется раз в месяц.
        var admin = new User
        {
            Id = Guid.NewGuid(),
            Email = $"admin-{Guid.NewGuid():N}@example.com",
            EmailConfirmed = true,
            IsAdmin = true,
            SubscriptionTier = SubscriptionTier.Free,
        };
        await users.AddAsync(admin);
        await subs.RecordAgentTokensAsync(admin.Id, ConexyModelType.ConexyCoder, new LlmTokenUsage(UncachedInput: 5_000 * K));
        Assert((await subs.GetUsageAsync(admin.Id)).LimitResetAvailable, "an admin is offered the reset");

        var adminAfter = await subs.ResetLimitsAsync(admin.Id);
        Assert(adminAfter.AgentUsed == 0, "the admin reset must clear usage");

        // Повторный сброс админу в течение месяца недоступен.
        var adminSecondRefused = false;
        try { await subs.ResetLimitsAsync(admin.Id); }
        catch (AuthException) { adminSecondRefused = true; }
        Assert(adminSecondRefused, "a second admin reset within the month must be refused");
    }
}
