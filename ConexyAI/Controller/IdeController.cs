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
    // PROBLEMS_PANEL: добавлено 2026-10-04 — анализ ошибок/предупреждений для панели Problems.
    private readonly IDiagnosticsService _diagnostics;
    private readonly IConexyBashService _bash;
    // LSP_LITE: добавлено 2026-10-05 — Outline, go-to-definition, hover и подсказки по символам IDE.
    private readonly ISymbolService _symbols;
    // DEBUG_TRACE: добавлено 2026-10-05 — точки останова и трассировка выполнения Python.
    private readonly IDebugService _debug;
    private readonly ILogger<IdeController> _logger;

    public IdeController(
        IIdeFileService fileService,
        IChatAccessService chatAccess,
        IConexyWorkspaceService workspace,
        IDiagnosticsService diagnostics,
        IConexyBashService bash,
        ISymbolService symbols,
        IDebugService debug,
        ILogger<IdeController> logger)
    {
        _fileService = fileService;
        _chatAccess = chatAccess;
        _workspace = workspace;
        _diagnostics = diagnostics;
        _bash = bash;
        _symbols = symbols;
        _debug = debug;
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

    // IDE_GIT: добавлено 2026-10-04 — Source Control (локальный git в рабочей области).
    /// <summary>Working-tree status of the workspace repository.</summary>
    [HttpGet("{sessionId:guid}/git/status")]
    public async Task<ActionResult<GitStatusResult>> GetGitStatus(
        Guid sessionId, [FromQuery] string? repoFolder = null, CancellationToken ct = default)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });
        if (!await _chatAccess.CanReadAsync(userId, sessionId, ct))
            return Forbidden();

        try { return Ok(await _workspace.GitStatusAsync(sessionId, repoFolder, ct)); }
        catch (Exception ex) { return MapError(ex); }
    }

    /// <summary>Recent commits of the current branch.</summary>
    [HttpGet("{sessionId:guid}/git/log")]
    public async Task<ActionResult<GitLogResult>> GetGitLog(
        Guid sessionId, [FromQuery] int limit = 30, [FromQuery] string? repoFolder = null, CancellationToken ct = default)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });
        if (!await _chatAccess.CanReadAsync(userId, sessionId, ct))
            return Forbidden();

        try { return Ok(await _workspace.GitLogAsync(sessionId, limit, repoFolder, ct)); }
        catch (Exception ex) { return MapError(ex); }
    }

    /// <summary>Local branches of the workspace repository.</summary>
    [HttpGet("{sessionId:guid}/git/branches")]
    public async Task<ActionResult<GitBranchesResult>> GetGitBranches(
        Guid sessionId, [FromQuery] string? repoFolder = null, CancellationToken ct = default)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });
        if (!await _chatAccess.CanReadAsync(userId, sessionId, ct))
            return Forbidden();

        try { return Ok(await _workspace.GitBranchesAsync(sessionId, repoFolder, ct)); }
        catch (Exception ex) { return MapError(ex); }
    }

    // IDE_DIFF: добавлено 2026-10-05 — diff-редактор «до/после» для одного файла.
    /// <summary>Content of a file at a revision (default HEAD) versus its working-tree content.</summary>
    [HttpGet("{sessionId:guid}/git/file")]
    public async Task<ActionResult<GitFileDiffResult>> GetGitFileDiff(
        Guid sessionId,
        [FromQuery] string path,
        [FromQuery] string? rev = null,
        [FromQuery] string? repoFolder = null,
        CancellationToken ct = default)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });
        if (!await _chatAccess.CanReadAsync(userId, sessionId, ct))
            return Forbidden();
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest(new { error = "Query parameter 'path' is required." });

        try { return Ok(await _workspace.GitFileDiffAsync(sessionId, path, rev, repoFolder, ct)); }
        catch (Exception ex) { return MapError(ex); }
    }

    /// <summary>Stages or unstages paths (empty = all).</summary>
    [HttpPost("{sessionId:guid}/git/stage")]
    public async Task<IActionResult> Stage(
        Guid sessionId, [FromBody] GitStageRequest? request, CancellationToken ct = default)
    {
        var denied = await AuthorizeWriteAsync(sessionId, ct);
        if (denied is not null)
            return denied;

        try
        {
            return Ok(await _workspace.GitStageAsync(sessionId, request?.Paths ?? Array.Empty<string>(), !(request?.Staged ?? true), request?.RepoFolder, ct));
        }
        catch (Exception ex) { return MapError(ex); }
    }

    /// <summary>Commits the staged changes locally (no push).</summary>
    [HttpPost("{sessionId:guid}/git/commit")]
    public async Task<IActionResult> Commit(
        Guid sessionId, [FromBody] GitCommitRequest? request, CancellationToken ct = default)
    {
        var denied = await AuthorizeWriteAsync(sessionId, ct);
        if (denied is not null)
            return denied;

        if (request is null || string.IsNullOrWhiteSpace(request.Message))
            return BadRequest(new { error = "Commit message is required." });

        try
        {
            return Ok(await _workspace.GitCommitAsync(sessionId, request.Message, request.AuthorName, request.AuthorEmail, request.RepoFolder, ct));
        }
        catch (Exception ex) { return MapError(ex); }
    }

    /// <summary>Switches to an existing branch (owner only).</summary>
    [HttpPost("{sessionId:guid}/git/checkout")]
    public async Task<IActionResult> Checkout(
        Guid sessionId, [FromBody] GitCheckoutRequest? request, CancellationToken ct = default)
    {
        var denied = await AuthorizeOwnerAsync(sessionId, ct);
        if (denied is not null)
            return denied;

        if (request is null || string.IsNullOrWhiteSpace(request.Branch))
            return BadRequest(new { error = "Branch is required." });

        try
        {
            return Ok(await _workspace.GitSwitchBranchAsync(sessionId, request.Branch, request.RepoFolder, ct));
        }
        catch (Exception ex) { return MapError(ex); }
    }

    // PROBLEMS_PANEL: добавлено 2026-10-04 — единая панель проблем: проверка всего проекта
    // (comпилятор/линтер/типы) в песочнице, без запуска приложения.
    /// <summary>Analyzes the whole workspace and returns structured diagnostics.</summary>
    [HttpPost("{sessionId:guid}/problems/analyze")]
    public async Task<ActionResult<ProblemsResult>> AnalyzeProblems(
        Guid sessionId, [FromBody] AnalyzeProblemsRequest? request, CancellationToken ct = default)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });
        if (!await _chatAccess.CanReadAsync(userId, sessionId, ct))
            return Forbidden();

        // A brand-new chat (no workspace yet) simply has no problems to report.
        if (_workspace.GetTaskWorkspacePathIfExists(sessionId) is null)
            return Ok(new ProblemsResult(true, null, 0, 0, Array.Empty<BuildProblem>(), null));

        try
        {
            var detection = await _diagnostics.DetectCommandAsync(sessionId, request?.Path, request?.Tool, ct);
            if (detection.Command is null)
                return Ok(new ProblemsResult(false, null, 0, 0, Array.Empty<BuildProblem>(), detection.Error));

            var run = await _bash.ExecuteAsync(sessionId, new BashToolRequest
            {
                Command = detection.Command,
                TimeoutSeconds = 600,
                RawOutput = true,
                IsDangerous = false
            }, emitStartEvent: false, ct);

            if (run.ErrorType is "workspace_not_found" or "spawn_failed")
                return Ok(new ProblemsResult(false, detection.Tool.ToString(), 0, 0, Array.Empty<BuildProblem>(),
                    run.ErrorType == "workspace_not_found" ? "Workspace not found." : "The sandbox is unavailable."));

            var report = _diagnostics.Parse(detection.Tool, run.Output, run.WasTruncated);
            var problems = report.Diagnostics.Take(500).Select(d => new BuildProblem
            {
                File = d.File,
                Line = d.Line,
                Column = d.Column,
                Severity = d.Severity,
                Code = d.Code ?? string.Empty,
                Message = d.Message
            }).ToList();

            return Ok(new ProblemsResult(true, detection.Tool.ToString(), report.Errors, report.Warnings, problems, null));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Problems analysis failed for session {SessionId}", sessionId);
            return Ok(new ProblemsResult(false, null, 0, 0, Array.Empty<BuildProblem>(), "PROBLEMS_FAILED"));
        }
    }

    // LSP_LITE: добавлено 2026-10-05 — Outline, go-to-definition, hover и подсказки по символам
    // для редактора IDE. Текст берётся из живого буфера редактора (content), поэтому несохранённые
    // правки тоже видны.
    /// <summary>Returns the symbol tree (outline) of a file.</summary>
    [HttpPost("{sessionId:guid}/symbols")]
    public async Task<ActionResult<IdeSymbolsResult>> GetSymbols(
        Guid sessionId, [FromBody] IdeSymbolRequest? request, CancellationToken ct = default)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });
        if (!await _chatAccess.CanReadAsync(userId, sessionId, ct))
            return Forbidden();
        if (request is null || string.IsNullOrWhiteSpace(request.Path))
            return BadRequest(new { error = "Path is required." });

        if (request.Content is null && _workspace.GetTaskWorkspacePathIfExists(sessionId) is null)
            return Ok(new IdeSymbolsResult(true, request.Path, null, Array.Empty<IdeSymbol>(), null));

        try { return Ok(await _symbols.GetDocumentSymbolsAsync(sessionId, request.Path, request.Content, ct)); }
        catch (Exception ex) { return MapError(ex); }
    }

    /// <summary>Finds the declaration(s) of an identifier across the workspace.</summary>
    [HttpPost("{sessionId:guid}/definition")]
    public async Task<ActionResult<IdeDefinitionResult>> FindDefinition(
        Guid sessionId, [FromBody] IdeDefinitionRequest? request, CancellationToken ct = default)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });
        if (!await _chatAccess.CanReadAsync(userId, sessionId, ct))
            return Forbidden();
        if (request is null || string.IsNullOrWhiteSpace(request.Word))
            return Ok(new IdeDefinitionResult(false, request?.Word ?? string.Empty, Array.Empty<IdeLocation>(), null));

        if (request.Content is null && _workspace.GetTaskWorkspacePathIfExists(sessionId) is null)
            return Ok(new IdeDefinitionResult(false, request.Word, Array.Empty<IdeLocation>(), null));

        try { return Ok(await _symbols.FindDefinitionsAsync(sessionId, request.Path, request.Word, request.Content, ct)); }
        catch (Exception ex) { return MapError(ex); }
    }

    /// <summary>Returns a signature + doc comment for the identifier under the cursor.</summary>
    [HttpPost("{sessionId:guid}/hover")]
    public async Task<ActionResult<IdeHoverResult>> GetHover(
        Guid sessionId, [FromBody] IdeHoverRequest? request, CancellationToken ct = default)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });
        if (!await _chatAccess.CanReadAsync(userId, sessionId, ct))
            return Forbidden();
        if (request is null || string.IsNullOrWhiteSpace(request.Word))
            return Ok(new IdeHoverResult(false, request?.Word ?? string.Empty, null, null, null, null, 0, null));

        if (request.Content is null && _workspace.GetTaskWorkspacePathIfExists(sessionId) is null)
            return Ok(new IdeHoverResult(false, request.Word, null, null, null, null, 0, null));

        try { return Ok(await _symbols.GetHoverAsync(sessionId, request.Path, request.Word, request.Content, ct)); }
        catch (Exception ex) { return MapError(ex); }
    }

    /// <summary>Project-wide symbol lookup by name prefix (used for completion).</summary>
    [HttpPost("{sessionId:guid}/symbols/search")]
    public async Task<ActionResult<IdeSymbolSearchResult>> SearchSymbols(
        Guid sessionId, [FromBody] IdeSymbolSearchRequest? request, CancellationToken ct = default)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });
        if (!await _chatAccess.CanReadAsync(userId, sessionId, ct))
            return Forbidden();
        if (request is null || string.IsNullOrWhiteSpace(request.Query))
            return Ok(new IdeSymbolSearchResult(true, Array.Empty<IdeSymbolSearchItem>(), null));

        if (_workspace.GetTaskWorkspacePathIfExists(sessionId) is null)
            return Ok(new IdeSymbolSearchResult(true, Array.Empty<IdeSymbolSearchItem>(), null));

        try { return Ok(await _symbols.SearchSymbolsAsync(sessionId, request.Query, request.Limit, ct)); }
        catch (Exception ex) { return MapError(ex); }
    }

    // DEBUG_TRACE: добавлено 2026-10-05 — «уровень A» отладчика: точки останова и трассировка
    // выполнения Python (sys.settrace) в песочнице. Запуск кода — только владельцу чата.
    /// <summary>Returns the chat's saved breakpoints.</summary>
    [HttpGet("{sessionId:guid}/debug/breakpoints")]
    public async Task<ActionResult<DebugBreakpointsResult>> GetBreakpoints(
        Guid sessionId, CancellationToken ct = default)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });
        if (!await _chatAccess.CanReadAsync(userId, sessionId, ct))
            return Forbidden();

        try { return Ok(await _debug.GetBreakpointsAsync(sessionId, ct)); }
        catch (Exception ex) { return MapError(ex); }
    }

    /// <summary>Replaces the chat's saved breakpoints.</summary>
    [HttpPut("{sessionId:guid}/debug/breakpoints")]
    public async Task<IActionResult> SetBreakpoints(
        Guid sessionId, [FromBody] DebugBreakpointsRequest? request, CancellationToken ct = default)
    {
        var denied = await AuthorizeWriteAsync(sessionId, ct);
        if (denied is not null)
            return denied;

        try { return Ok(await _debug.SetBreakpointsAsync(sessionId, request?.Breakpoints ?? Array.Empty<DebugBreakpoint>(), ct)); }
        catch (Exception ex) { return MapError(ex); }
    }

    /// <summary>Runs the entry script under the trace harness and returns recorded steps.</summary>
    [HttpPost("{sessionId:guid}/debug/run")]
    public async Task<IActionResult> RunDebug(
        Guid sessionId, [FromBody] DebugRunRequest? request, CancellationToken ct = default)
    {
        var denied = await AuthorizeWriteAsync(sessionId, ct);
        if (denied is not null)
            return denied;

        if (request is null)
            return BadRequest(new { error = "Invalid request body." });

        try { return Ok(await _debug.RunAsync(sessionId, request, ct)); }
        catch (Exception ex) { return MapError(ex); }
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
