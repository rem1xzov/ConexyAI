using ConexyAI.Contract;
using ConexyAI.Extensions;
using ConexyAI.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ConexyAI.Controller;

/// <summary>
/// File-explorer CRUD for the Agent IDE. Read/write operations are plain request/response
/// (REST) rather than SignalR; mutations broadcast a <c>ToolActionEvent</c> so the UI tree
/// refreshes identically regardless of whether the change came from the user or the agent.
/// </summary>
[ApiController]
[Route("api/sessions")]
[Authorize]
public class IdeController : ControllerBase
{
    private readonly IIdeFileService _fileService;
    // CHAT_OWNERSHIP: добавлено 2026-09-24 — ревью C1: раньше id пользователя выбрасывался
    // (TryGetUserId(out _)), и чужой воркспейс читался и правился по одному chatId.
    private readonly IChatAccessService _chatAccess;
    private readonly IConexyWorkspaceService _workspace;
    private readonly ILogger<IdeController> _logger;

    public IdeController(
        IIdeFileService fileService,
        IChatAccessService chatAccess,
        IConexyWorkspaceService workspace,
        ILogger<IdeController> logger)
    {
        _fileService = fileService;
        _chatAccess = chatAccess;
        _workspace = workspace;
        _logger = logger;
    }

    /// <summary>Lists the immediate children of a workspace directory.</summary>
    [HttpGet("{sessionId:guid}/files")]
    public async Task<ActionResult<IReadOnlyList<FileNode>>> ListFiles(
        Guid sessionId,
        [FromQuery] string path = "",
        CancellationToken ct = default)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        if (!await _chatAccess.CanReadAsync(userId, sessionId, ct))
            return Forbidden();

        // Reading never creates the workspace: a brand-new chat simply has no files yet.
        if (_workspace.GetTaskWorkspacePathIfExists(sessionId) is null)
            return Ok(Array.Empty<FileNode>());

        try
        {
            return Ok(await _fileService.ListAsync(sessionId, path, ct));
        }
        catch (Exception ex)
        {
            return MapError(ex);
        }
    }

    /// <summary>Returns the content of a single workspace file for the editor.</summary>
    [HttpGet("{sessionId:guid}/files/content")]
    public async Task<ActionResult<FileContentResponse>> GetFileContent(
        Guid sessionId,
        [FromQuery] string path,
        CancellationToken ct = default)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        if (string.IsNullOrWhiteSpace(path))
            return BadRequest(new { error = "Query parameter 'path' is required." });

        if (!await _chatAccess.CanReadAsync(userId, sessionId, ct))
            return Forbidden();

        if (_workspace.GetTaskWorkspacePathIfExists(sessionId) is null)
            return NotFound(new { error = $"'{path}' not found." });

        try
        {
            return Ok(await _fileService.GetContentAsync(sessionId, path, ct));
        }
        catch (Exception ex)
        {
            return MapError(ex);
        }
    }

    /// <summary>Saves the user's manual edits to a workspace file.</summary>
    [HttpPut("{sessionId:guid}/files/content")]
    public async Task<IActionResult> SaveFile(
        Guid sessionId,
        [FromBody] SaveFileRequest? request,
        CancellationToken ct = default)
    {
        if (!TryGetUserId(out _))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        if (request == null || string.IsNullOrWhiteSpace(request.Path))
            return BadRequest(new { error = "Invalid request body." });

        var denied = await AuthorizeWriteAsync(sessionId, ct);
        if (denied is not null)
            return denied;

        try
        {
            await _fileService.SaveAsync(sessionId, request.Path, request.Content, ct);
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            return MapError(ex);
        }
    }

    /// <summary>Creates a file or directory in the workspace.</summary>
    [HttpPost("{sessionId:guid}/files")]
    public async Task<IActionResult> Create(
        Guid sessionId,
        [FromBody] CreateFileRequest? request,
        CancellationToken ct = default)
    {
        if (!TryGetUserId(out _))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        if (request == null || string.IsNullOrWhiteSpace(request.Path))
            return BadRequest(new { error = "Invalid request body." });

        var denied = await AuthorizeWriteAsync(sessionId, ct);
        if (denied is not null)
            return denied;

        try
        {
            await _fileService.CreateAsync(sessionId, request.Path, request.IsDirectory, ct);
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            return MapError(ex);
        }
    }

    /// <summary>Deletes a file or directory.</summary>
    [HttpDelete("{sessionId:guid}/files")]
    public async Task<IActionResult> Delete(
        Guid sessionId,
        [FromQuery] string path,
        CancellationToken ct = default)
    {
        if (!TryGetUserId(out _))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        if (string.IsNullOrWhiteSpace(path))
            return BadRequest(new { error = "Query parameter 'path' is required." });

        var denied = await AuthorizeOwnerAsync(sessionId, ct);
        if (denied is not null)
            return denied;

        try
        {
            await _fileService.DeleteAsync(sessionId, path, ct);
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            return MapError(ex);
        }
    }

    /// <summary>Renames (or moves) a file or directory.</summary>
    [HttpPost("{sessionId:guid}/files/rename")]
    public async Task<IActionResult> Rename(
        Guid sessionId,
        [FromBody] RenameFileRequest? request,
        CancellationToken ct = default)
    {
        if (!TryGetUserId(out _))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        if (request == null || string.IsNullOrWhiteSpace(request.OldPath) || string.IsNullOrWhiteSpace(request.NewPath))
            return BadRequest(new { error = "Invalid request body." });

        var denied = await AuthorizeOwnerAsync(sessionId, ct);
        if (denied is not null)
            return denied;

        try
        {
            await _fileService.RenameAsync(sessionId, request.OldPath, request.NewPath, ct);
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            return MapError(ex);
        }
    }

    private ObjectResult Forbidden() =>
        StatusCode(StatusCodes.Status403Forbidden, new { error = "CHAT_FORBIDDEN" });

    /// <summary>Owner check for writes; a brand-new chat is claimed by the caller.</summary>
    private async Task<IActionResult?> AuthorizeWriteAsync(Guid chatId, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });
        try
        {
            await _chatAccess.EnsureWritableAsync(userId, chatId, ct: ct);
            return null;
        }
        catch (ChatAccessDeniedException)
        {
            return Forbidden();
        }
    }

    /// <summary>Owner check for destructive operations on existing content.</summary>
    private async Task<IActionResult?> AuthorizeOwnerAsync(Guid chatId, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });
        return await _chatAccess.GetAccessAsync(userId, chatId, ct) == ChatAccessKind.Owner ? null : Forbidden();
    }

    private ActionResult MapError(Exception ex)
    {
        return ex switch
        {
            // WORKSPACE_JAIL: a command is running in the workspace — retry later.
            WorkspaceBusyException => Conflict(new { error = "WORKSPACE_BUSY" }),
            UnauthorizedAccessException => BadRequest(new { error = ex.Message }),
            FileNotFoundException => NotFound(new { error = ex.Message }),
            DirectoryNotFoundException => NotFound(new { error = ex.Message }),
            InvalidOperationException => Conflict(new { error = ex.Message }),
            IOException => BadRequest(new { error = ex.Message }),
            _ => StatusCode(StatusCodes.Status500InternalServerError, new { error = "INTERNAL_ERROR" })
        };
    }

    private bool TryGetUserId(out Guid userId) => User.TryGetUserId(out userId);
}
