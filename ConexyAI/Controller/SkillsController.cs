using System.Text;
using ConexyAI.Extensions;
using ConexyAI.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ConexyAI.Controller;

// AGENT_SKILLS: добавлено 2026-10-18 — скиллы агента пользователя (одни и те же для веба и десктопа).
[ApiController]
[Route("api/skills")]
[Authorize]
public class SkillsController : ControllerBase
{
    private const long MaxFileBytes = 256 * 1024;
    private readonly ISkillService _skills;

    public SkillsController(ISkillService skills) => _skills = skills;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SkillDto>>> List(CancellationToken ct)
    {
        if (!User.TryGetUserId(out var userId))
            return Unauthorized();
        return Ok(await _skills.ListAsync(userId, ct));
    }

    /// <summary>Загрузка SKILL.md (multipart, поле file). Скилл с тем же именем обновляется.</summary>
    [HttpPost]
    [RequestSizeLimit(MaxFileBytes + 100_000)]
    public async Task<ActionResult<SkillDto>> Upload([FromForm] IFormFile? file, CancellationToken ct)
    {
        if (!User.TryGetUserId(out var userId))
            return Unauthorized();
        if (file is null || file.Length == 0)
            return BadRequest(new { code = "no_file", message = "Выберите файл скилла (.md)." });
        if (file.Length > MaxFileBytes)
            return BadRequest(new { code = "skill_too_large", message = "Файл скилла больше 256 КБ." });

        using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = await reader.ReadToEndAsync(ct);
        try
        {
            return Ok(await _skills.UpsertFromFileAsync(userId, file.FileName, text, ct));
        }
        catch (AuthException ex)
        {
            return StatusCode(ex.StatusCode, new { code = ex.Code, message = ex.Message });
        }
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!User.TryGetUserId(out var userId))
            return Unauthorized();
        return await _skills.DeleteAsync(userId, id, ct) ? NoContent() : NotFound();
    }

    public record EnabledRequest(bool Enabled);

    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<SkillDto>> SetEnabled(Guid id, [FromBody] EnabledRequest request, CancellationToken ct)
    {
        if (!User.TryGetUserId(out var userId))
            return Unauthorized();
        var skill = await _skills.SetEnabledAsync(userId, id, request.Enabled, ct);
        return skill is null ? NotFound() : Ok(skill);
    }
}
