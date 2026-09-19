namespace ConexyAI.Model;

// SUBSCRIPTION_TIERS: добавлено 2026-09-17
public enum SubscriptionTier
{
    Free = 0,
    Pro = 1,
    ProMax = 2,
    // ADMIN_UNLIMITED: добавлено 2026-09-19 — synthetic tier for admins (unlimited).
    Admin = 3
}
