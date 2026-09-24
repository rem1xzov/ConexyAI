using ConexyAI.Configuration;
using ConexyAI.Contract;
using ConexyAI.Extensions;
using ConexyAI.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ConexyAI.Controller;

// MEMORY_CONTROL / USER_PREFERENCES: добавлено 2026-09-24.
//
// Ревью H5: память о пользователе извлекалась и подмешивалась во все режимы, но посмотреть или удалить
// её было нельзя. ТЗ 2, §5: пользовательские инструкции («Обо мне», «Как отвечать»). Всё — только для
// владельца токена: id пользователя берётся из клеймов, чужой факт по id не удаляется.
[ApiController]
[Route("api/user")]
[Authorize]
public class UserController : ControllerBase
{
    private readonly IUserMemoryService _memory;
    private readonly IUserPreferencesService _preferences;
    private readonly ILogger<UserController> _logger;

    public UserController(IUserMemoryService memory, IUserPreferencesService preferences, ILogger<UserController> logger)
    {
        _memory = memory;
        _preferences = preferences;
        _logger = logger;
    }

    /// <summary>The user's remembered facts and whether memory is on.</summary>
    [HttpGet("memory")]
    public async Task<ActionResult<MemoryDto>> GetMemory(CancellationToken ct)
    {
        if (!User.TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        var prefs = await _preferences.GetAsync(userId, ct);
        var facts = await _memory.GetFactEntriesAsync(userId, ct);
        return Ok(new MemoryDto(
            prefs.MemoryEnabled,
            facts.Select(f => new MemoryFactDto(f.Id, f.FactText, f.CreatedAt, f.UpdatedAt)).ToList()));
    }

    /// <summary>Deletes one fact. It is not extracted again from the same dialogs.</summary>
    [HttpDelete("memory/{id:guid}")]
    public async Task<IActionResult> DeleteFact(Guid id, CancellationToken ct)
    {
        if (!User.TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        if (!await _memory.DeleteFactAsync(userId, id, ct))
            return NotFound(new { error = "Fact not found." });

        _logger.LogInformation("User {UserId} deleted memory fact {FactId}.", userId, id);
        return NoContent();
    }

    /// <summary>Deletes every remembered fact.</summary>
    [HttpDelete("memory")]
    public async Task<IActionResult> ClearMemory(CancellationToken ct)
    {
        if (!User.TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        await _memory.ClearAsync(userId, ct);
        _logger.LogInformation("User {UserId} cleared their memory.", userId);
        return NoContent();
    }

    /// <summary>Turns long-term memory on or off.</summary>
    [HttpPut("memory/settings")]
    public async Task<IActionResult> SetMemorySettings([FromBody] MemorySettingsDto? dto, CancellationToken ct)
    {
        if (!User.TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        if (dto is null)
            return BadRequest(new { error = "A boolean 'enabled' is required." });

        await _preferences.SetMemoryEnabledAsync(userId, dto.Enabled, ct);
        return NoContent();
    }

    /// <summary>The user's custom instructions.</summary>
    [HttpGet("preferences")]
    public async Task<ActionResult<PreferencesDto>> GetPreferences(CancellationToken ct)
    {
        if (!User.TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        var prefs = await _preferences.GetAsync(userId, ct);
        return Ok(new PreferencesDto(prefs.AboutMe, prefs.ResponseStyle));
    }

    /// <summary>Saves the custom instructions (each field at most 1500 characters).</summary>
    [HttpPut("preferences")]
    public async Task<ActionResult<PreferencesDto>> SavePreferences([FromBody] PreferencesDto? dto, CancellationToken ct)
    {
        if (!User.TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        if (dto is null)
            return BadRequest(new { error = "Body is required." });

        if ((dto.AboutMe?.Trim().Length ?? 0) > UserPreferences_config.MaxTextLength
            || (dto.ResponseStyle?.Trim().Length ?? 0) > UserPreferences_config.MaxTextLength)
            return BadRequest(new { error = "TOO_LONG", max = UserPreferences_config.MaxTextLength });

        var saved = await _preferences.SaveInstructionsAsync(userId, dto.AboutMe, dto.ResponseStyle, ct);
        return Ok(new PreferencesDto(saved.AboutMe, saved.ResponseStyle));
    }
}
