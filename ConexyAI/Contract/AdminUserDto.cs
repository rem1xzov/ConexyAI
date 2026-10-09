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
    bool IsSuperAdmin);

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
