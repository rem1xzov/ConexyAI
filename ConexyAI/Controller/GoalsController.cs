using ConexyAI.Contract;
using ConexyAI.Extensions;
using ConexyAI.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ConexyAI.Controller;

// GOAL: добавлено 2026-11-20 — API долгоживущей цели чата. Цель принадлежит владельцу чата: id чата
// не секрет (им делятся), поэтому владение проверяет сервис по ChatEntity.UserId.
[ApiController]
[Route("api/goals")]
[Authorize]
public class GoalsController : ControllerBase
{
    private readonly IGoalService _goals;

    public GoalsController(IGoalService goals) => _goals = goals;

    /// <summary>Активная цель чата.</summary>
    [HttpGet("{chatId:guid}")]
    public async Task<ActionResult<GoalDto>> Get(Guid chatId, CancellationToken ct)
    {
        if (!User.TryGetUserId(out var userId))
            return Unauthorized();
        var goal = await _goals.GetActiveAsync(userId, chatId, ct);
        return goal is null ? NotFound() : Ok(goal);
    }

    /// <summary>Начать цель (или переопределить активную: раунды начинаются заново).</summary>
    [HttpPost("{chatId:guid}")]
    public async Task<ActionResult<GoalDto>> Start(Guid chatId, [FromBody] CreateGoalRequest? request, CancellationToken ct)
    {
        if (!User.TryGetUserId(out var userId))
            return Unauthorized();
        if (request is null || string.IsNullOrWhiteSpace(request.Objective))
            return BadRequest(new { code = "objective_required", message = "Опишите цель, которую нужно довести до конца." });

        var goal = await _goals.StartAsync(userId, chatId, request.Objective, request.MaxRounds, ct);
        return goal is null
            ? NotFound(new { code = "chat_not_found", message = "Чат не найден или цель пустая/слишком длинная." })
            : Ok(goal);
    }

    /// <summary>Сменить состояние: active (продолжить) | paused | blocked | complete.</summary>
    [HttpPatch("{chatId:guid}")]
    public async Task<ActionResult<GoalDto>> SetStatus(Guid chatId, [FromBody] UpdateGoalRequest? request, CancellationToken ct)
    {
        if (!User.TryGetUserId(out var userId))
            return Unauthorized();
        if (request is null || !GoalStatuses.IsValid(request.Status))
            return BadRequest(new { code = "invalid_status", message = "Статус должен быть active, paused, blocked или complete." });

        var goal = await _goals.SetStatusAsync(userId, chatId, request.Status, request.Reason, ct);
        return goal is null
            ? NotFound(new { code = "goal_not_found", message = "Активная цель не найдена или переход недопустим." })
            : Ok(goal);
    }

    /// <summary>Отменить цель.</summary>
    [HttpDelete("{chatId:guid}")]
    public async Task<IActionResult> Delete(Guid chatId, CancellationToken ct)
    {
        if (!User.TryGetUserId(out var userId))
            return Unauthorized();
        return await _goals.DeleteAsync(userId, chatId, ct) ? NoContent() : NotFound();
    }
}
