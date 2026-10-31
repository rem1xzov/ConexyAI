namespace ConexyAI.Desktop.Tools;

/// <summary>Диспетчер инструментов, которые сервер просит выполнить на этом ПК.</summary>
public sealed class ToolRunner
{
    private readonly PathGuard _guard;
    private readonly FileTools _files;
    private readonly SearchTools _search;
    private readonly EditorTool _editor;
    private readonly PatchApplier _patch;
    private readonly ShellTool _shell;
    private readonly PcTools _pc;
    private readonly WindowTools _windows;
    private readonly Action<string> _log;
    private readonly Mcp.LocalTools? _local;
    private readonly ICommandApprover _approver;
    // LOOP_GUARD: счётчик чтений без правок — заслон работает даже со старой версией сервера.
    private readonly ReadLoopGuard _readGuard = new();
    // BUILD_FILES_GUARD: агент менял файлы сборки/тестов — run_tests/get_diagnostics только с подтверждения.
    private volatile bool _buildFilesTouched;
    private string _buildFingerprint;

    /// <summary>Контрольные точки файлов за ходы агента (/rewind).</summary>
    public Checkpoints Checkpoints { get; } = new();

    // WEB_DEVICE: добавлено 2026-10-18 — исходники файлов по веб-задачам для turn_diff.
    public TaskChanges TaskChanges { get; } = new();

    /// <summary>
    /// Режим плана (/plan): агент только изучает проект. Запрет проверяет сам клиент — на послушание
    /// модели не полагаемся.
    /// </summary>
    public bool PlanMode { get; set; }

    public ToolRunner(string workspaceRoot, ICommandApprover approver, Action<string> log,
            Func<CancellationToken, Task<string?>>? gitHubToken = null, Mcp.LocalTools? local = null)
    {
        _local = local;
        _approver = approver;
        _guard = new PathGuard(workspaceRoot);
        // WEB_DEVICE: добавлено 2026-10-18 — правки веб-задач не попадают в /rewind локального сеанса.
        _guard.BeforeWrite = path =>
        {
            if (ToolCallContext.Current is not { Remote: true })
                Checkpoints.BeforeWrite(path);
            TaskChanges.BeforeWrite(path);
            if (ProjectCommands.IsBuildDefinition(path, _guard.Root))
                _buildFilesTouched = true;
        };
        _buildFingerprint = ProjectCommands.Fingerprint(_guard.Root);
        _files = new FileTools(_guard);
        _search = new SearchTools(_guard);
        _editor = new EditorTool(_guard, _files);
        _patch = new PatchApplier(_guard);
        _shell = new ShellTool(_guard, approver, gitHubToken);
        _pc = new PcTools(_guard, approver, _shell);
        _windows = new WindowTools(approver, _guard.Root);
        _log = log;
    }

    public string Root => _guard.Root;

    /// <summary>Новый ход/задача: счётчики чтения начинаются с нуля.</summary>
    public void ResetReadGuard() => _readGuard.Reset();

    public async Task<ToolResult> RunAsync(string tool, string argumentsJson, CancellationToken ct)
    {
        try
        {
            var args = ToolArgs.Parse(argumentsJson);
            // Служебный запрос сервера в начале каждого хода — без строки в терминале.
            if (tool == "project_rules")
                return ProjectRules(args);
            // WEB_DEVICE: добавлено 2026-10-18 — итоговый diff задачи, тоже без строки в терминале.
            if (tool == "turn_diff")
                return ToolCallContext.Current is { TaskId: var taskId } && taskId != Guid.Empty
                    ? ToolResult.Ok(TaskChanges.BuildJson(taskId, _guard.Relative))
                    : ToolResult.Fail("turn_diff: неизвестна задача.");
            if (tool == "ask_user")
                return await AskUserAsync(args, ct);
            if (PlanMode && PlanBlockReason(tool, args, _guard.Root) is { } blocked)
                return ToolResult.Fail(blocked);
            // EDIT_AFTER_READ + BASH_FILE_OPS: проверки до выполнения — правка непрочитанного файла
            // и файловые операции через оболочку отклоняются с точным следующим шагом.
            if (_readGuard.Refuse(tool, args, FileExists) is { } readRefusal)
                return ToolResult.Fail(readRefusal);

            // REMOTE_APPROVAL_POLICY: имя инструмента нужно approver'у демона (install_software и т.п. с сайта нельзя).
            if (ToolCallContext.Current is { } call)
                ToolCallContext.Current = call with { Tool = tool };

            // DAEMON_LOG_MASK: в журнал демона не пишем токены из команд и URL.
            _log(SecretMask.Mask(Describe(tool, args)));

            // LOCAL_MCP: Google и локальные MCP-серверы.
            if (tool.StartsWith("local__", StringComparison.Ordinal))
                return _local is null
                    ? ToolResult.Fail("Локальные инструменты не подключены.")
                    : await _local.CallAsync(tool, argumentsJson, _guard, ct);

            var result = tool switch
            {
                "file_read" => _files.Read(args),
                // Служебные: сервер собирает/разбирает .docx/.xlsx/.pptx/.pdf, а байты живут в папке проекта.
                "file_write_bytes" => WriteBytes(args),
                "file_read_bytes" => ReadBytes(args),
                "file_write" => _files.Write(args),
                "file_patch" => _files.Patch(args),
                "workspace_list_files" => _files.List(args),
                "grep" => _search.Grep(args),
                "glob" => _search.Glob(args),
                "str_replace_editor" => _editor.Run(args),
                "apply_patch" => _patch.Apply(args.Required("patch", "apply_patch"), args.Str("path")),
                "bash" or "terminal_exec" or "cmd_exec" => await _shell.RunAsync(args, tool, ct),
                "bg_start" => await _pc.BgStartAsync(args, ct),
                "bg_logs" => _pc.BgLogs(args),
                "bg_stop" => _pc.BgStop(args),
                "http_request" => await _pc.HttpAsync(args, ct),
                "page_screenshot" => await _pc.ScreenshotAsync(args, ct),
                "window_list" => _windows.List(args),
                "screen_capture" => await _windows.CaptureAsync(args, ct),
                "window_keys" => await _windows.KeysAsync(args, ct),
                "pc_list_dir" => await _pc.ListDirAsync(args, ct),
                "pc_read_file" => await _pc.ReadFileAsync(args, ct),
                "pc_copy_to_project" => await _pc.CopyToProjectAsync(args, ct),
                "download_file" => await _pc.DownloadAsync(args, ct),
                "install_software" => await _pc.InstallAsync(args, ct),
                "run_tests" => await RunProjectAsync(args, test: true, ct),
                "get_diagnostics" => await RunProjectAsync(args, test: false, ct),
                _ => ToolResult.Fail($"Инструмент '{tool}' не поддерживается десктопным клиентом."),
            };
            // LOOP_GUARD: считаем только реально выполненные вызовы.
            if (!result.IsError)
                _readGuard.Record(tool, args);
            return result;
        }
        catch (ToolException ex)
        {
            return ToolResult.Fail(ex.Message);
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Fail("Выполнение отменено.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Fail("Ошибка файловой системы: " + ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return ToolResult.Fail("Сетевая ошибка: " + ex.Message);
        }
    }

    // Имена файлов правил — фиксированные: сервер не може этим тихим вызовом читать произвольные файлы.
    private static readonly HashSet<string> RuleFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "CONEXY.md", "CLAUDE.md", "AGENTS.md", ".conexy/rules.md", ".conexy/hooks.json",
    };

    private async Task<ToolResult> AskUserAsync(ToolArgs args, CancellationToken ct)
    {
        var question = args.Required("question", "ask_user");
        var options = (args.StrArray("options") ?? []).Where(o => !string.IsNullOrWhiteSpace(o)).Take(6).ToArray();
        var answer = await _approver.AskUserAsync(question, options, ct);
        return answer is null
            ? ToolResult.Fail("Пользователь не ответил. Прими разумное решение сам и явно напиши о нём в итоге.")
            : ToolResult.Ok("Ответ пользователя: " + answer);
    }

    private static readonly HashSet<string> PlanWriteTools = new(StringComparer.Ordinal)
    {
        "file_write", "file_patch", "apply_patch", "file_write_bytes", "bg_start", "install_software",
        "download_file", "pc_copy_to_project", "window_keys",
    };

    /// <summary>Почему инструмент запрещён в режиме плана (null — разрешён: чтение, поиск, просмотр).</summary>
    private static string? PlanBlockReason(string tool, ToolArgs args, string root)
    {
        const string why = "РЕЖИМ ПЛАНА: изменения запрещены. Ничего не меняй — изучи проект и опиши этот шаг в плане.";
        if (PlanWriteTools.Contains(tool))
            return why;
        if (tool == "str_replace_editor" && args.Str("command") != "view")
            return why;
        if (tool is "bash" or "terminal_exec" or "cmd_exec" && !CommandPolicy.IsReadOnly(args.Str("command") ?? string.Empty, root))
            return why + " В плане разрешены только команды чтения (ls, cat, git status/log/diff).";
        if (tool.StartsWith("local__", StringComparison.Ordinal)
            && !(tool.StartsWith("local__google__", StringComparison.Ordinal) || tool.StartsWith("local__mail__", StringComparison.Ordinal))
            || tool.EndsWith("_send", StringComparison.Ordinal) || tool.EndsWith("__send", StringComparison.Ordinal)
            || tool.EndsWith("attachment", StringComparison.Ordinal) || tool.EndsWith("drive_download", StringComparison.Ordinal))
            return why;
        return null;
    }

    private Task<ToolResult> RunProjectAsync(ToolArgs args, bool test, CancellationToken ct)
    {
        var touched = _buildFilesTouched || ProjectCommands.Fingerprint(_guard.Root) != _buildFingerprint;
        return ProjectCommands.RunAsync(_guard.Root, _shell, args, test, ct, requireApproval: touched, onApproved: () =>
        {
            // Пользователь видел текущее состояние — следующая правка файлов сборки снова потребует вопроса.
            _buildFilesTouched = false;
            _buildFingerprint = ProjectCommands.Fingerprint(_guard.Root);
        });
    }

    /// <summary>Есть ли файл уже сейчас: новый файл можно создать без предварительного чтения.</summary>
    private bool FileExists(string path)
    {
        try
        {
            return File.Exists(_guard.Resolve(path));
        }
        catch (Exception ex) when (ex is ToolException or IOException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private ToolResult ProjectRules(ToolArgs args)
    {
        var result = new List<object>();
        foreach (var name in args.StrArray("files") ?? [])
        {
            if (!RuleFiles.Contains(name))
                continue;
            try
            {
                var path = _guard.Resolve(name);
                if (File.Exists(path) && new FileInfo(path).Length <= 200_000)
                    result.Add(new { file = name, content = File.ReadAllText(path) });
            }
            catch (Exception ex) when (ex is ToolException or IOException or UnauthorizedAccessException) { }
        }
        // Без \uXXXX: кириллица в правилах иначе раздувает ответ в шесть раз.
        return ToolResult.Ok(System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
    }

    private ToolResult WriteBytes(ToolArgs args)
    {
        var path = _guard.ResolveForWrite(args.Required("path", "file_write_bytes"));
        byte[] bytes;
        try { bytes = Convert.FromBase64String(args.Required("content_base64", "file_write_bytes")); }
        catch (FormatException) { return ToolResult.Fail("Неверные данные файла."); }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return ToolResult.Ok($"Сохранено: {_guard.Relative(path)} ({bytes.Length / 1024 + 1} КБ)");
    }

    private ToolResult ReadBytes(ToolArgs args)
    {
        var path = _guard.Resolve(args.Required("path", "file_read_bytes"));
        if (!File.Exists(path))
            return ToolResult.Fail($"Файл не найден: {_guard.Relative(path)}");
        // Сообщение хаба ограничено 16 МБ, base64 раздувает на треть.
        if (new FileInfo(path).Length > 10L * 1024 * 1024)
            return ToolResult.Fail("Файл больше 10 МБ — слишком большой для чтения.");
        return ToolResult.Ok("FILE_BASE64:" + Convert.ToBase64String(File.ReadAllBytes(path)));
    }

    private static string Describe(string tool, ToolArgs args)
    {
        if (tool.StartsWith("local__", StringComparison.Ordinal))
        {
            var parts = tool.Split("__", 3);
            var what = args.Str("query") ?? args.Str("id") ?? args.Str("to") ?? string.Empty;
            return $"{(parts.Length == 3 ? parts[1] + "." + parts[2] : tool)} {what}".TrimEnd();
        }
        var target = args.Str("path") ?? args.Str("command") ?? args.Str("window") ?? args.Str("url") ?? args.Str("source")
                     ?? args.Str("package") ?? args.Str("id") ?? args.Str("pattern") ?? string.Empty;
        if (target.Length > 80)
            target = target[..80] + "…";
        return $"{tool} {target}".TrimEnd();
    }
}
