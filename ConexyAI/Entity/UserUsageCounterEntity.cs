using ConexyAI.Model;

namespace ConexyAI.Entity;

// SUBSCRIPTION_TIERS: добавлено 2026-09-17
/// <summary>
/// Per-user usage counters keyed by subscription tier. Flash/pro counters count requests;
/// the agent counter accumulates <c>usage.total_tokens</c> (input+output). Each counter has
/// its own lazy window-reset timestamp.
/// </summary>
public class UserUsageCounterEntity
{
    public Guid UserId { get; set; }
    public SubscriptionTier Tier { get; set; } = SubscriptionTier.Free;

    public int FlashRequestsUsed { get; set; }
    public int ProRequestsUsed { get; set; }
    public long AgentTokensUsed { get; set; }

    public DateTime FlashWindowResetAt { get; set; }
    public DateTime ProWindowResetAt { get; set; }
    public DateTime AgentWindowResetAt { get; set; }
}
