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
        TestRegistry.Add("subs COWORK: paid plans only, with a token budget of its own", CoworkAsync);
        TestRegistry.Add("subs ADMIN: admins are unlimited but their usage is tracked", AdminAsync);
        TestRegistry.Add("subs BILLING: cache hits are cheap and output is priced up (weighted tokens)", BillingAsync);
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
        services.AddDbContext<DbConexy>(o => o.UseInMemoryDatabase(dbName, root));
        services.AddScoped<IUsageRepository, UsageRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.Configure<SubscriptionLimitsOptions>(o =>
        {
            // Числа те же, что в appsettings.json, но заданы здесь явно: этот тест проверяет ЛОГИКУ
            // лимитов, а не то, что кто-то не переписал конфиг.
            o.Free = Limits(100, 20, 400 * K, 0, coworkEnabled: false, agentWindowDays: 30);
            o.Go = Limits(150, 50, 1_000 * K, 1_000 * K, coworkEnabled: true);
            o.Pro = Limits(250, 150, 2_000 * K, 2_000 * K, coworkEnabled: true);
            o.ProMax = Limits(300, 200, 3_500 * K, 3_500 * K, coworkEnabled: true);
            o.Ultra = Limits(300, 200, 4_000 * K, 4_000 * K, coworkEnabled: true);
        });
        services.AddScoped<ISubscriptionService, SubscriptionService>();
        return services.BuildServiceProvider();
    }

    private static TierLimits Limits(
        int flash, int pro, int agent, int cowork, bool coworkEnabled, int agentWindowDays = 7) => new()
    {
        FlashRequestsPerWindow = flash,
        ProRequestsPerWindow = pro,
        AgentTokenBudget = agent,
        CoworkTokenBudget = cowork,
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
        Assert(proMaxUsage.AgentLimit == 3_500 * K, $"ProMax agent budget must be 3.5M, got {proMaxUsage.AgentLimit}");
        Assert(proMaxUsage.CoworkLimit == 3_500 * K, $"ProMax cowork budget must equal the agent budget, got {proMaxUsage.CoworkLimit}");
        Assert(proMaxUsage.FlashLimit == 300 && proMaxUsage.ProLimit == 200,
            "the flash/pro request limits must stay as they were");

        // ANNUAL_ULTRA: годовой тариф — самая большая полка.
        var ultra = await AddUserAsync(sp, SubscriptionTier.Ultra);
        var ultraUsage = await subs.GetUsageAsync(ultra);
        Assert(ultraUsage.Tier == "Ultra", $"Ultra must be a tier of its own, got {ultraUsage.Tier}");
        Assert(ultraUsage.AgentLimit == 4_000 * K && ultraUsage.CoworkLimit == 4_000 * K, "Ultra token budgets");

        // Go: свои числа запросов и токенов.
        var go = await AddUserAsync(sp, SubscriptionTier.Go);
        var goUsage = await subs.GetUsageAsync(go);
        Assert(goUsage.Tier == "Go", $"Go must be a tier of its own, got {goUsage.Tier}");
        Assert(goUsage.FlashLimit == 150 && goUsage.ProLimit == 50, "Go request limits");
        Assert(goUsage.AgentLimit == 1_000 * K && goUsage.CoworkLimit == 1_000 * K, "Go token budgets");

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
        Assert(upgraded.AgentLimit == 2_000 * K, $"Pro agent budget must be 2M, got {upgraded.AgentLimit}");
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

        // Токены Cowork идут в свой пул и не трогают бюджет агента.
        await subs.RecordAgentTokensAsync(pro, ConexyModelType.ConexyCowork, new LlmTokenUsage(UncachedInput: 2_000 * K));
        var usage = await subs.GetUsageAsync(pro);
        Assert(usage.CoworkUsed == 2_000 * K, $"cowork tokens must land in the cowork pool, got {usage.CoworkUsed}");
        Assert(usage.AgentUsed == 0, $"cowork must not spend the agent budget, got {usage.AgentUsed}");

        // Исчерпанный бюджет Cowork: своя причина и дата сброса.
        var spent = await subs.CheckBeforeRunAsync(pro, ConexyModelType.ConexyCowork);
        Assert(spent.Kind == UsageDecisionKind.LimitExceeded && spent.LimitName == "cowork" && spent.ResetsAt is not null,
            $"an exhausted cowork budget must report itself, got {spent.LimitName}");

        // Агент-кодер того же пользователя продолжает работать: пулы независимы.
        Assert((await subs.CheckBeforeRunAsync(pro, ConexyModelType.ConexyCoder)).Kind == UsageDecisionKind.Allowed,
            "the coding agent must be unaffected by the cowork budget");
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
}
