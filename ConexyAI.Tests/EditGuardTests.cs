using System.Runtime.CompilerServices;
using ConexyAI.Service;
using DesktopTools = ConexyAI.Desktop.Tools;

// EDIT_AFTER_READ + BASH_FILE_OPS: контракт из Claude Code, которого не хватало.
// 1. Файл нельзя править, не прочитав его в этом прогоне (кроме создания нового).
// 2. Файловые операции через bash отклоняются один раз с указанием точного инструмента.
internal static class EditGuardTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("edit guard: writing an unread file is refused, reading unlocks it", ServerReadBeforeEditAsync);
        TestRegistry.Add("edit guard: a new file needs no read, apply_patch checks every header path", ServerNewFileAndPatchAsync);
        TestRegistry.Add("edit guard: bash file operations are refused once and name the tool", ServerBashFileOpsAsync);
        TestRegistry.Add("desktop tools: editing an unread file is refused until file_read", DesktopReadBeforeEditAsync);
        TestRegistry.Add("desktop tools: reading a file through bash is refused once", DesktopBashFileOpsAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static Task ServerReadBeforeEditAsync()
    {
        var guard = new EditAfterReadGuard(_ => true);

        Assert(guard.Refuse("file_patch", """{"path":"src/A.cs","old_text":"a","new_text":"b"}""") is not null,
            "patching a file that was never read must be refused");
        Assert(guard.Refuse("file_write", """{"path":"src/A.cs","content":"x"}""") is not null,
            "writing a file that was never read must be refused");
        Assert(guard.Refuse("str_replace_editor", """{"command":"str_replace","path":"src/A.cs"}""") is not null,
            "str_replace_editor must obey the same rule");

        // Отказ не делает файл прочитанным, а реальное чтение — делает.
        guard.Record("file_read", """{"path":"src/A.cs"}""");

        Assert(guard.Refuse("file_patch", """{"path":"src/A.cs","old_text":"a","new_text":"b"}""") is null,
            "after file_read the same edit is allowed");
        Assert(guard.Refuse("file_write", """{"path":"./src/A.cs","content":"x"}""") is null,
            "the same file written with a different spelling is the same file");
        Assert(guard.Refuse("file_write", """{"path":"src/B.cs","content":"x"}""") is not null,
            "another file is still unread");
        Assert(guard.Refuse("str_replace_editor", """{"command":"view","path":"src/B.cs"}""") is null,
            "view is a read, not an edit");
        guard.Record("str_replace_editor", """{"command":"view","path":"src/B.cs"}""");
        Assert(guard.Refuse("file_write", """{"path":"src/B.cs","content":"x"}""") is null,
            "view counts as a read");
        return Task.CompletedTask;
    }

    private static Task ServerNewFileAndPatchAsync()
    {
        var guard = new EditAfterReadGuard(_ => false);
        Assert(guard.Refuse("file_write", """{"path":"src/New.cs","content":"x"}""") is null,
            "a file that does not exist yet can be created without reading");

        var existing = new EditAfterReadGuard(_ => true);
        const string patch = """
            --- a/src/Old.cs
            +++ b/src/Old.cs
            @@ -1 +1 @@
            -a
            +b
            """;
        Assert(existing.Refuse("apply_patch", $$"""{"patch":{{System.Text.Json.JsonSerializer.Serialize(patch)}}}""") is not null,
            "apply_patch must check the paths from the diff headers");

        existing.Record("file_read", """{"path":"src/Old.cs"}""");
        Assert(existing.Refuse("apply_patch", $$"""{"patch":{{System.Text.Json.JsonSerializer.Serialize(patch)}}}""") is null,
            "a read file unlocks its patch");
        return Task.CompletedTask;
    }

    private static Task ServerBashFileOpsAsync()
    {
        var guard = new EditAfterReadGuard(_ => true);
        const string read = """{"command":"Get-Content src/A.cs"}""";
        var first = guard.Refuse("bash", read);
        Assert(first is not null && first.Contains("file_read", StringComparison.Ordinal),
            "Get-Content must be refused with a pointer to file_read");
        Assert(guard.Refuse("bash", read) is null, "the refusal is given exactly once");

        var write = guard.Refuse("bash", """{"command":"Set-Content src/A.cs 'x'"}""");
        Assert(write is not null && write.Contains("apply_patch", StringComparison.Ordinal),
            "Set-Content must point at file_write/apply_patch");
        Assert(guard.Refuse("bash", """{"command":"sed -i 's/a/b/' src/A.cs"}""") is not null,
            "sed -i is a file edit");
        // READ_VIA_SHELL: прогон на реальном проекте — 50+ чтений кода через `sed -n`/`cat`, ни одного file_read.
        var sedRead = guard.Refuse("bash", """{"command":"sed -n '1,60p' src/A.cs"}""");
        Assert(sedRead is not null && sedRead.Contains("file_read", StringComparison.Ordinal),
            "reading code with sed -n must point at file_read");
        Assert(guard.Refuse("bash", """{"command":"cat src/A.cs"}""") is not null,
            "cat is not a file-reading tool here");
        Assert(guard.Refuse("bash", """{"command":"head -40 src/A.cs"}""") is not null,
            "head is not a file-reading tool here");
        Assert(guard.Refuse("bash", """{"command":"grep -n \"x\" src/A.cs"}""") is not null,
            "grep through the shell must point at the grep tool");
        Assert(guard.Refuse("bash", """{"command":"find . -name \"*.cs\""}""") is not null,
            "find -name must point at glob");

        Assert(guard.Refuse("bash", """{"command":"dotnet build"}""") is null,
            "builds and tests stay allowed");
        Assert(guard.Refuse("bash", """{"command":"git status"}""") is null,
            "ordinary shell work stays allowed");
        return Task.CompletedTask;
    }

    private static async Task DesktopReadBeforeEditAsync()
    {
        using var harness = new DesktopHarness();
        await harness.Ok("file_write", new { path = "src/New.cs", content = "one\n" });
        await harness.Ok("file_read", new { path = "src/New.cs" });
        await harness.Ok("file_write", new { path = "src/New.cs", content = "two\n" });

        var unread = await harness.Run("file_write", new { path = "src/Other.cs", content = "x" });
        Assert(!unread.IsError, "creating a new file needs no read");
        var again = await harness.Run("file_write", new { path = "src/Other.cs", content = "y" });
        Assert(again.IsError && again.Output.Contains("file_read", StringComparison.Ordinal),
            "overwriting a file that was never read must be refused");

        var patch = await harness.Run("apply_patch", new
        {
            patch = "--- a/src/Other.cs\n+++ b/src/Other.cs\n@@ -1 +1 @@\n-x\n+y\n",
        });
        Assert(patch.IsError, "apply_patch on an unread file must be refused");

        await harness.Ok("file_read", new { path = "src/Other.cs" });
        await harness.Ok("apply_patch", new
        {
            patch = "--- a/src/Other.cs\n+++ b/src/Other.cs\n@@ -1 +1 @@\n-x\n+z\n",
        });
    }

    private static async Task DesktopBashFileOpsAsync()
    {
        using var harness = new DesktopHarness();
        await harness.Ok("file_write", new { path = "notes.txt", content = "hello\n" });

        var read = await harness.Run("bash", new { command = "Get-Content notes.txt", description = "Смотрю файл" });
        Assert(read.IsError && read.Output.Contains("file_read", StringComparison.Ordinal),
            "Get-Content must be refused with a pointer to file_read");

        var write = await harness.Run("bash", new { command = "Set-Content notes.txt 'x'", description = "Пишу файл" });
        Assert(write.IsError && write.Output.Contains("apply_patch", StringComparison.Ordinal),
            "Set-Content must point at apply_patch/file_write");

        var build = await harness.Run("bash", new { command = "echo ok", description = "Проверяю оболочку" });
        Assert(!build.IsError, "ordinary commands are unaffected");
    }

    private sealed class DesktopHarness : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "conexy_edit_guard_" + Guid.NewGuid().ToString("N"));
        private DesktopTools.ToolRunner Runner { get; }

        public DesktopHarness()
        {
            Directory.CreateDirectory(Root);
            Runner = new DesktopTools.ToolRunner(Root, new AutoApprover(), _ => { });
        }

        public Task<DesktopTools.ToolResult> Run(string tool, object args) =>
            Runner.RunAsync(tool, System.Text.Json.JsonSerializer.Serialize(args), CancellationToken.None);

        public async Task<string> Ok(string tool, object args)
        {
            var result = await Run(tool, args);
            Assert(!result.IsError, $"{tool} failed: {result.Output}");
            return result.Output;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private sealed class AutoApprover : DesktopTools.ICommandApprover
        {
            public Task<bool> ApproveAsync(string command, string workingDirectory, CancellationToken ct) => Task.FromResult(true);
        }
    }
}
