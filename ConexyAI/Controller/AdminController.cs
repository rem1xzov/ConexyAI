using ConexyAI.Configuration;
using ConexyAI.Contract;
using ConexyAI.Entity;
using ConexyAI.Extensions;
using ConexyAI.Model;
using ConexyAI.Repository;
using ConexyAI.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ConexyAI.Controller;

// ADMIN_PANEL: добавлено 2026-09-19
[ApiController]
[Route("api/admin")]
[Authorize]
public class AdminController : ControllerBase
{
    // YOOKASSA: добавлено 2026-09-27 — границы периода для сводки оплат задаются в МСК, потому что
    // чеки в «Мой налог» пробиваются по московским дням, а в базе всё хранится в UTC.
    // Москва не переходит на летнее время, поэтому фиксированный сдвиг UTC+3 корректен всегда.
    private static readonly TimeSpan MskOffset = TimeSpan.FromHours(3);
    /// <summary>Максимальная ширина диапазона сводки — защита от выкачивания всей истории разом.</summary>
    private const int MaxSummaryRangeDays = 366;

    private readonly IUserRepository _userRepository;
    private readonly IOptions<AdminAccountsOptions> _adminOptions;
    private readonly ILogger<AdminController> _logger;
    // YOOKASSA: сводка успешных платежей.
    private readonly IPaymentRepository _paymentRepository;

    // USER_DATA_CLEANUP: добавлено 2026-09-24 — ревью M9: файлы пользователя удаляются вместе с ним.
    private readonly IUserDataCleanupService _cleanup;

    // ADMIN_TOKEN_CREDIT: добавлено 2026-11-19 — ручное начисление токенов пользователю.
    private readonly IAdminTokenService? _tokenService;

    public AdminController(
        IUserRepository userRepository,
        IOptions<AdminAccountsOptions> adminOptions,
        ILogger<AdminController> logger,
        IUserDataCleanupService cleanup,
        IPaymentRepository paymentRepository,
        // Необязательный последний параметр: тесты собирают контроллер вручную.
        IAdminTokenService? tokenService = null)
    {
        _cleanup = cleanup;
        _userRepository = userRepository;
        _adminOptions = adminOptions;
        _logger = logger;
        _paymentRepository = paymentRepository;
        _tokenService = tokenService;
    }

    /// <summary>Paginated list of users (admin only).</summary>
    [HttpGet("users")]
    public async Task<ActionResult<AdminUsersResponse>> GetUsers(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        if (!User.IsAdmin())
            return Forbid();

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var (items, total) = await _userRepository.GetUsersPaginatedAsync(page, pageSize, ct);
        var users = items.Select(ToDto).ToList();
        return Ok(new AdminUsersResponse(total, page, pageSize, users));
    }

    /// <summary>Promotes a user to admin (admin only).</summary>
    [HttpPost("users/{id:guid}/make-admin")]
    public async Task<IActionResult> MakeAdmin(Guid id, CancellationToken ct)
    {
        if (!User.IsAdmin())
            return Forbid();

        var target = await _userRepository.GetByIdAsync(id, ct);
        if (target is null)
            return NotFound();

        target.IsAdmin = true;
        target.SubscriptionTier = SubscriptionTier.Admin;
        await _userRepository.UpdateAsync(target, ct);

        _logger.LogInformation("Admin {Requester} promoted user {Target} to admin.", User.GetUserId(), id);
        return Ok(new { success = true });
    }

    /// <summary>Demotes a user from admin (admin only; superadmins are protected).</summary>
    [HttpPost("users/{id:guid}/revoke-admin")]
    public async Task<IActionResult> RevokeAdmin(Guid id, CancellationToken ct)
    {
        if (!User.IsAdmin())
            return Forbid();

        var target = await _userRepository.GetByIdAsync(id, ct);
        if (target is null)
            return NotFound();

        if (IsSuperAdmin(target))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                code = "superadmin_protected",
                message = "Суперадмина нельзя разжаловать."
            });
        }

        target.IsAdmin = false;
        if (target.SubscriptionTier == SubscriptionTier.Admin)
            target.SubscriptionTier = SubscriptionTier.Free;
        await _userRepository.UpdateAsync(target, ct);
        // TOKEN_REVOCATION: добавлено 2026-09-24 (ревью M19) — права админа снимаются уже следующим
        // запросом (isAdmin в токене подменяется значением из БД), а смена версии вдобавок
        // разлогинивает разжалованного на всех устройствах.
        await _userRepository.BumpTokenVersionAsync(target.Id, ct);

        _logger.LogInformation("Admin {Requester} demoted user {Target} from admin.", User.GetUserId(), id);
        return Ok(new { success = true });
    }

    // ADMIN_SUBSCRIPTION: добавлено 2026-10-09 — админ назначает пользователю тариф вручную
    // (Free/Go/Pro/ProMax/Ultra). Смена тарифа автоматически обнуляет счётчики (TIER_SYNC), поэтому
    // лимиты нового тарифа доступны сразу. Тариф Admin здесь не выдаётся — для этого make-admin.
    [HttpPost("users/{id:guid}/subscription")]
    public async Task<ActionResult<AdminUserDto>> SetSubscription(
        Guid id, [FromBody] AdminSetSubscriptionRequest request, CancellationToken ct)
    {
        if (!User.IsAdmin())
            return Forbid();

        var target = await _userRepository.GetByIdAsync(id, ct);
        if (target is null)
            return NotFound();

        if (!Enum.TryParse<SubscriptionTier>(request.Tier, ignoreCase: true, out var tier) || tier == SubscriptionTier.Admin)
            return BadRequest(new { code = "invalid_tier", message = "Неизвестный тариф." });

        target.SubscriptionTier = tier;
        // Months > 0 — до этой даты; иначе бессрочно (ручная выдача администратором).
        target.SubscriptionExpiresAt = request.Months is > 0
            ? DateTime.UtcNow.AddMonths(request.Months.Value)
            : null;
        // LIMIT_RESET: выданный вручную Pro+ тоже получает акцию сброса лимитов.
        if (IsProOrHigher(tier))
            target.LimitResetAvailable = true;

        await _userRepository.UpdateAsync(target, ct);
        _logger.LogInformation(
            "Admin {Requester} set subscription of user {Target} to {Tier} for {Months} month(s).",
            User.GetUserId(), id, tier, request.Months ?? 0);
        return Ok(ToDto(target));
    }

    // ADMIN_TOKEN_CREDIT: добавлено 2026-11-19 — админ вводит сумму и начисляет токены пользователю.
    /// <summary>
    /// Credits tokens to a user's pool (Coder or Cowork) from the admin panel. Only administrators may
    /// call this; the credit is atomic and recorded in the admin_token_grant journal.
    /// </summary>
    [HttpPost("users/{id:guid}/tokens")]
    public async Task<ActionResult<AdminUserDto>> CreditTokens(
        Guid id, [FromBody] AdminCreditTokensRequest request, CancellationToken ct)
    {
        if (!User.IsAdmin())
            return Forbid();

        if (_tokenService is null)
            return StatusCode(503);

        User.TryGetUserId(out var adminId);
        try
        {
            var credited = await _tokenService.CreditAsync(id, adminId, request.Pool, request.Amount, request.Reason, ct);
            if (!credited)
                return NotFound();
        }
        catch (AuthException ex)
        {
            return StatusCode(ex.StatusCode, new { code = ex.Code, message = ex.Message });
        }

        var target = await _userRepository.GetByIdAsync(id, ct);
        return target is null ? NotFound() : Ok(ToDto(target));
    }

    /// <summary>Deletes a user and all their data via FK cascade (admin only; superadmins are protected).</summary>
    [HttpDelete("users/{id:guid}")]    public async Task<IActionResult> DeleteUser(Guid id, CancellationToken ct)
    {
        if (!User.IsAdmin())
            return Forbid();

        var target = await _userRepository.GetByIdAsync(id, ct);
        if (target is null)
            return NotFound();

        if (IsSuperAdmin(target))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                code = "superadmin_protected",
                message = "Суперадмина нельзя удалить."
            });
        }

        // USER_DATA_CLEANUP: рабочие каталоги и файлы документов — до удаления строки: после каскада
        // уже не узнать, какие чаты и документы были его.
        await _cleanup.DeleteUserFilesAsync(id, ct);

        // TOKEN_REVOCATION: 2026-09-24 — токены удалённого пользователя отклоняются со следующего
        // запроса: проверка токена не находит строку (DeleteAsync сбрасывает кэш).
        await _userRepository.DeleteAsync(id, ct);
        _logger.LogInformation("Admin {Requester} deleted user {Target}.", User.GetUserId(), id);
        return Ok(new { success = true });
    }

    /// <summary>
    /// Сводка успешных платежей за период (только админ) — чтобы вручную пробить сводный чек в
    /// приложении «Мой налог». Успешным считается платёж с проставленным <c>PaidAt</c> (он ставится
    /// только после подтверждения статуса через API ЮKassa).
    /// </summary>
    [HttpGet("payments/summary")]
    public async Task<ActionResult<AdminPaymentSummaryResponse>> GetPaymentsSummary(
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        CancellationToken ct)
    {
        if (!User.IsAdmin())
            return Forbid();

        // По умолчанию — сегодняшний день по Москве.
        var today = DateOnly.FromDateTime(DateTime.UtcNow + MskOffset);
        var fromDate = from ?? today;
        var toDate = to ?? today;

        if (fromDate > toDate)
            return BadRequest(new { error = "INVALID_RANGE", message = "Дата начала позже даты конца." });
        if (toDate.DayNumber - fromDate.DayNumber > MaxSummaryRangeDays)
            return BadRequest(new { error = "RANGE_TOO_WIDE", message = "Слишком широкий диапазон дат." });

        var fromUtc = ToUtcMidnight(fromDate);
        var toUtcExclusive = ToUtcMidnight(toDate.AddDays(1));

        var rows = await _paymentRepository.GetSucceededForPeriodAsync(fromUtc, toUtcExclusive, ct);
        var items = rows
            .Select(r => new AdminPaymentSummaryItem(
                r.PaymentId, r.PaidAt, r.AmountRub, r.PlanId, r.Tier, r.UserId, r.Email, r.GitHubUsername))
            .ToList();

        return Ok(new AdminPaymentSummaryResponse(
            fromDate,
            toDate,
            items.Sum(i => i.AmountRub),
            items.Count,
            items));
    }

    /// <summary>Полночь московского дня, выраженная в UTC (московские сутки начинаются в 21:00 UTC).</summary>
    private static DateTime ToUtcMidnight(DateOnly date) =>
        DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue).Add(-MskOffset), DateTimeKind.Utc);

    // ADMIN_VERIFIED_ONLY: 2026-09-24 (ревью H1) — суперадмин только по GitHub id/username или
    // ПОДТВЕРЖДЁННОМУ email. Иначе аккаунт, занявший email владельца через регистрацию, числился бы
    // «защищённым суперадмином», и его нельзя было бы удалить.
    private bool IsSuperAdmin(User user) => _adminOptions.Value.IsSuperAdmin(user);

    // LIMIT_RESET: акция сброса доступна на Pro и выше.
    private static bool IsProOrHigher(SubscriptionTier tier) =>
        tier is SubscriptionTier.Pro or SubscriptionTier.ProMax or SubscriptionTier.Ultra;

    private AdminUserDto ToDto(User u) => new(
        u.Id,
        u.Email,
        u.GitHubUsername,
        u.CreatedAt,
        // TIER_DISPLAY: тариф с учётом срока — истёкшая подписка показывается как Free.
        SubscriptionService.EffectiveTier(u, DateTime.UtcNow).ToString(),
        u.IsAdmin,
        IsSuperAdmin(u),
        // ADMIN_TOKEN_CREDIT: добавлено 2026-11-19 — сколько токенов куплено/выдано пользователю.
        u.CoderTokenTopUp,
        u.CoworkTokenTopUp);
}
