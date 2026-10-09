using ConexyAI.Contract;
using ConexyAI.Extensions;
using ConexyAI.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ConexyAI.Controller;

// SUBSCRIPTION_TIERS: добавлено 2026-09-17
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SubscriptionController : ControllerBase
{
    private readonly ISubscriptionService _subscriptionService;

    public SubscriptionController(ISubscriptionService subscriptionService)
    {
        _subscriptionService = subscriptionService;
    }

    /// <summary>Returns the current user's tier and per-limit usage (for the donut indicator).</summary>
    [HttpGet("usage")]
    public async Task<ActionResult<SubscriptionUsageDto>> GetUsage(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        return Ok(await _subscriptionService.GetUsageAsync(userId, ct));
    }

    // LIMIT_RESET: сброс всех лимитов (акция Pro+, одна на период).
    [HttpPost("reset-limits")]
    public async Task<ActionResult<SubscriptionUsageDto>> ResetLimits(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        try
        {
            return Ok(await _subscriptionService.ResetLimitsAsync(userId, ct));
        }
        catch (AuthException ex)
        {
            return StatusCode(ex.StatusCode, new { code = ex.Code, message = ex.Message });
        }
    }

    private bool TryGetUserId(out Guid userId) => User.TryGetUserId(out userId);
}
