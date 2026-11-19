namespace ConexyAI.Contract;

// ADMIN_PANEL: добавлено 2026-09-19
/// <summary>One user row for the admin panel.</summary>
public record AdminUserDto(
    Guid Id,
    string? Email,
    string? GitHubUsername,
    DateTime CreatedAt,
    string Tier,
    bool IsAdmin,
    bool IsSuperAdmin,
    // ADMIN_TOKEN_CREDIT: добавлено 2026-11-19 — купленный (и выданный админом) объём токенов.
    long CoderTokenTopUp = 0,
    long CoworkTokenTopUp = 0);

/// <summary>Paginated admin-panel user list.</summary>
public record AdminUsersResponse(
    int TotalCount,
    int Page,
    int PageSize,
    IReadOnlyList<AdminUserDto> Users);

// ADMIN_SUBSCRIPTION: добавлено 2026-10-09 — админ назначает пользователю тариф вручную.
/// <summary>Body for the admin "set subscription" action. <c>Months</c> &gt; 0 — до этой даты,
/// иначе тариф выдаётся бессрочно (ручная выдача).</summary>
public record AdminSetSubscriptionRequest(string Tier, int? Months);

// ADMIN_TOKEN_CREDIT: добавлено 2026-11-19 — админ вводит сумму и начисляет токены пользователю.
/// <summary>Body of <c>POST /api/admin/users/{id}/tokens</c>: pool (Coder|Cowork), amount, reason.</summary>
public record AdminCreditTokensRequest(string? Pool, long Amount, string? Reason);
