using System.IO.Compression;
using ConexyAI.Contract;
using ConexyAI.Extensions;
using ConexyAI.Repository;
using ConexyAI.Service;
using ConexyAI.Service.Office;
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
    // CHAT_SYNC: добавлено 2026-09-23 — история читается только через свой владелец-сервис.
    private readonly IConversationService _conversation;
    private readonly ILogger<ConexyController> _logger;

    public ConexyController(
        IConexyService conexyService,
        IConexyWorkspaceService workspaceService,
        IConversationService conversation,
        ILogger<ConexyController> logger)
    {
        _conexyService = conexyService;
        _workspaceService = workspaceService;
        _conversation = conversation;
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

    /// <summary>
    /// The signed-in user's chats, newest activity first, so the sidebar can be rebuilt on any
    /// device. Scoped to the token's user id — there is no way to ask for someone else's list.
    /// </summary>
    [HttpGet("chats")]
    public async Task<ActionResult<IReadOnlyList<ChatSummaryDto>>> GetChats(
        [FromQuery] int limit = 50, CancellationToken ct = default)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        var chats = await _conversation.GetChatsAsync(userId, Math.Clamp(limit, 1, 200), ct);
        return Ok(chats.Select(ToChatSummary).ToList());
    }

    /// <summary>Full stored transcript of one chat, oldest first, for its owner only.</summary>
    [HttpGet("chats/{chatId:guid}/messages")]
    public async Task<ActionResult<ChatTranscriptDto>> GetChatMessages(Guid chatId, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        // depth: null -> the whole stored transcript. Ownership is enforced by the repository query
        // (WHERE UserId = current), so asking for someone else's chat id returns an empty transcript.
        var messages = await _conversation.GetHistoryAsync(userId, chatId, incognito: false, depth: null, ct);

        return Ok(new ChatTranscriptDto(
            chatId,
            messages.Select(m => new ChatTranscriptMessageDto(m.Role, m.Content, m.CreatedAt)).ToList()));
    }

    /// <summary>
    /// Chat list projection. The chat <em>kind</em> is inferred from the workspace: a chat that has
    /// one on disk is a conexy-coder (agent) chat, because the workspace is keyed by the chat id and
    /// only the agent path creates it. Without this a synced agent chat would land in the wrong
    /// sidebar tab. (Students mode is not recoverable here — it is not stored anywhere.)
    /// </summary>
    private ChatSummaryDto ToChatSummary(ChatListSummary chat)
    {
        // CHAT_KIND_SYNC: сначала сохранённый режим — он точен для всех трёх вкладок (включая
        // «учеников», которые иначе неопределимы). Фолбэк по workspace нужен только для строк,
        // записанных до появления колонки: у агентских чатов на диске есть workspace, у остальных нет.
        var kind = chat.Kind;
        if (string.IsNullOrEmpty(kind))
        {
            kind = _workspaceService.GetTaskWorkspacePathIfExists(chat.ChatId) is not null
                ? "projects"
                : "chat";
        }

        return new ChatSummaryDto(
            chat.ChatId,
            ForPreview(chat.FirstUserMessage),
            kind,
            chat.LastActivityAt,
            chat.MessageCount,
            ForPreview(chat.LastAssistantMessage));
    }

    /// <summary>Collapses a stored message into a single-line, bounded preview.</summary>
    private static string? ForPreview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var line = text.ReplaceLineEndings(" ").Trim();
        if (line.Length == 0) return null;
        return line.Length > 120 ? line[..120] + "…" : line;
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

    // OFFICE_FORMATS: добавлено 2026-09-23 — documents the agents create (.docx/.xlsx/.pptx) are binary:
    // the editor shows "binary file" for them, so they need a plain download.
    /// <summary>Streams a single workspace file as-is.</summary>
    [HttpGet("workspace/{sessionId}/raw")]
    public async Task<IActionResult> DownloadWorkspaceFile(string sessionId, [FromQuery] string path, CancellationToken ct)
    {
        if (!TryGetUserId(out _))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        if (!TryParseWorkspaceId(sessionId, out var chatId, out var error))
            return error;

        if (string.IsNullOrWhiteSpace(path))
            return BadRequest(new { error = "Query parameter 'path' is required." });

        var read = await _workspaceService.ReadBytesAsync(chatId, path, ct);
        if (!read.Success)
            return NotFound(new { error = read.Error });

        var contentType = OfficeDocumentWriter.TryParseFormat(Path.GetExtension(path), out var format)
            ? OfficeDocumentWriter.ContentType(format)
            : Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? "application/pdf" : "application/octet-stream";
        return File(read.Content!, contentType, Path.GetFileName(path));
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

    // OFFICE_FORMATS: добавлено 2026-09-23
    /// <summary>
    /// Any model's answer (flash, pro, students, coder, cowork) as .docx / .xlsx / .pptx. The chat
    /// models have no tools, so this is how every mode gets office output, not only the agents.
    /// </summary>
    [HttpPost("export")]
    [RequestSizeLimit(4_000_000)]
    public IActionResult ExportDocument([FromBody] ExportDocumentRequest request)
    {
        if (!TryGetUserId(out _))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        if (!OfficeDocumentWriter.TryParseFormat(request.Format, out var format))
            return BadRequest(new { error = "Format must be docx, xlsx or pptx." });

        if (string.IsNullOrWhiteSpace(request.Markdown))
            return BadRequest(new { error = "Nothing to export." });

        var name = ExportFileName(request.FileName);
        var bytes = OfficeDocumentWriter.Create(format, request.Markdown, name);
        return File(bytes, OfficeDocumentWriter.ContentType(format), name + OfficeDocumentWriter.Extension(format));
    }

    private static string ExportFileName(string? requested)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string((requested ?? string.Empty).Where(c => !invalid.Contains(c) && !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length > 80) cleaned = cleaned[..80].Trim();
        return cleaned.Length == 0 ? "conexy-answer" : cleaned;
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
