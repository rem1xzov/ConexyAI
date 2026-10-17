using ConexyAI.Desktop.Tools;

namespace ConexyAI.Desktop;

/// <summary>`conexy selftest` — проверка локальных инструментов во временной папке, без сервера.</summary>
public static class SelfTest
{
    private sealed class FixedApprover(bool answer) : ICommandApprover
    {
        public Task<bool> ApproveAsync(string command, string workingDirectory, CancellationToken ct) => Task.FromResult(answer);
    }

    /// <summary>`conexy mcptest команда аргументы...` — запускает stdio MCP-сервер, печатает инструменты и вызывает первый.</summary>
    public static async Task<int> McpAsync(string[] command, string? callArgs)
    {
        var entry = new Mcp.McpServerEntry { Command = command[0], Args = command.Skip(1).ToList() };
        await using var client = new Mcp.StdioMcpClient("test", entry);
        await client.StartAsync(CancellationToken.None);
        if (client.Error is not null)
        {
            Console.WriteLine("FAIL  start: " + client.Error);
            return 1;
        }
        Console.WriteLine($"PASS  start: {client.Tools.Count} tools: {string.Join(", ", client.Tools.Select(t => t.Tool))}");
        if (client.Tools.Count == 0)
            return 0;
        var (text, isError) = await client.CallAsync(client.Tools[0].Tool, callArgs ?? "{}", CancellationToken.None);
        Console.WriteLine($"{(isError ? "FAIL" : "PASS")}  call {client.Tools[0].Tool}: {text}");
        return isError ? 1 : 0;
    }

    public static async Task<int> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "conexy-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var failures = 0;

        async Task Check(string name, ToolRunner runner, string tool, string json, Func<ToolResult, bool> ok)
        {
            var result = await runner.RunAsync(tool, json, CancellationToken.None);
            var pass = ok(result);
            Console.WriteLine($"{(pass ? "PASS" : "FAIL")}  {name}" + (pass ? string.Empty : $"\n      -> {result.Output}"));
            if (!pass) failures++;
        }

        try
        {
            var runner = new ToolRunner(root, new FixedApprover(false), _ => { });

            await Check("file_write", runner, "file_write", """{"path":"src/a.txt","content":"one\ntwo\nthree\n"}""", r => !r.IsError);
            await Check("file_read", runner, "file_read", """{"path":"src/a.txt"}""", r => r.Output.Contains("two"));
            await Check("file_patch", runner, "file_patch", """{"path":"src/a.txt","search_block":"two","replace_block":"2"}""", r => !r.IsError);
            await Check("str_replace", runner, "str_replace_editor", """{"command":"str_replace","path":"src/a.txt","old_str":"three","new_str":"3"}""", r => !r.IsError);
            await Check("str_replace undo", runner, "str_replace_editor", """{"command":"undo","path":"src/a.txt"}""", r => !r.IsError);
            await Check("view range", runner, "str_replace_editor", """{"command":"view","path":"src/a.txt","view_range":[2,2]}""", r => r.Output.Contains('2') && !r.Output.Contains("one"));

            // Патч с намеренно неверными номерами строк и счётчиками — позиция ищется по содержимому.
            var patch = "--- a/src/a.txt\n+++ b/src/a.txt\n@@ -99,9 +99,9 @@\n one\n-2\n+TWO\n three\n";
            await Check("apply_patch (wrong line numbers)", runner, "apply_patch", System.Text.Json.JsonSerializer.Serialize(new { patch }),
                r => !r.IsError && File.ReadAllText(Path.Combine(root, "src", "a.txt")).Contains("TWO"));
            var create = "--- /dev/null\n+++ b/new.txt\n@@ -0,0 +1,2 @@\n+hello\n+world\n";
            await Check("apply_patch (create)", runner, "apply_patch", System.Text.Json.JsonSerializer.Serialize(new { patch = create }),
                r => !r.IsError && File.ReadAllText(Path.Combine(root, "new.txt")).StartsWith("hello"));
            var bad = "--- a/src/a.txt\n+++ b/src/a.txt\n@@ -1,1 +1,1 @@\n-NOPE\n+x\n";
            await Check("apply_patch (no match is atomic error)", runner, "apply_patch", System.Text.Json.JsonSerializer.Serialize(new { patch = bad }), r => r.IsError);

            await Check("grep", runner, "grep", """{"pattern":"TWO"}""", r => r.Output.Contains("src/a.txt:2"));
            await Check("glob", runner, "glob", """{"pattern":"*.txt"}""", r => r.Output.Contains("new.txt"));
            await Check("list", runner, "workspace_list_files", "{}", r => r.Output.Contains("src/a.txt"));

            await Check("path escape ..", runner, "file_read", """{"path":"../outside.txt"}""", r => r.IsError && r.Output.Contains("вне рабочей"));
            await Check("path escape absolute", runner, "file_read", System.Text.Json.JsonSerializer.Serialize(new { path = Path.GetTempPath() }), r => r.IsError);
            await Check("write into .git denied", runner, "file_write", """{"path":".git/config","content":"x"}""", r => r.IsError);

            var echo = OperatingSystem.IsWindows() ? "Write-Output hi" : "echo hi";
            await Check("shell read-only needs no approval", runner, "bash", System.Text.Json.JsonSerializer.Serialize(new { command = OperatingSystem.IsWindows() ? "dir" : "ls" }), r => !r.IsError);
            await Check("shell declined by user", runner, "bash", System.Text.Json.JsonSerializer.Serialize(new { command = echo }), r => r.IsError && r.Output.Contains("отклонил"));
            await Check("forbidden command", runner, "bash", """{"command":"rm -rf /"}""", r => r.IsError && r.Output.Contains("запрещена"));

            var approved = new ToolRunner(root, new FixedApprover(true), _ => { });
            await Check("shell approved", approved, "bash", System.Text.Json.JsonSerializer.Serialize(new { command = echo }), r => !r.IsError && r.Output.Contains("hi"));
            await Check("shell exit code", approved, "bash", System.Text.Json.JsonSerializer.Serialize(new { command = "exit 3" }), r => r.IsError && r.Output.Contains("Exit code: 3"));
            await Check("cmd fallback", approved, "cmd_exec", """{"command":"echo hi"}""", r => !r.IsError && r.Output.Contains("hi"));
            await Check("shell cwd", approved, "bash", """{"command":"pwd","cwd":"src"}""", r => !r.IsError && r.Output.Contains("src"));
            await Check("shell cwd escape", approved, "bash", """{"command":"pwd","cwd":".."}""", r => r.IsError && r.Output.Contains("вне рабочей папки"));
            await Check("cmdlet failure exit code", approved, "bash", """{"command":"Get-Item C:\\nope_conexy_xyz"}""", r => !OperatingSystem.IsWindows() || r.IsError);

            var outside = Path.Combine(Path.GetTempPath(), "conexy-outside-" + Guid.NewGuid().ToString("N") + ".log");
            File.WriteAllText(outside, "line1\nline2\nERROR boom\n");
            try
            {
                var outsideJson = System.Text.Json.JsonSerializer.Serialize(new { path = outside, tail_lines = 2 });
                await Check("pc read declined", runner, "pc_read_file", outsideJson, r => r.IsError && r.Output.Contains("отклонил"));
                await Check("pc read approved", approved, "pc_read_file", outsideJson, r => !r.IsError && r.Output.Contains("ERROR boom") && !r.Output.Contains("line1"));
                await Check("pc copy to project", approved, "pc_copy_to_project",
                    System.Text.Json.JsonSerializer.Serialize(new { source = outside, destination = "logs/app.log" }),
                    r => !r.IsError && File.Exists(Path.Combine(root, "logs", "app.log")));
                await Check("pc write outside denied", approved, "file_write",
                    System.Text.Json.JsonSerializer.Serialize(new { path = outside, content = "x" }), r => r.IsError);
            }
            finally
            {
                File.Delete(outside);
            }

            await Check("http to closed port", approved, "http_request", """{"url":"http://127.0.0.1:9/"}""", r => r.IsError);
            var sleep = OperatingSystem.IsWindows() ? "Write-Output started; Start-Sleep 30" : "echo started; sleep 30";
            await Check("bg start", approved, "bg_start", System.Text.Json.JsonSerializer.Serialize(new { command = sleep, wait_seconds = 3 }),
                r => !r.IsError && r.Output.Contains("bg1") && r.Output.Contains("started"));
            await Check("bg logs", approved, "bg_logs", """{"id":"bg1"}""", r => !r.IsError && r.Output.Contains("работает"));
            await Check("bg stop", approved, "bg_stop", """{"id":"bg1"}""", r => !r.IsError && r.Output.Contains("остановлен"));

            File.WriteAllText(Path.Combine(root, "page.html"), "<html><body style='background:#c00'><h1>Hello</h1></body></html>");
            await Check("page screenshot", approved, "page_screenshot", """{"url":"page.html","width":400,"height":300}""",
                r => (!r.IsError && r.Output.StartsWith("IMAGE_BASE64:image/png:")) || r.Output.Contains("не найден Edge"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }

        Console.WriteLine(failures == 0 ? "\nВсе проверки пройдены." : $"\nПровалено проверок: {failures}.");
        return failures == 0 ? 0 : 1;
    }
}
