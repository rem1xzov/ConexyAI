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
    private readonly Action<string> _log;

    public ToolRunner(string workspaceRoot, ICommandApprover approver, Action<string> log,
            Func<CancellationToken, Task<string?>>? gitHubToken = null)
    {
        _guard = new PathGuard(workspaceRoot);
        _files = new FileTools(_guard);
        _search = new SearchTools(_guard);
        _editor = new EditorTool(_guard, _files);
        _patch = new PatchApplier(_guard);
        _shell = new ShellTool(_guard, approver, gitHubToken);
        _pc = new PcTools(_guard, approver, _shell);
        _log = log;
    }

    public string Root => _guard.Root;

    public async Task<ToolResult> RunAsync(string tool, string argumentsJson, CancellationToken ct)
    {
        try
        {
            var args = ToolArgs.Parse(argumentsJson);
            _log(Describe(tool, args));

            return tool switch
            {
                "file_read" => _files.Read(args),
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
                "pc_list_dir" => await _pc.ListDirAsync(args, ct),
                "pc_read_file" => await _pc.ReadFileAsync(args, ct),
                "pc_copy_to_project" => await _pc.CopyToProjectAsync(args, ct),
                "download_file" => await _pc.DownloadAsync(args, ct),
                "install_software" => await _pc.InstallAsync(args, ct),
                "run_tests" => await ProjectCommands.RunAsync(_guard.Root, _shell, args, test: true, ct),
                "get_diagnostics" => await ProjectCommands.RunAsync(_guard.Root, _shell, args, test: false, ct),
                _ => ToolResult.Fail($"Инструмент '{tool}' не поддерживается десктопным клиентом."),
            };
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

    private static string Describe(string tool, ToolArgs args)
    {
        var target = args.Str("path") ?? args.Str("command") ?? args.Str("url") ?? args.Str("source")
                     ?? args.Str("package") ?? args.Str("id") ?? args.Str("pattern") ?? string.Empty;
        if (target.Length > 80)
            target = target[..80] + "…";
        return $"{tool} {target}".TrimEnd();
    }
}
