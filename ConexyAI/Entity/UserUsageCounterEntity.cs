using ConexyAI.Model;

namespace ConexyAI.Entity;

// SUBSCRIPTION_TIERS: добавлено 2026-09-17
/// <summary>
/// Per-user usage counters keyed by subscription tier. Flash/pro counters count requests; the agent
/// counters accumulate <c>usage.total_tokens</c> (input+output). Each counter has its own lazy
/// window-reset timestamp.
/// </summary>
public class UserUsageCounterEntity
{
    public Guid UserId { get; set; }
    public SubscriptionTier Tier { get; set; } = SubscriptionTier.Free;

    public int FlashRequestsUsed { get; set; }
    public int ProRequestsUsed { get; set; }
    public long AgentTokensUsed { get; set; }

    // COWORK_BUDGET: добавлено 2026-09-26 — отдельный счётчик Cowork, чтобы исследовательские
    // прогоны не съедали бюджет агента-кодера и наоборот.
    public long CoworkTokensUsed { get; set; }

    // TOKEN_TOPUP: добавлено 2026-10-06 — сколько РАЗОВО КУПЛЕННЫХ токенов уже израсходовано.
    // В отличие от AgentTokensUsed/CoworkTokensUsed, эти счётчики НИКОГДА не сбрасываются ни по окну,
    // ни при смене тарифа: покупка конечна («закрывается, когда израсходует»), а не пополняется
    // каждую неделю. Купленный объём лежит на пользователе (User.CoderTokenTopUp/CoworkTokenTopUp).
    public long CoderTopUpUsed { get; set; }
    public long CoworkTopUpUsed { get; set; }

    // CACHE_STATS: добавлено 2026-10-06 — сколько ВХОДНЫХ токенов прочитано из кэша префикса (сырые,
    // до скидки). Нужно ТОЛЬКО для показа пользователю («прочитано из кэша — со скидкой»); на лимит не
    // влияет (его считает взвешенная сумма в CoderTopUpUsed/CoworkTopUpUsed и *TokensUsed).
    // Сбрасывается вместе с окном, как AgentTokensUsed/CoworkTokensUsed.
    public long CoderCachedTokens { get; set; }
    public long CoworkCachedTokens { get; set; }

    public DateTime FlashWindowResetAt { get; set; }
    public DateTime ProWindowResetAt { get; set; }
    public DateTime AgentWindowResetAt { get; set; }
    public DateTime CoworkWindowResetAt { get; set; }
}
