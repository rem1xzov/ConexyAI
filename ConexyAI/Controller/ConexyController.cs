using System.IO.Compression;
using ConexyAI.Contract;
using ConexyAI.Extensions;
using ConexyAI.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ConexyAI.Controller;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ConexyController : ControllerBase
{
    private readonly IConexyService _conexyService;
    private readonly IConexyWorkspaceService _workspaceService;
    private readonly ILogger<ConexyController> _logger;

    public ConexyController(
        IConexyService conexyService,
        IConexyWorkspaceService workspaceService,
        ILogger<ConexyController> logger)
    {
        _conexyService = conexyService;
        _workspaceService = workspaceService;
        _logger = logger;
    }

    [HttpPost("run")]
    // ATTACHMENT_ERRORS: добавлено 2026-09-21 — base64 attachments ride in the JSON body, so the
    // request needs the same headroom as the other upload endpoints instead of Kestrel's default.
    [RequestSizeLimit(55_000_000)]
    public async Task<ActionResult<ConexyResponse>> RunTask([FromBody] ConexyRequest request, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized(new { error = "Valid user id claim not found in token." });
        }

        if (!ModelState.IsValid)
        {
            var errors = string.Join("; ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
            _logger.LogError("Validation error in /run: {Errors}", errors);
            return BadRequest(new { message = errors });
        }

        try
        {
            // Enqueue + return 202 immediately; all streaming flows through SignalR.
            var response = await _conexyService.ExecuteAsync(userId, request, ct);
            return AcceptedAtAction(nameof(GetStatus), new { id = response.Id }, response);
        }
        catch (RateLimitExceededException ex)
        {
            _logger.LogWarning("Rate limit exceeded. Model='{Model}'", request.Model);
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = ex.Message });
        }
        catch (LimitExceededException ex)
        {
            // SUBSCRIPTION_TIERS: добавлено 2026-09-17
            _logger.LogWarning("Subscription limit exceeded. Model='{Model}' Limit='{Limit}'", request.Model, ex.LimitName);
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                error = "LIMIT_EXCEEDED",
                limit = ex.LimitName,
                resetsAt = ex.ResetsAt
            });
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning("Rejected request. Model='{Model}' Error='{Error}'", request.Model, ex.Message);
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("cannot be tracked"))
        {
            // This is an EF Core ChangeTracker conflict, not a rate limit. Return 409 so
            // clients/monitoring retry the state instead of treating it as throttling.
            _logger.LogError(ex, "EF Core tracking conflict. Model='{Model}' SessionId='{SessionId}'", request.Model, request.SessionId);
            return Conflict(new { error = "concurrent_modification", detail = "Entity state conflict, retry the operation." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while running task. Model='{Model}'", request.Model);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = ex.Message });
        }
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ConexyResponse>> GetStatus(Guid id, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized(new { error = "Valid user id claim not found in token." });
        }

        var response = await _conexyService.GetByIdAsync(id, userId, ct);
        if (response == null) return NotFound();
        return Ok(response);
    }

    /// <summary>Lists the files (flat paths + nested tree) created by the agent in a session workspace.</summary>
    [HttpGet("workspace/{sessionId}/files")]
    public ActionResult<WorkspaceListing> GetWorkspaceFiles(string sessionId)
    {
        if (!TryGetUserId(out _))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        if (!TryParseWorkspaceId(sessionId, out var chatId, out var error))
            return error;

        // Never 404 for a workspace that has not been created yet: create it (or
        // fall back to an empty listing) so the Agent IDE always renders cleanly.
        var root = _workspaceService.GetTaskWorkspacePath(chatId);

        if (!Directory.Exists(root))
            return Ok(new WorkspaceListing(Array.Empty<string>(), Array.Empty<WorkspaceFileEntry>()));

        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => NormalizeSeparators(Path.GetRelativePath(root, f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Ok(new WorkspaceListing(files, BuildTree(root, root)));
    }

    /// <summary>Returns the raw content of a single workspace file.</summary>
    [HttpGet("workspace/{sessionId}/file")]
    public async Task<ActionResult<WorkspaceFileContent>> GetWorkspaceFile(string sessionId, [FromQuery] string path, CancellationToken ct)
    {
        if (!TryGetUserId(out _))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        if (!TryParseWorkspaceId(sessionId, out var chatId, out var error))
            return error;

        if (string.IsNullOrWhiteSpace(path))
            return BadRequest(new { error = "Query parameter 'path' is required." });

        var read = await _workspaceService.ReadFileAsync(chatId, path, ct);
        if (!read.Success)
            return NotFound(new { error = read.Error });

        return Ok(new WorkspaceFileContent(path, Path.GetFileName(path), read.Content!));
    }

    /// <summary>Saves manual edits made in the Workspace code editor.</summary>
    [HttpPut("workspace/{sessionId}/file")]
    public async Task<IActionResult> SaveWorkspaceFile(string sessionId, [FromBody] SaveFileDto? dto, CancellationToken ct)
    {
        if (!TryGetUserId(out _))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        if (!TryParseWorkspaceId(sessionId, out var chatId, out var error))
            return error;

        if (dto == null || string.IsNullOrWhiteSpace(dto.Path) || dto.Path.Contains(".."))
            return BadRequest(new { error = "Invalid path." });

        var res = await _workspaceService.WriteFileAsync(chatId, dto.Path, dto.Content ?? string.Empty, ct);
        if (!res.Success)
            return BadRequest(new { error = res.Error });

        return Ok(new { success = true });
    }

    /// <summary>Deletes a single workspace file from the File Explorer.</summary>
    [HttpDelete("workspace/{sessionId}/file")]
    public async Task<IActionResult> DeleteWorkspaceFile(string sessionId, [FromQuery] string path, CancellationToken ct)
    {
        if (!TryGetUserId(out _))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        if (!TryParseWorkspaceId(sessionId, out var chatId, out var error))
            return error;

        if (string.IsNullOrWhiteSpace(path) || path.Contains(".."))
            return BadRequest(new { error = "Invalid path." });

        var res = await _workspaceService.DeleteFileAsync(chatId, path, ct);
        if (!res.Success)
            return NotFound(new { error = res.Error });

        return Ok(new { success = true });
    }

    /// <summary>Zips the session workspace and streams it back as a download.</summary>
    [HttpGet("workspace/{sessionId}/download-zip")]
    public async Task<IActionResult> DownloadWorkspaceZip(string sessionId, CancellationToken ct)
    {
        if (!TryGetUserId(out _))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        if (!TryParseWorkspaceId(sessionId, out var chatId, out var error))
            return error;

        var root = _workspaceService.GetTaskWorkspacePath(chatId);
        if (!Directory.Exists(root) || !Directory.EnumerateFileSystemEntries(root).Any())
            return NotFound(new { error = "Workspace is empty." });

        var zipPath = Path.Combine(Path.GetTempPath(), $"conexy-{chatId:N}.zip");
        try
        {
            if (System.IO.File.Exists(zipPath))
                System.IO.File.Delete(zipPath);

            ZipFile.CreateFromDirectory(root, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false);

            var bytes = await System.IO.File.ReadAllBytesAsync(zipPath, ct);
            return new FileContentResult(bytes, "application/zip")
            {
                FileDownloadName = $"workspace-{chatId:N}.zip"
            };
        }
        finally
        {
            if (System.IO.File.Exists(zipPath))
            {
                try { System.IO.File.Delete(zipPath); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>Extracts an uploaded ZIP archive into the session workspace.</summary>
    [HttpPost("workspace/{sessionId}/upload-zip")]
    [RequestSizeLimit(55_000_000)] // ~50MB file + multipart overhead
    public async Task<IActionResult> UploadWorkspaceZip(string sessionId, [FromForm] IFormFile file, CancellationToken ct)
    {
        if (!TryGetUserId(out _))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        if (!TryParseWorkspaceId(sessionId, out var chatId, out var error))
            return error;

        if (file is null || file.Length == 0)
            return BadRequest(new { error = "No file uploaded." });

        const long MaxBytes = 50L * 1024 * 1024;
        if (file.Length > MaxBytes)
            return BadRequest(new { error = "File exceeds the 50MB limit." });

        var root = _workspaceService.GetTaskWorkspacePath(chatId);
        Directory.CreateDirectory(root);

        var tempPath = Path.Combine(Path.GetTempPath(), $"conexy-upload-{Guid.NewGuid():N}.zip");
        try
        {
            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            {
                await file.CopyToAsync(fs, ct);
            }

            var rootFull = Path.GetFullPath(root);
            var rootPrefix = rootFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            using var archive = ZipFile.OpenRead(tempPath);
            foreach (var entry in archive.Entries)
            {
                // Skip directory entries (they have no file name).
                if (string.IsNullOrEmpty(entry.Name))
                    continue;

                var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                var destPath = Path.GetFullPath(Path.Combine(rootFull, relative));
                if (!destPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                    return BadRequest(new { error = "ZIP contains invalid paths." });

                var dir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                entry.ExtractToFile(destPath, overwrite: true);
            }
        }
        finally
        {
            if (System.IO.File.Exists(tempPath))
            {
                try { System.IO.File.Delete(tempPath); } catch { /* best effort */ }
            }
        }

        return Ok(new { extracted = true });
    }

    private bool TryParseWorkspaceId(string sessionId, out Guid chatId, out ActionResult error)
    {
        chatId = Guid.Empty;
        error = null!;

        if (!Guid.TryParse(sessionId, out chatId))
        {
            error = BadRequest(new { error = "Invalid session id." });
            return false;
        }

        return true;
    }

    private static string NormalizeSeparators(string path) => path.Replace('\\', '/');

    private static IReadOnlyList<WorkspaceFileEntry> BuildTree(string root, string directory)
    {
        var entries = new List<WorkspaceFileEntry>();

        foreach (var dir in Directory.EnumerateDirectories(directory).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var rel = NormalizeSeparators(Path.GetRelativePath(root, dir));
            entries.Add(new WorkspaceFileEntry(Path.GetFileName(dir), rel, true, 0, BuildTree(root, dir)));
        }

        foreach (var file in Directory.EnumerateFiles(directory).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var rel = NormalizeSeparators(Path.GetRelativePath(root, file));
            long size = 0;
            try { size = new FileInfo(file).Length; } catch { /* ignore */ }
            entries.Add(new WorkspaceFileEntry(Path.GetFileName(file), rel, false, size, Array.Empty<WorkspaceFileEntry>()));
        }

        return entries;
    }

    private bool TryGetUserId(out Guid userId)
    {
        return User.TryGetUserId(out userId);
    }
}
