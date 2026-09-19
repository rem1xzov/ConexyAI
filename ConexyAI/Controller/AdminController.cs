using ConexyAI.Configuration;
using ConexyAI.Contract;
using ConexyAI.Entity;
using ConexyAI.Extensions;
using ConexyAI.Model;
using ConexyAI.Repository;
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
    private readonly IUserRepository _userRepository;
    private readonly IOptions<AdminAccountsOptions> _adminOptions;
    private readonly ILogger<AdminController> _logger;

    public AdminController(
        IUserRepository userRepository,
        IOptions<AdminAccountsOptions> adminOptions,
        ILogger<AdminController> logger)
    {
        _userRepository = userRepository;
        _adminOptions = adminOptions;
        _logger = logger;
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

        await _userRepository.DeleteAsync(id, ct);
        _logger.LogInformation("Admin {Requester} deleted user {Target}.", User.GetUserId(), id);
        return Ok(new { success = true });
    }

    private bool IsSuperAdmin(User user) =>
        _adminOptions.Value.Matches(user.Email, user.GitHubUsername);

    private AdminUserDto ToDto(User u) => new(
        u.Id,
        u.Email,
        u.GitHubUsername,
        u.CreatedAt,
        u.SubscriptionTier.ToString(),
        u.IsAdmin,
        IsSuperAdmin(u));
}
