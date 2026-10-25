namespace ConexyAI.Model;

// SUBSCRIPTION_TIERS: добавлено 2026-09-17
// TIER_GO: добавлено 2026-09-26 — промежуточный тариф Go между Free и Pro (открывает Cowork).
// Порядок значений — это порядок тарифов «от младшего к старшему»; в базе тариф хранится СТРОКОЙ
// (HasConversion<string>()), поэтому перенумерация безопасна.
public enum SubscriptionTier
{
    Free = 0,
    Go = 1,
    Pro = 2,
    ProMax = 3,
    // ULTRA: добавлено 2026-10-01 — тариф с самыми большими лимитами, продаётся помесячно.
    Ultra = 4,
    // ADMIN_UNLIMITED: добавлено 2026-09-19 — synthetic tier for admins (unlimited).
    Admin = 5
}
