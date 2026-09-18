namespace ConexyAI.Contract;

// SUBSCRIPTION_TIERS: добавлено 2026-09-17
/// <summary>Thrown when a user's tier limit is exhausted; the controller maps it to HTTP 429.</summary>
public class LimitExceededException : Exception
{
    public string LimitName { get; }
    public DateTime ResetsAt { get; }

    public LimitExceededException(string limitName, DateTime resetsAt)
        : base("LIMIT_EXCEEDED")
    {
        LimitName = limitName;
        ResetsAt = resetsAt;
    }
}

public enum UsageDecisionKind
{
    Allowed,
    FallbackToFlash,
    LimitExceeded
}

/// <summary>Outcome of the pre-run limit check.</summary>
public record UsageDecision(UsageDecisionKind Kind, string? LimitName = null, DateTime? ResetsAt = null);

/// <summary>Current usage snapshot returned to the frontend for the donut indicator.</summary>
public record SubscriptionUsageDto(
    string Tier,
    long FlashUsed,
    long FlashLimit,
    DateTime FlashResetsAt,
    long ProUsed,
    long ProLimit,
    DateTime ProResetsAt,
    long AgentUsed,
    long AgentLimit,
    DateTime AgentResetsAt);
