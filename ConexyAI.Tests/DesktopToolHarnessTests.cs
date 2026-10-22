using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ConexyAI.Desktop.Tools;
using DesktopToolResult = ConexyAI.Desktop.Tools.ToolResult;
using DesktopToolRunner = ConexyAI.Desktop.Tools.ToolRunner;
using ICommandApprover = ConexyAI.Desktop.Tools.ICommandApprover;

// Smoke-проверка всех инструментов десктоп-клиента через ToolRunner.RunAsync — так их вызывает сервер.
internal static class DesktopToolHarnessTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("desktop tools: files, search and editors", FilesAsync);
        TestRegistry.Add("desktop tools: shell, background processes and project commands", ShellAsync);
        TestRegistry.Add("desktop tools: pc_* tools reach outside the project", PcAsync);
        TestRegistry.Add("desktop tools: service tools and guards (paths, unknown tools, plan mode)", GuardsAsync);
        TestRegistry.Add("desktop tools: re-reading the same file is stopped until an edit", ReadLoopAsync);
        TestRegistry.Add("desktop tools: file reads are paginated and capped", ReadPagingAsync);
        TestRegistry.Add("desktop tools: a system folder is never a workspace", SystemRootAsync,
            () => OperatingSystem.IsWindows() ? null : "the system-folder guard is Windows-specific");
        TestRegistry.Add("desktop tools: http_request and download_file", NetworkAsync,
            () => NetworkAvailable() ? null : "requires network access to example.com");
        TestRegistry.Add("desktop tools: window_list, screen_capture, page_screenshot", ScreenAsync,
            () => !OperatingSystem.IsWindows() ? "requires Windows desktop"
                : Environment.GetEnvironmentVariable("CONEXY_TEST_GUI") != "1" ? "set CONEXY_TEST_GUI=1 to capture the screen" : null);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private sealed class AutoApprover : ICommandApprover
    {
        public Task<bool> ApproveAsync(string command, string workingDirectory, CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class Harness : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "conexy_tools_" + Guid.NewGuid().ToString("N"));
        public DesktopToolRunner Runner { get; }

        public Harness()
        {
            Directory.CreateDirectory(Root);
            Runner = new DesktopToolRunner(Root, new AutoApprover(), _ => { });
        }

        public async Task<string> Ok(string tool, object args, Func<string, bool>? check = null)
        {
            var r = await Run(tool, args);
            Assert(!r.IsError, $"{tool} failed: {r.Output}");
            Assert(check?.Invoke(r.Output) ?? true, $"{tool} returned unexpected output: {r.Output}");
            return r.Output;
        }

        public async Task Fail(string tool, object args)
        {
            var r = await Run(tool, args);
            Assert(r.IsError, $"{tool} should have failed, got: {r.Output}");
        }

        private Task<DesktopToolResult> Run(string tool, object args) =>
            Runner.RunAsync(tool, JsonSerializer.Serialize(args), CancellationToken.None);

        public void Dispose()
        {
            for (var i = 0; i < 15; i++)
            {
                try { if (Directory.Exists(Root)) Directory.Delete(Root, true); return; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(100); }
            }
        }
    }

    private static async Task FilesAsync()
    {
        using var h = new Harness();
        await h.Ok("file_write", new { path = "src/a.txt", content = "line1\nline2\nhello world\n" });
        await h.Ok("file_read", new { path = "src/a.txt" }, o => o.Contains("hello world"));
        await h.Ok("file_patch", new { path = "src/a.txt", search_block = "hello world", replace_block = "hello conexy" });
        await h.Ok("file_read", new { path = "src/a.txt" }, o => o.Contains("hello conexy"));
        await h.Ok("workspace_list_files", new { path = "." }, o => o.Contains("a.txt"));
        await h.Ok("grep", new { pattern = "conexy" }, o => o.Contains("a.txt"));
        await h.Ok("glob", new { pattern = "**/*.txt" }, o => o.Contains("a.txt"));

        await h.Ok("str_replace_editor", new { command = "create", path = "b.txt", file_text = "x\ny\n" });
        // EDIT_AFTER_READ: правка существующего файла требует предварительного чтения.
        await h.Ok("str_replace_editor", new { command = "view", path = "b.txt" });
        await h.Ok("str_replace_editor", new { command = "str_replace", path = "b.txt", old_str = "y", new_str = "z" });
        await h.Ok("str_replace_editor", new { command = "insert", path = "b.txt", insert_line = 0, new_str = "top" });
        await h.Ok("str_replace_editor", new { command = "view", path = "b.txt" }, o => o.Contains("top") && o.Contains("z"));
        await h.Ok("str_replace_editor", new { command = "undo", path = "b.txt" });
        await h.Ok("apply_patch", new { patch = "--- a/b.txt\n+++ b/b.txt\n@@ -1,2 +1,2 @@\n x\n-z\n+patched\n" });
        await h.Ok("file_read", new { path = "b.txt" }, o => o.Contains("patched"));

        await h.Ok("file_write_bytes", new { path = "bin.dat", content_base64 = Convert.ToBase64String(new byte[] { 1, 2, 3 }) });
        await h.Ok("file_read_bytes", new { path = "bin.dat" }, o => o == "FILE_BASE64:AQID");
    }

    private static async Task ShellAsync()
    {
        using var h = new Harness();
        await h.Ok("bash", new { command = "echo bash-ok" }, o => o.Contains("bash-ok"));
        await h.Ok("terminal_exec", new { command = "echo te-ok" }, o => o.Contains("te-ok"));
        await h.Ok("cmd_exec", new { command = "echo cmd-ok" }, o => o.Contains("cmd-ok"));
        await h.Ok("run_tests", new { command = "echo tests-ok" }, o => o.Contains("tests-ok"));
        await h.Ok("get_diagnostics", new { command = "echo diag-ok" }, o => o.Contains("diag-ok"));

        var ping = OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1" : "ping -c 30 127.0.0.1";
        var started = await h.Ok("bg_start", new { command = ping, name = "pinger", wait_seconds = 1 });
        var id = started.Split(' ', 2)[0].Replace("id=", "");
        await h.Ok("bg_logs", new { id, lines = 20 });
        await h.Ok("bg_stop", new { id });
    }

    private static async Task PcAsync()
    {
        using var h = new Harness();
        var outside = Path.Combine(Path.GetTempPath(), "conexy_outside_" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(outside, "outside");
        try
        {
            await h.Ok("pc_list_dir", new { path = Path.GetDirectoryName(outside) });
            await h.Ok("pc_read_file", new { path = outside }, o => o.Contains("outside"));
            await h.Ok("pc_copy_to_project", new { source = outside, destination = "copied.txt" });
            Assert(File.ReadAllText(Path.Combine(h.Root, "copied.txt")) == "outside", "pc_copy_to_project must copy the content");
        }
        finally { File.Delete(outside); }
    }

    // TOKEN_ECONOMY: целый файл — это десятки тысяч токенов в контексте на каждом шаге.
    private static async Task ReadPagingAsync()
    {
        using var h = new Harness();
        var lines = Enumerable.Range(1, 1_000).Select(i => $"line {i} " + new string('x', 20)).ToArray();
        File.WriteAllLines(Path.Combine(h.Root, "big.txt"), lines);

        var first = await h.Ok("file_read", new { path = "big.txt" }, o => o.Contains("line 1"));
        Assert(first.Contains("дальше: offset=400"), $"the first page must point to the next offset: {first[^200..]}");
        Assert(first.Length < 26_000, $"a read must be capped, got {first.Length} chars");

        var tail = await h.Ok("file_read", new { path = "big.txt", offset = 900, limit = 50 }, o => o.Contains("line 950"));
        Assert(tail.Contains("строки 901–950"), $"the page range is reported: {tail[^200..]}");
    }

    // LOOP_GUARD: клиент сам останавливает бесконечное чтение — даже если сервер старый и не прислал отказ.
    private static async Task ReadLoopAsync()
    {
        using var h = new Harness();
        File.WriteAllText(Path.Combine(h.Root, "a.txt"), "line\n");

        for (var i = 0; i < ConexyAI.Desktop.Tools.ReadLoopGuard.TargetLimit; i++)
            await h.Ok("file_read", new { path = "a.txt" });
        await h.Fail("file_read", new { path = "a.txt" });

        // Правка — прогресс: после неё читать снова можно.
        await h.Ok("file_write", new { path = "a.txt", content = "line\nchanged\n" });
        await h.Ok("file_read", new { path = "a.txt" }, o => o.Contains("changed"));

        // PowerShell-однострочник для чтения куска файла — тоже чтение, хотя в нём есть `;` и `$`.
        var readScript = ToolArgs.Parse("""{"command":"$p='a.txt'; $t=[System.IO.File]::ReadAllText($p,[System.Text.Encoding]::UTF8); $t.Substring(0,10)"}""");
        Assert(ReadLoopGuard.IsRead("bash", readScript), "a PowerShell read one-liner must count as a read");
        var writeScript = ToolArgs.Parse("""{"command":"$p='a.txt'; Set-Content $p 'x'"}""");
        Assert(!ReadLoopGuard.IsRead("bash", writeScript), "a script that writes is an action");

        // BASH_FILE_OPS: читать файлы через оболочку больше не нужно — отказ ровно один раз.
        var shellRead = OperatingSystem.IsWindows() ? "Get-Content a.txt" : "cat a.txt";
        await h.Fail("bash", new { command = shellRead });
        // Повтор той же команды выполняется — но счётчик повторов чтения так же ловит петлю.
        await h.Ok("bash", new { command = shellRead });
        await h.Ok("bash", new { command = shellRead });
        await h.Fail("bash", new { command = shellRead });

        // grep по конкретному файлу должен искать, а не возвращать «0 файлов».
        await h.Ok("grep", new { pattern = "changed", path = "a.txt" }, o => o.Contains("a.txt"));
    }

    // WORKSPACE_GUARD: агент один раз выполнил листинг C:\WINDOWS\system32 и принял это за работу.
    private static Task SystemRootAsync()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert(ConexyAI.Desktop.Workspace.IsUnsafe(windows), "the Windows folder must not be a workspace");
        Assert(ConexyAI.Desktop.Workspace.IsUnsafe(Path.Combine(windows, "system32")), "system32 must not be a workspace");
        Assert(ConexyAI.Desktop.Workspace.IsUnsafe(Path.GetPathRoot(windows)), "a drive root must not be a workspace");
        Assert(!ConexyAI.Desktop.Workspace.IsUnsafe(Path.GetTempPath()), "a temp folder is fine");

        var runner = new DesktopToolRunner(Path.Combine(windows, "system32"), new AutoApprover(), _ => { });
        var result = runner.RunAsync("file_read", "{\"path\":\"kernel32.dll\"}", CancellationToken.None).GetAwaiter().GetResult();
        Assert(result.IsError, "tools must refuse to work when the workspace is a system folder");
        Assert(result.Output.Contains("не используется"), $"the refusal must explain why: {result.Output}");
        return Task.CompletedTask;
    }

    private static async Task GuardsAsync()
    {
        using var h = new Harness();
        File.WriteAllText(Path.Combine(h.Root, "AGENTS.md"), "rule");
        File.WriteAllText(Path.Combine(h.Root, "secret.txt"), "secret");
        await h.Ok("project_rules", new { files = new[] { "AGENTS.md", "secret.txt" } }, o => o.Contains("rule") && !o.Contains("secret"));
        await h.Fail("ask_user", new { question = "?" });
        await h.Fail("turn_diff", new { });

        await h.Fail("file_read", new { path = "../../etc/passwd" });
        await h.Fail("file_write", new { path = OperatingSystem.IsWindows() ? "C:/Windows/evil.txt" : "/etc/evil.txt", content = "x" });
        await h.Fail("nonexistent_tool", new { });
        await h.Fail("local__x__y", new { });

        h.Runner.PlanMode = true;
        await h.Fail("file_write", new { path = "plan.txt", content = "x" });
        await h.Fail("bash", new { command = "rm AGENTS.md" });
        await h.Ok("file_read", new { path = "AGENTS.md" }, o => o.Contains("rule"));
        Assert(!File.Exists(Path.Combine(h.Root, "plan.txt")), "plan mode must not write files");
    }

    private static async Task NetworkAsync()
    {
        using var h = new Harness();
        await h.Ok("http_request", new { url = "https://example.com", method = "GET" }, o => o.Contains("200"));
        await h.Ok("download_file", new { url = "https://example.com", destination = "dl/example.html" });
        Assert(File.Exists(Path.Combine(h.Root, "dl", "example.html")), "download_file must save the file");
    }

    private static async Task ScreenAsync()
    {
        using var h = new Harness();
        await h.Ok("window_list", new { });
        await h.Ok("screen_capture", new { window = "explorer" }, o => o.StartsWith("IMAGE_BASE64:"));
        await h.Ok("page_screenshot", new { url = "https://example.com", width = 800, height = 600 }, o => o.StartsWith("IMAGE_BASE64:"));
    }

    private static bool NetworkAvailable()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            return http.Send(new HttpRequestMessage(HttpMethod.Head, "https://example.com")).IsSuccessStatusCode;
        }
        catch { return false; }
    }
}
