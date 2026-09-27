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

    public AdminController(
        IUserRepository userRepository,
        IOptions<AdminAccountsOptions> adminOptions,
        ILogger<AdminController> logger,
        IUserDataCleanupService cleanup,
        IPaymentRepository paymentRepository)
    {
        _cleanup = cleanup;
        _userRepository = userRepository;
        _adminOptions = adminOptions;
        _logger = logger;
        _paymentRepository = paymentRepository;
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

    /// <summary>Deletes a user and all their data via FK cascade (admin only; superadmins are protected).</summary>
    [HttpDelete("users/{id:guid}")]
    public async Task<IActionResult> DeleteUser(Guid id, CancellationToken ct)
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

    private AdminUserDto ToDto(User u) => new(
        u.Id,
        u.Email,
        u.GitHubUsername,
        u.CreatedAt,
        u.SubscriptionTier.ToString(),
        u.IsAdmin,
        IsSuperAdmin(u));
}
