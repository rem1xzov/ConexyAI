namespace ConexyAI.Configuration;

// SUBSCRIPTION_TIERS: добавлено 2026-09-17
/// <summary>
/// Binds the <c>SubscriptionLimits</c> section of appsettings.json. Per-tier usage limits: flash/pro
/// are request counts; the two agent modes have separate token budgets (input+output, i.e. DeepSeek
/// <c>usage.total_tokens</c>) — <c>conexy-coder</c> and <c>conexy-cowork</c> are billed against their
/// own pools, so a heavy research session in Cowork never eats the coding budget. Windows reset lazily.
/// </summary>
public class SubscriptionLimitsOptions
{
    public const string SectionName = "SubscriptionLimits";

    public TierLimits Free { get; set; } = new();

    // TIER_GO: добавлено 2026-09-26 — промежуточный тариф за 590 ₽: между Free и Pro.
    public TierLimits Go { get; set; } = new();

    public TierLimits Pro { get; set; } = new();
    public TierLimits ProMax { get; set; } = new();

    // ANNUAL_ULTRA: добавлено 2026-10-01 — годовой тариф Ultra (самые большие лимиты).
    public TierLimits Ultra { get; set; } = new();

    // PRICED_BILLING: добавлено 2026-10-06 — коэффициенты цены для оплаты «по цене». Бюджет тарифа
    // считается в «эквиваленте входного токена без кэша»: обычный вход ×1, вход с попаданием в кэш ×
    // CacheHitTokenWeight, выход × OutputTokenWeight. По прайсу DeepSeek: кэш-хит = 0,003 / 0,15 = 0,02,
    // выход = 0,60 / 0,15 = 4,0 (отношения одинаковы и для ночного, и для дневного тарифа).
    /// <summary>Цена кэш-хит входного токена относительно обычного входа (без кэша).</summary>
    public double CacheHitTokenWeight { get; set; } = 0.02;

    /// <summary>Цена выходного токена относительно обычного входа (без кэша).</summary>
    public double OutputTokenWeight { get; set; } = 4.0;
}

public class TierLimits
{
    public int FlashRequestsPerWindow { get; set; }
    public int ProRequestsPerWindow { get; set; }

    /// <summary>Token budget of the coding agent (<c>conexy-coder</c>) per window.</summary>
    public int AgentTokenBudget { get; set; }

    // COWORK_BUDGET: добавлено 2026-09-26
    /// <summary>
    /// Token budget of Cowork (<c>conexy-cowork</c>) per window — a pool of its own, separate from
    /// <see cref="AgentTokenBudget"/>.
    /// </summary>
    public int CoworkTokenBudget { get; set; }

    /// <summary>
    /// Whether the tier may open Cowork at all. Cowork is a paid feature: the free tier keeps the mode
    /// locked, and the limit check refuses it with its own reason (<c>cowork_plan</c>) so the UI can say
    /// "available on a paid plan" instead of pretending the budget ran out.
    /// </summary>
    public bool CoworkEnabled { get; set; }

    public int FlashWindowDays { get; set; } = 7;
    public int ProWindowDays { get; set; } = 7;
    public int AgentWindowDays { get; set; } = 30;
    public int CoworkWindowDays { get; set; } = 7;
}
