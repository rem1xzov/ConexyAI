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
    private readonly Action<string> _log;

    public ToolRunner(string workspaceRoot, ICommandApprover approver, Action<string> log)
    {
        _guard = new PathGuard(workspaceRoot);
        _files = new FileTools(_guard);
        _search = new SearchTools(_guard);
        _editor = new EditorTool(_guard, _files);
        _patch = new PatchApplier(_guard);
        _shell = new ShellTool(_guard, approver);
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
                "bash" or "terminal_exec" => await _shell.RunAsync(args, tool, ct),
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
    }

    private static string Describe(string tool, ToolArgs args)
    {
        var target = args.Str("path") ?? args.Str("command") ?? args.Str("pattern") ?? string.Empty;
        if (target.Length > 80)
            target = target[..80] + "…";
        return $"{tool} {target}".TrimEnd();
    }
}
