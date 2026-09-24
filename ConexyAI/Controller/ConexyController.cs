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
    // CHAT_OWNERSHIP: добавлено 2026-09-24 — ревью C1: каждый чат-ресурс проверяет владельца.
    private readonly IChatAccessService _chatAccess;
    private readonly IIncognitoChatStore _incognito;
    private readonly ISandboxActivity _sandboxActivity;
    private readonly IConexyCancellationRegistry _cancellations;
    private readonly ILogger<ConexyController> _logger;

    public ConexyController(
        IConexyService conexyService,
        IConexyWorkspaceService workspaceService,
        IConversationService conversation,
        IChatAccessService chatAccess,
        IIncognitoChatStore incognito,
        ISandboxActivity sandboxActivity,
        IConexyCancellationRegistry cancellations,
        ILogger<ConexyController> logger)
    {
        _conexyService = conexyService;
        _workspaceService = workspaceService;
        _conversation = conversation;
        _chatAccess = chatAccess;
        _incognito = incognito;
        _sandboxActivity = sandboxActivity;
        _cancellations = cancellations;
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
        catch (ChatAccessDeniedException)
        {
            // CHAT_OWNERSHIP: ревью C1 — ход в чужом чате.
            _logger.LogWarning("Run refused: chat {ChatId} is not owned by user {UserId}.", request.ChatId, userId);
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "CHAT_FORBIDDEN" });
        }
        catch (TurnInFlightException)
        {
            // TURN_IN_FLIGHT: ревью H6 — ход с этим id ещё идёт.
            return Conflict(new { error = "TURN_IN_FLIGHT" });
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
            // L12: the raw exception text stays in the log, the client gets a stable code.
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "INTERNAL_ERROR" });
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
    /// <para>
    /// CHAT_SYNC_COMPLETE: изменено 2026-09-24 — ревью H8. Список ограничен лимитом, а клиент считал
    /// «нет в списке» = «удалён на сервере» и вычищал 51-й и более старые чаты (включая закреплённые)
    /// со всех устройств. Теперь закреплённые чаты приходят всегда, а <c>allChatIds</c> — полный набор
    /// id: удалять локально можно только то, чего в нём нет.
    /// </para>
    /// </summary>
    [HttpGet("chats")]
    public async Task<ActionResult<ChatListDto>> GetChats(
        [FromQuery] int limit = 200, CancellationToken ct = default)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        var chats = await _conversation.GetChatsAsync(userId, Math.Clamp(limit, 1, 500), ct);
        var allIds = await _conversation.GetChatIdsAsync(userId, ct);
        return Ok(new ChatListDto(chats.Select(ToChatSummary).ToList(), allIds));
    }

    // CHAT_SHARE_LINK: добавлено 2026-09-24 — ссылка может вести на чат вне первых N из списка:
    /// <summary>One chat of the signed-in user as the list shows it (404 for anything else).</summary>
    [HttpGet("chats/{chatId:guid}")]
    public async Task<ActionResult<ChatSummaryDto>> GetChat(Guid chatId, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        var chat = await _conversation.GetChatAsync(userId, chatId, ct);
        return chat is null ? NotFound(new { error = "Chat not found." }) : Ok(ToChatSummary(chat));
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

    // CHAT_DELETE: добавлено 2026-09-23
    /// <summary>
    /// Permanently deletes a chat: its stored messages and, when the caller really owned it, its
    /// workspace directory. Without this the chat only vanished from the browser that deleted it and
    /// came straight back on the next sync — the row was still in the database.
    /// <para>
    /// CHAT_OWNERSHIP: изменено 2026-09-24. Ревью C1/M7/M8/M9: право решает таблица владельцев (а не
    /// «удалилась ли хоть одна строка истории» — её можно было создать под чужим chatId); идущий ход
    /// останавливается, а чат помечается удалённым, чтобы запись хода в finally его не воскресила;
    /// вместе с историей уходят строки ходов, карточки подтверждения и факты памяти из этого чата;
    /// инкогнито-чат (только в памяти) тоже удаляется, вместе с файлами на диске.
    /// </para>
    /// </summary>
    [HttpDelete("chats/{chatId:guid}")]
    public async Task<IActionResult> DeleteChat(Guid chatId, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        // INCOGNITO_CHAT: an incognito chat lives in memory only — drop it (and its files) without
        // writing anything about it to the database, not even a tombstone.
        if (_incognito.GetOwner(chatId) == userId && !await _chatAccess.IsRecordedAsync(chatId, ct))
        {
            _cancellations.CancelChat(chatId);
            _incognito.Clear(chatId, userId);
            await _workspaceService.CleanupWorkspaceAsync(chatId, ct);
            return NoContent();
        }

        var access = await _chatAccess.GetAccessAsync(userId, chatId, ct);

        if (access != ChatAccessKind.Owner)
        {
            // Foreign, already deleted, or never existed: the same answer, so ids cannot be probed.
            return NotFound(new { error = "Chat not found." });
        }

        // M8: stop a running turn first, and tombstone the chat before its finally block persists.
        var cancelled = _cancellations.CancelChat(chatId);
        await _chatAccess.MarkDeletedAsync(userId, chatId, ct);

        var deletedRows = await _conversation.DeleteChatAsync(userId, chatId, ct);
        var wasIncognito = _incognito.Clear(chatId, userId);
        await _workspaceService.CleanupWorkspaceAsync(chatId, ct);

        _logger.LogInformation(
            "Chat {ChatId} deleted by user {UserId}: {Rows} history row(s) removed, {Cancelled} running turn(s) stopped, incognito={Incognito}, workspace cleaned.",
            chatId, userId, deletedRows, cancelled, wasIncognito);
        return NoContent();
    }

    // CHAT_RENAME: добавлено 2026-09-23
    /// <summary>
    /// Renames one chat. The name is stored with the chat's history rows, under the caller's user id,
    /// so it travels to every device with the rest of the chat list.
    /// </summary>
    [HttpPatch("chats/{chatId:guid}")]
    public async Task<IActionResult> RenameChat(Guid chatId, [FromBody] ChatRenameDto? dto, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        var title = dto?.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            return BadRequest(new { error = "A non-empty 'title' is required." });
        }

        // The column is varchar(200); reject instead of silently truncating what the user typed.
        if (title.Length > MaxChatTitleLength)
        {
            return BadRequest(new { error = $"The title must be at most {MaxChatTitleLength} characters." });
        }

        var updatedRows = await _conversation.RenameChatAsync(userId, chatId, title, ct);
        if (updatedRows == 0)
        {
            // Same ownership gate as delete: no rows of this user means no such chat for them.
            return NotFound(new { error = "Chat not found." });
        }

        _logger.LogInformation(
            "Chat {ChatId} renamed by user {UserId} ({Rows} row(s) updated).", chatId, userId, updatedRows);
        return Ok(new { id = chatId, title });
    }

    // CHAT_PIN: добавлено 2026-09-23
    /// <summary>
    /// Pins or unpins one chat. Like a rename, the flag is stored with the chat's history rows under
    /// the caller's user id, so the sidebar order follows the user to every device. A separate route
    /// (rather than a field on the rename body) keeps "rename" and "pin" from guessing at each other's
    /// missing fields.
    /// </summary>
    [HttpPatch("chats/{chatId:guid}/pin")]
    public async Task<IActionResult> PinChat(Guid chatId, [FromBody] ChatPinDto? dto, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        if (dto is null)
            return BadRequest(new { error = "A boolean 'isPinned' is required." });

        var updatedRows = await _conversation.SetPinnedAsync(userId, chatId, dto.IsPinned, ct);
        if (updatedRows == 0)
        {
            // Same ownership gate as rename/delete: no rows of this user means no such chat for them.
            return NotFound(new { error = "Chat not found." });
        }

        _logger.LogInformation(
            "Chat {ChatId} {PinState} by user {UserId} ({Rows} row(s) updated).",
            chatId, dto.IsPinned ? "pinned" : "unpinned", userId, updatedRows);
        return Ok(new { id = chatId, isPinned = dto.IsPinned });
    }

    /// <summary>
    /// Chat list projection. The chat <em>kind</em> is inferred from the workspace: a chat that has
    /// one on disk is a conexy-coder (agent) chat, because the workspace is keyed by the chat id and
    /// only the agent path creates it. Without this a synced agent chat would land in the wrong
    /// sidebar tab. (Students mode is not recoverable here — it is not stored anywhere.)
    /// </summary>
    private ChatSummaryDto ToChatSummary(ChatListSummary chat)
    {
        return new ChatSummaryDto(
            chat.ChatId,
            // CHAT_RENAME: имя, заданное пользователем, важнее выведенного из первого сообщения.
            !string.IsNullOrWhiteSpace(chat.Title) ? chat.Title : ForPreview(chat.FirstUserMessage),
            ResolveChatKind(chat.ChatId, chat.Kind),
            chat.LastActivityAt,
            chat.MessageCount,
            ForPreview(chat.LastAssistantMessage),
            // CHAT_PIN: закрепление приезжает вместе с чатом, чтобы порядок сайдбара был общим.
            chat.IsPinned,
            // CHAT_OWNERSHIP: модель последнего хода — Cowork открывается как Cowork (ревью M18).
            chat.Model);
    }

    /// <summary>
    /// The tab a chat belongs to: the stored mode when one was persisted, otherwise a guess from the
    /// filesystem (agent chats are the only ones with a workspace). The guess only matters for rows
    /// written before the mode column existed.
    /// </summary>
    private string ResolveChatKind(Guid chatId, string? storedKind)
    {
        if (!string.IsNullOrEmpty(storedKind)) return storedKind;
        return _workspaceService.GetTaskWorkspacePathIfExists(chatId) is not null ? "projects" : "chat";
    }

    /// <summary>Upper bound that matches the column length in the database.</summary>
    private const int MaxChatTitleLength = 200;

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
    public async Task<ActionResult<WorkspaceListing>> GetWorkspaceFiles(string sessionId, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        if (!TryParseWorkspaceId(sessionId, out var chatId, out var error))
            return error;

        // CHAT_OWNERSHIP: ревью C1.
        if (!await _chatAccess.CanReadAsync(userId, chatId, ct))
            return Forbidden();

        // Never 404 for a workspace that has not been created yet: an empty listing keeps the Agent
        // IDE rendering cleanly. Reading no longer creates the directory — only writes do.
        var root = _workspaceService.GetTaskWorkspacePathIfExists(chatId);
        if (root is null)
            return Ok(new WorkspaceListing(Array.Empty<string>(), Array.Empty<WorkspaceFileEntry>()));

        // WORKSPACE_JAIL: ревью C2 — симлинки не показываются и не обходятся (иначе `ln -s / host`
        // отдавал листинг хоста, а `ln -s . loop` вешал запрос бесконечной рекурсией).
        root = WorkspaceJail.GetRealPath(root);
        var files = Directory.EnumerateFiles(root, "*", WorkspaceJail.NoLinks(recursive: true))
            .Select(f => NormalizeSeparators(Path.GetRelativePath(root, f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Ok(new WorkspaceListing(files, BuildTree(root, root)));
    }

    /// <summary>Returns the raw content of a single workspace file.</summary>
    [HttpGet("workspace/{sessionId}/file")]
    public async Task<ActionResult<WorkspaceFileContent>> GetWorkspaceFile(string sessionId, [FromQuery] string path, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        if (!TryParseWorkspaceId(sessionId, out var chatId, out var error))
            return error;

        if (string.IsNullOrWhiteSpace(path))
            return BadRequest(new { error = "Query parameter 'path' is required." });

        if (!await _chatAccess.CanReadAsync(userId, chatId, ct))
            return Forbidden();

        if (_workspaceService.GetTaskWorkspacePathIfExists(chatId) is null)
            return NotFound(new { error = $"File '{path}' not found." });

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
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        if (!TryParseWorkspaceId(sessionId, out var chatId, out var error))
            return error;

        if (string.IsNullOrWhiteSpace(path))
            return BadRequest(new { error = "Query parameter 'path' is required." });

        if (!await _chatAccess.CanReadAsync(userId, chatId, ct))
            return Forbidden();

        if (_workspaceService.GetTaskWorkspacePathIfExists(chatId) is null)
            return NotFound(new { error = $"File '{path}' not found." });

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
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        if (!TryParseWorkspaceId(sessionId, out var chatId, out var error))
            return error;

        if (dto == null || string.IsNullOrWhiteSpace(dto.Path) || dto.Path.Contains(".."))
            return BadRequest(new { error = "Invalid path." });

        var denied = await EnsureWritableAsync(userId, chatId, ct);
        if (denied is not null)
            return denied;

        var res = await _workspaceService.WriteFileAsync(chatId, dto.Path, dto.Content ?? string.Empty, ct);
        if (!res.Success)
            return BadRequest(new { error = res.Error });

        return Ok(new { success = true });
    }

    /// <summary>Deletes a single workspace file from the File Explorer.</summary>
    [HttpDelete("workspace/{sessionId}/file")]
    public async Task<IActionResult> DeleteWorkspaceFile(string sessionId, [FromQuery] string path, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        if (!TryParseWorkspaceId(sessionId, out var chatId, out var error))
            return error;

        if (string.IsNullOrWhiteSpace(path) || path.Contains(".."))
            return BadRequest(new { error = "Invalid path." });

        if (await _chatAccess.GetAccessAsync(userId, chatId, ct) != ChatAccessKind.Owner)
            return Forbidden();

        // WORKSPACE_JAIL: a delete works on a path, so it never races a running sandbox command.
        using var slot = _sandboxActivity.TryAcquire(chatId);
        if (slot is null)
            return Conflict(new { error = "WORKSPACE_BUSY" });

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
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        if (!TryParseWorkspaceId(sessionId, out var chatId, out var error))
            return error;

        if (!await _chatAccess.CanReadAsync(userId, chatId, ct))
            return Forbidden();

        var root = _workspaceService.GetTaskWorkspacePathIfExists(chatId);
        if (root is null || !Directory.EnumerateFileSystemEntries(root).Any())
            return NotFound(new { error = "Workspace is empty." });

        // WORKSPACE_JAIL: ревью C2. ZipFile.CreateFromDirectory читал файлы по симлинкам — архив
        // воркспейса со ссылкой на /proc/self/environ приносил секреты бэкенда. Архив собирается
        // вручную: симлинки пропускаются, каждый файл читается через проверенный дескриптор.
        root = WorkspaceJail.GetRealPath(root);
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", WorkspaceJail.NoLinks(recursive: true)))
            {
                var relative = NormalizeSeparators(Path.GetRelativePath(root, file));
                try
                {
                    await using var source = WorkspaceJail.OpenRead(root, relative);
                    var entry = archive.CreateEntry(relative, CompressionLevel.Fastest);
                    await using var target = entry.Open();
                    await source.CopyToAsync(target, ct);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    _logger.LogWarning("Skipped '{Path}' while zipping workspace {ChatId}: {Reason}", relative, chatId, ex.Message);
                }
            }
        }

        return new FileContentResult(buffer.ToArray(), "application/zip")
        {
            FileDownloadName = $"workspace-{chatId:N}.zip"
        };
    }

    // WORKSPACE_JAIL: ревью M11 — лимиты распаковки: без них 50-мегабайтный архив разворачивался в
    // десятки гигабайт и забивал диск бэкенда.
    private const long MaxZipUncompressedBytes = 500L * 1024 * 1024;
    private const int MaxZipEntries = 20_000;
    private const long MaxZipEntryRatio = 200;

    /// <summary>Extracts an uploaded ZIP archive into the session workspace.</summary>
    [HttpPost("workspace/{sessionId}/upload-zip")]
    [RequestSizeLimit(55_000_000)] // ~50MB file + multipart overhead
    public async Task<IActionResult> UploadWorkspaceZip(string sessionId, [FromForm] IFormFile file, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        if (!TryParseWorkspaceId(sessionId, out var chatId, out var error))
            return error;

        if (file is null || file.Length == 0)
            return BadRequest(new { error = "No file uploaded." });

        const long MaxBytes = 50L * 1024 * 1024;
        if (file.Length > MaxBytes)
            return BadRequest(new { error = "File exceeds the 50MB limit." });

        var denied = await EnsureWritableAsync(userId, chatId, ct);
        if (denied is not null)
            return denied;

        // Extraction works on paths: never while a sandbox command runs in this workspace.
        using var slot = _sandboxActivity.TryAcquire(chatId);
        if (slot is null)
            return Conflict(new { error = "WORKSPACE_BUSY" });

        var root = _workspaceService.GetTaskWorkspacePath(chatId);

        var tempPath = Path.Combine(Path.GetTempPath(), $"conexy-upload-{Guid.NewGuid():N}.zip");
        try
        {
            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            {
                await file.CopyToAsync(fs, ct);
            }

            using var archive = ZipFile.OpenRead(tempPath);
            if (archive.Entries.Count > MaxZipEntries)
                return BadRequest(new { error = $"ZIP contains more than {MaxZipEntries} entries." });

            long declaredTotal = 0;
            foreach (var entry in archive.Entries)
            {
                declaredTotal += entry.Length;
                if (entry.Length > 1024 * 1024 && entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > MaxZipEntryRatio)
                    return BadRequest(new { error = "ZIP contains an entry with a suspicious compression ratio." });
            }
            if (declaredTotal > MaxZipUncompressedBytes)
                return BadRequest(new { error = "ZIP expands beyond the 500MB limit." });

            long written = 0;
            foreach (var entry in archive.Entries)
            {
                // Skip directory entries (they have no file name).
                if (string.IsNullOrEmpty(entry.Name))
                    continue;

                var relative = entry.FullName.Replace('\\', '/');
                if (relative.StartsWith('/') || relative.Split('/').Any(part => part == ".."))
                    return BadRequest(new { error = "ZIP contains invalid paths." });

                // WORKSPACE_JAIL: the destination is resolved with symlinks followed, so a link left
                // in the workspace cannot redirect the extraction outside it.
                FileStream target;
                try
                {
                    target = WorkspaceJail.OpenWrite(root, relative);
                }
                catch (UnauthorizedAccessException)
                {
                    return BadRequest(new { error = "ZIP contains invalid paths." });
                }

                await using (target)
                await using (var source = entry.Open())
                {
                    // Count real bytes: the sizes in the central directory can lie.
                    var bufferBytes = new byte[81920];
                    int read;
                    while ((read = await source.ReadAsync(bufferBytes, ct)) > 0)
                    {
                        written += read;
                        if (written > MaxZipUncompressedBytes)
                            return BadRequest(new { error = "ZIP expands beyond the 500MB limit." });
                        await target.WriteAsync(bufferBytes.AsMemory(0, read), ct);
                    }
                }
            }
        }
        catch (InvalidDataException)
        {
            return BadRequest(new { error = "The file is not a valid ZIP archive." });
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

    /// <summary>403 with a stable code, for chats of another user.</summary>
    private ObjectResult Forbidden() =>
        StatusCode(StatusCodes.Status403Forbidden, new { error = "CHAT_FORBIDDEN" });

    /// <summary>Owner check for write paths; claims a brand-new chat for the caller.</summary>
    private async Task<IActionResult?> EnsureWritableAsync(Guid userId, Guid chatId, CancellationToken ct)
    {
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

        foreach (var dir in Directory.EnumerateDirectories(directory, "*", WorkspaceJail.NoLinks(recursive: false)).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var rel = NormalizeSeparators(Path.GetRelativePath(root, dir));
            entries.Add(new WorkspaceFileEntry(Path.GetFileName(dir), rel, true, 0, BuildTree(root, dir)));
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", WorkspaceJail.NoLinks(recursive: false)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
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
