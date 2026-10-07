using ConexyAI.Configuration;
using ConexyAI.Contract;
using ConexyAI.Entity;
using ConexyAI.Model;
using ConexyAI.Repository;
using Microsoft.Extensions.Options;

namespace ConexyAI.Service;

// SUBSCRIPTION_TIERS: добавлено 2026-09-17
/// <summary>
/// Per-user subscription limits and usage tracking. Windows reset lazily on first access
/// after their reset timestamp; flash/pro counters count requests, the agent counter
/// accumulates DeepSeek <c>usage.total_tokens</c>.
/// </summary>
public interface ISubscriptionService
{
    Task<SubscriptionUsageDto> GetUsageAsync(Guid userId, CancellationToken ct = default);
    Task<UsageDecision> CheckBeforeRunAsync(Guid userId, ConexyModelType modelType, CancellationToken ct = default);
    Task RecordRequestAsync(Guid userId, ConexyModelType modelType, CancellationToken ct = default);

    // COWORK_BUDGET: с 2026-09-26 у Coder и Cowork разные бюджеты, поэтому тип модели обязателен:
    // без него токены Cowork уходили бы в пул агента-кодера.
    // PRICED_BILLING: с 2026-10-06 принимает разбивку хода (кэш/без кэша/выход) и возвращает
    // фактически начисленную (взвешенную) сумму — её же накапливает токен-брейкер в раннере.
    Task<long> RecordAgentTokensAsync(Guid userId, ConexyModelType modelType, LlmTokenUsage usage, CancellationToken ct = default);
}

public class SubscriptionService : ISubscriptionService
{
    // COWORK_BUDGET: имена лимитов уезжают клиенту в теле 429 (`limit`) и решают, что показать:
    // «режим не входит в тариф» или «бюджет на этот период израсходован».
    private const string CoworkPlanLimit = "cowork_plan";
    private const string CoworkBudgetLimit = "cowork";

    private readonly IUsageRepository _repository;
    // ADMIN_UNLIMITED: добавлено 2026-09-19
    private readonly IUserRepository _userRepository;
    private readonly IOptions<SubscriptionLimitsOptions> _options;

    public SubscriptionService(IUsageRepository repository, IUserRepository userRepository, IOptions<SubscriptionLimitsOptions> options)
    {
        _repository = repository;
        _userRepository = userRepository;
        _options = options;
    }

    public async Task<SubscriptionUsageDto> GetUsageAsync(Guid userId, CancellationToken ct = default)
    {
        // ADMIN_UNLIMITED: изменено 2026-10-01 — админам по-прежнему ничего не ограничивает, но теперь
        // ИХ расход тоже считается, чтобы тот же кружок был осмысленным (раньше отдавался синтетический
        // ноль без записи в БД). Лимиты при этом отдаются как «бесконечные».
        var user = await _userRepository.GetByIdAsync(userId, ct);
        var counter = await GetOrCreateAsync(userId, user, ct);

        if (user?.IsAdmin == true)
            return AdminUsage(counter);

        var limits = GetTierLimits(counter.Tier);
        return new SubscriptionUsageDto(
            counter.Tier.ToString(),
            counter.FlashRequestsUsed, limits.FlashRequestsPerWindow, counter.FlashWindowResetAt,
            counter.ProRequestsUsed, limits.ProRequestsPerWindow, counter.ProWindowResetAt,
            // TOKEN_TOPUP: «использовано» = расход по тарифу (окно) + расход купленного (бессрочно).
            counter.AgentTokensUsed + counter.CoderTopUpUsed, AgentBudget(limits, user), counter.AgentWindowResetAt,
            // COWORK_BUDGET: отдельный пул; 0 в лимите — режим не входит в тариф.
            counter.CoworkTokensUsed + counter.CoworkTopUpUsed, CoworkBudget(limits, user), counter.CoworkWindowResetAt);
    }

    public async Task<UsageDecision> CheckBeforeRunAsync(Guid userId, ConexyModelType modelType, CancellationToken ct = default)
    {
        // ADMIN_UNLIMITED: добавлено 2026-09-19 — admins bypass every limit and never read or
        // increment the UserUsageCounter.
        var user = await _userRepository.GetByIdAsync(userId, ct);
        if (user?.IsAdmin == true)
            return new UsageDecision(UsageDecisionKind.Allowed);

        var counter = await GetOrCreateAsync(userId, user, ct);
        var limits = GetTierLimits(counter.Tier);
        // TOKEN_TOPUP: докупленные токены расширяют лимит пула (см. AgentBudget/CoworkBudget), а
        // «использовано» включает расход и тарифа (окно), и купленного (бессрочно).
        var agentBudget = AgentBudget(limits, user);
        var coworkBudget = CoworkBudget(limits, user);
        var agentUsed = counter.AgentTokensUsed + counter.CoderTopUpUsed;
        var coworkUsed = counter.CoworkTokensUsed + counter.CoworkTopUpUsed;

        switch (modelType)
        {
            case ConexyModelType.ConexyV1Flash:
                if (counter.FlashRequestsUsed >= limits.FlashRequestsPerWindow)
                    return new UsageDecision(UsageDecisionKind.LimitExceeded, "flash", counter.FlashWindowResetAt);
                return new UsageDecision(UsageDecisionKind.Allowed);

            case ConexyModelType.ConexyV1Pro:
                if (counter.ProRequestsUsed >= limits.ProRequestsPerWindow)
                {
                    // Transparent fallback to flash if the flash budget still has room.
                    if (counter.FlashRequestsUsed < limits.FlashRequestsPerWindow)
                        return new UsageDecision(UsageDecisionKind.FallbackToFlash);
                    return new UsageDecision(UsageDecisionKind.LimitExceeded, "pro", counter.ProWindowResetAt);
                }
                return new UsageDecision(UsageDecisionKind.Allowed);

            // COWORK_MODE / COWORK_BUDGET: изменено 2026-09-26 — Cowork вынесен в свой пул токенов и
            // доступен только на платных тарифах. Раньше он шёл по бюджету агента и не был ограничен
            // по тарифу вообще: на Free режим просто работал.
            // TOKEN_TOPUP: изменено 2026-10-06 — если куплены Cowork-токены, режим открыт и на Free.
            // «Не входит в тариф» — только когда купленного вообще не было; если покупали и израсходовали,
            // открывается уже бюджетная причина, чтобы пользователь увидел «докупи», а не «возьми тариф».
            case ConexyModelType.ConexyCowork:
                if (!limits.CoworkEnabled && (user?.CoworkTokenTopUp ?? 0L) <= 0)
                    return new UsageDecision(UsageDecisionKind.LimitExceeded, CoworkPlanLimit, DateTime.UtcNow);
                if (coworkUsed >= coworkBudget)
                    return new UsageDecision(UsageDecisionKind.LimitExceeded, CoworkBudgetLimit, counter.CoworkWindowResetAt);
                return new UsageDecision(UsageDecisionKind.Allowed);

            case ConexyModelType.ConexyCoder:
                if (agentUsed >= agentBudget)
                    return new UsageDecision(UsageDecisionKind.LimitExceeded, "agent", counter.AgentWindowResetAt);
                return new UsageDecision(UsageDecisionKind.Allowed);

            default:
                return new UsageDecision(UsageDecisionKind.Allowed);
        }
    }

    public async Task RecordRequestAsync(Guid userId, ConexyModelType modelType, CancellationToken ct = default)
    {
        // ADMIN_UNLIMITED: изменено 2026-10-01 — расход админа теперь учитывается (для кружка),
        // но ни на что не влияет: CheckBeforeRunAsync для админа всегда разрешает запуск.
        var user = await _userRepository.GetByIdAsync(userId, ct);
        var counter = await GetOrCreateAsync(userId, user, ct);
        if (modelType == ConexyModelType.ConexyV1Pro)
            counter.ProRequestsUsed++;
        else if (modelType == ConexyModelType.ConexyV1Flash)
            counter.FlashRequestsUsed++;
        await _repository.UpsertAsync(counter, ct);
    }

    public async Task<long> RecordAgentTokensAsync(Guid userId, ConexyModelType modelType, LlmTokenUsage usage, CancellationToken ct = default)
    {
        // PRICED_BILLING: в бюджет идёт взвешенная сумма, а не сырой total_tokens: у DeepSeek кэш-хит
        // стоит ~2% от обычного входа, а выход ~4× — без веса пользователь платил бы за кэшированное
        // как за новое (85% его «расхода» — именно кэш-хиты).
        var billed = BilledTokens(usage);
        if (billed <= 0) return 0;

        // ADMIN_UNLIMITED: изменено 2026-10-01 — расход админа учитывается, но не блокирует.
        var user = await _userRepository.GetByIdAsync(userId, ct);
        var counter = await GetOrCreateAsync(userId, user, ct);
        var limits = GetTierLimits(counter.Tier);

        // TOKEN_TOPUP: billed сначала закрывает бюджет тарифа (окно), а его перелив — купленные токены
        // (бессрочные). Так купленное действительно расходуется один раз, а не возобновляется каждую неделю.
        // COWORK_BUDGET: Cowork платит из своего пула, Coder — из агентского.
        if (modelType == ConexyModelType.ConexyCowork)
        {
            var (toTier, toTopUp) = Split(billed, limits.CoworkTokenBudget, counter.CoworkTokensUsed,
                user?.CoworkTokenTopUp ?? 0L, counter.CoworkTopUpUsed);
            counter.CoworkTokensUsed += toTier;
            counter.CoworkTopUpUsed += toTopUp;
        }
        else
        {
            var (toTier, toTopUp) = Split(billed, limits.AgentTokenBudget, counter.AgentTokensUsed,
                user?.CoderTokenTopUp ?? 0L, counter.CoderTopUpUsed);
            counter.AgentTokensUsed += toTier;
            counter.CoderTopUpUsed += toTopUp;
        }

        await _repository.UpsertAsync(counter, ct);
        // Токен-брейкеру отдаём ПОЛНую взвешенную сумму: он сторожит перерасход внутри прогона
        // и должен видеть реальный расход даже когда обе части лимита уже закрыты.
        return billed;
    }

    // TOKEN_TOPUP: добавлено 2026-10-06
    /// <summary>
    /// Делит начисленное между бюджетом тарифа и купленным пулом: сначала тарифное окно, остаток — в
    /// купленное (но не больше, чем его осталось). Оба пула закрыты — лишнее никуда не пишется (вал
    /// уже заблокирует следующий запуск).
    /// </summary>
    private static (long ToTier, long ToTopUp) Split(long billed, long tierBudget, long tierUsed, long topUpPurchased, long topUpUsed)
    {
        var tierRoom = Math.Max(0L, tierBudget - tierUsed);
        var toTier = Math.Min(billed, tierRoom);
        var overflow = billed - toTier;
        var topUpRoom = Math.Max(0L, topUpPurchased - topUpUsed);
        var toTopUp = Math.Min(overflow, topUpRoom);
        return (toTier, toTopUp);
    }

    // PRICED_BILLING: добавлено 2026-10-06
    /// <summary>
    /// Переводит разбивку хода в «эквивалент входного токена без кэша» с учётом реальных цен
    /// (см. SubscriptionLimitsOptions.CacheHitTokenWeight / OutputTokenWeight). Округление вверх —
    /// чтобы мелкие ходы не обнулялись.
    /// </summary>
    private long BilledTokens(LlmTokenUsage usage)
    {
        var weights = _options.Value;
        var billed = usage.UncachedInput
            + usage.CachedInput * weights.CacheHitTokenWeight
            + usage.Output * weights.OutputTokenWeight;
        return billed <= 0 ? 0 : (long)Math.Ceiling(billed);
    }

    private async Task<UserUsageCounterEntity> GetOrCreateAsync(Guid userId, User? user, CancellationToken ct)
    {
        // TIER_SYNC: тариф счётчика следует за тарифом пользователя. Раньше счётчик создавался
        // бесплатным и таким же оставался навсегда: оплаченный тариф не применялся вообще, а с
        // появлением платного Cowork это заперло бы режим и у тех, кто за него заплатил.
        // YOOKASSA: тариф берём с учётом срока оплаты — просроченный платный тариф равен Free.
        var now = DateTime.UtcNow;
        var tier = EffectiveTier(user, now);

        var counter = await _repository.GetAsync(userId, ct);
        var limits = GetTierLimits(tier);

        if (counter is null)
        {
            counter = NewCounter(userId, tier, now);
            await _repository.UpsertAsync(counter, ct);
            return counter;
        }

        var changed = false;

        if (counter.Tier != tier)
        {
            // Смена тарифа: счётчики обнуляем и окна открываем заново — лимиты нового тарифа должны
            // быть доступны сразу, а не после остатка чужого периода.
            counter.Tier = tier;
            counter.FlashRequestsUsed = 0;
            counter.ProRequestsUsed = 0;
            counter.AgentTokensUsed = 0;
            counter.CoworkTokensUsed = 0;
            counter.FlashWindowResetAt = now.AddDays(limits.FlashWindowDays);
            counter.ProWindowResetAt = now.AddDays(limits.ProWindowDays);
            counter.AgentWindowResetAt = now.AddDays(limits.AgentWindowDays);
            counter.CoworkWindowResetAt = now.AddDays(limits.CoworkWindowDays);
            await _repository.UpsertAsync(counter, ct);
            return counter;
        }

        // Lazy window resets — only persist when something actually changed.
        if (now >= counter.FlashWindowResetAt)
        {
            counter.FlashRequestsUsed = 0;
            counter.FlashWindowResetAt = now.AddDays(limits.FlashWindowDays);
            changed = true;
        }
        if (now >= counter.ProWindowResetAt)
        {
            counter.ProRequestsUsed = 0;
            counter.ProWindowResetAt = now.AddDays(limits.ProWindowDays);
            changed = true;
        }
        if (now >= counter.AgentWindowResetAt)
        {
            counter.AgentTokensUsed = 0;
            counter.AgentWindowResetAt = now.AddDays(limits.AgentWindowDays);
            changed = true;
        }
        // COWORK_BUDGET: своё окно у Cowork.
        if (now >= counter.CoworkWindowResetAt)
        {
            counter.CoworkTokensUsed = 0;
            counter.CoworkWindowResetAt = now.AddDays(limits.CoworkWindowDays);
            changed = true;
        }

        if (changed)
            await _repository.UpsertAsync(counter, ct);

        return counter;
    }

    private UserUsageCounterEntity NewCounter(Guid userId, SubscriptionTier tier, DateTime now)
    {
        // TIER_SYNC: окна сразу считаем по тарифу пользователя, а не по бесплатному — карта
        // «тариф → окна» живёт в конфигурации (GetTierLimits), дублировать её здесь нечем.
        var limits = GetTierLimits(tier);

        return new UserUsageCounterEntity
        {
            UserId = userId,
            Tier = tier,
            FlashRequestsUsed = 0,
            ProRequestsUsed = 0,
            AgentTokensUsed = 0,
            CoworkTokensUsed = 0,
            FlashWindowResetAt = now.AddDays(limits.FlashWindowDays),
            ProWindowResetAt = now.AddDays(limits.ProWindowDays),
            AgentWindowResetAt = now.AddDays(limits.AgentWindowDays),
            CoworkWindowResetAt = now.AddDays(limits.CoworkWindowDays)
        };
    }

    // YOOKASSA: добавлено 2026-09-27
    /// <summary>Тариф с учётом срока оплаты: истёкший платный тариф считается Free.</summary>
    private static SubscriptionTier EffectiveTier(User? user, DateTime now)
    {
        if (user is null) return SubscriptionTier.Free;
        if (user.IsAdmin) return SubscriptionTier.Admin;
        if (user.SubscriptionTier == SubscriptionTier.Free) return SubscriptionTier.Free;
        if (user.SubscriptionExpiresAt is { } expires && expires <= now) return SubscriptionTier.Free;
        return user.SubscriptionTier;
    }

    // TOKEN_TOPUP: добавлено 2026-10-06
    /// <summary>
    /// Эффективный лимит пула = бюджет тарифа + разово докупленные токены. Пополнение хранится на
    /// пользователе, поэтому переживает и смену тарифа, и сброс окна (счётчик при этом обнуляется).
    /// </summary>
    private static long AgentBudget(TierLimits limits, User? user) =>
        limits.AgentTokenBudget + (user?.CoderTokenTopUp ?? 0L);

    private static long CoworkBudget(TierLimits limits, User? user) =>
        limits.CoworkTokenBudget + (user?.CoworkTokenTopUp ?? 0L);

    private TierLimits GetTierLimits(SubscriptionTier tier) => tier switch
    {
        // TIER_GO: добавлено 2026-09-26
        SubscriptionTier.Go => _options.Value.Go,
        SubscriptionTier.Pro => _options.Value.Pro,
        SubscriptionTier.ProMax => _options.Value.ProMax,
        // ANNUAL_ULTRA: добавлено 2026-10-01
        SubscriptionTier.Ultra => _options.Value.Ultra,
        // ADMIN_UNLIMITED: окна админского счётчика берём у Ultra; лимиты всё равно заменяются на ∞.
        SubscriptionTier.Admin => _options.Value.Ultra,
        _ => _options.Value.Free
    };

    // ADMIN_UNLIMITED: изменено 2026-10-01 — расход админа реальный, а лимиты «бесконечные»,
    // поэтому один и тот же кружок работает и у обычного пользователя, и у админа.
    private static SubscriptionUsageDto AdminUsage(UserUsageCounterEntity counter) =>
        new("Admin",
            counter.FlashRequestsUsed, long.MaxValue, counter.FlashWindowResetAt,
            counter.ProRequestsUsed, long.MaxValue, counter.ProWindowResetAt,
            counter.AgentTokensUsed, long.MaxValue, counter.AgentWindowResetAt,
            counter.CoworkTokensUsed, long.MaxValue, counter.CoworkWindowResetAt);
}
