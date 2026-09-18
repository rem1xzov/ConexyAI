namespace ConexyAI.Configuration;

// SUBSCRIPTION_TIERS: добавлено 2026-09-17
/// <summary>
/// Binds the <c>SubscriptionLimits</c> section of appsettings.json. Per-tier usage limits:
/// flash/pro are request counts, the agent (conexy-coder) is a token budget
/// (input+output, i.e. DeepSeek <c>usage.total_tokens</c>). Windows reset lazily.
/// </summary>
public class SubscriptionLimitsOptions
{
    public const string SectionName = "SubscriptionLimits";

    public TierLimits Free { get; set; } = new();
    public TierLimits Pro { get; set; } = new();
    public TierLimits ProMax { get; set; } = new();
}

public class TierLimits
{
    public int FlashRequestsPerWindow { get; set; }
    public int ProRequestsPerWindow { get; set; }
    public int AgentTokenBudget { get; set; }
    public int FlashWindowDays { get; set; } = 7;
    public int ProWindowDays { get; set; } = 7;
    public int AgentWindowDays { get; set; } = 30;
}
