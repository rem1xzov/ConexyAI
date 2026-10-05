using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using ConexyAI.Configuration;
using ConexyAI.Contract;
using ConexyAI.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

// SEARCH_REPLACE: добавлено 2026-10-05 — тесты глобального поиска и замены по проекту.
internal static class SearchTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("search: literal matches carry path, line, column and length", SearchLiteralAsync);
        TestRegistry.Add("search: regex, case sensitivity and include-glob filter results", SearchRegexAsync);
        TestRegistry.Add("search: an invalid pattern and an empty query are refused", SearchValidationAsync);
        TestRegistry.Add("replace: literal replaces every occurrence and skips binary files", ReplaceLiteralAsync);
        TestRegistry.Add("replace: regex mode supports $1 group references", ReplaceRegexAsync);
        TestRegistry.Add("replace: scoping to one path leaves other files untouched", ReplaceSinglePathAsync);
    }

    private static async Task SearchLiteralAsync()
    {
        var (root, workspace, chatId) = await SetupAsync();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "src", "a.ts"), "const x = foo();\nlet y = 1;\n");
            await File.WriteAllTextAsync(Path.Combine(root, "src", "b.ts"), "// foo here\n");

            var service = Service(workspace);
            var result = await service.SearchAsync(chatId, new ProjectSearchRequest("foo", false, false, null));

            TestRegistry.Assert(result.Success, "search succeeded: " + result.Error);
            TestRegistry.Assert(result.Matches.Count == 2, "two matches, got " + result.Matches.Count);

            var first = result.Matches.Single(m => m.Path == "src/a.ts");
            TestRegistry.Assert(first.Line == 1 && first.Column == 11 && first.Length == 3, $"a.ts match at 1:11 len 3, got {first.Line}:{first.Column} len {first.Length}");
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static async Task SearchRegexAsync()
    {
        var (root, workspace, chatId) = await SetupAsync();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "src", "a.ts"), "const foo1 = 1;\nconst foo2 = 2;\nconst FOO3 = 3;\n");
            await File.WriteAllTextAsync(Path.Combine(root, "notes.txt"), "foo1 foo2 foo3\n");

            var service = Service(workspace);

            var regex = await service.SearchAsync(chatId, new ProjectSearchRequest(@"foo\d", true, true, null));
            TestRegistry.Assert(regex.Success, "regex search succeeded: " + regex.Error);
            // a.ts: foo1, foo2 (not FOO3). notes.txt: foo1, foo2, foo3 on one line.
            TestRegistry.Assert(regex.Matches.Count == 5, "case-sensitive regex matches 5 occurrences, got " + regex.Matches.Count);

            var insensitive = await service.SearchAsync(chatId, new ProjectSearchRequest(@"foo\d", true, false, null));
            TestRegistry.Assert(insensitive.Matches.Count == 6, "case-insensitive regex also matches FOO3, got " + insensitive.Matches.Count);

            var glob = await service.SearchAsync(chatId, new ProjectSearchRequest("foo", false, false, "*.ts"));
            TestRegistry.Assert(glob.Matches.All(m => m.Path.EndsWith(".ts", StringComparison.Ordinal)), "include-glob *.ts excludes notes.txt");
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static async Task SearchValidationAsync()
    {
        var (root, workspace, chatId) = await SetupAsync();
        try
        {
            var service = Service(workspace);
            var invalid = await service.SearchAsync(chatId, new ProjectSearchRequest("(", true, false, null));
            TestRegistry.Assert(!invalid.Success && invalid.Error == "INVALID_PATTERN", "an invalid regex is refused, got " + invalid.Error);

            var empty = await service.SearchAsync(chatId, new ProjectSearchRequest("", false, false, null));
            TestRegistry.Assert(!empty.Success && empty.Error == "EMPTY_QUERY", "an empty query is refused, got " + empty.Error);
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static async Task ReplaceLiteralAsync()
    {
        var (root, workspace, chatId) = await SetupAsync();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "src", "a.ts"), "foo foo bar\n");
            await File.WriteAllTextAsync(Path.Combine(root, "src", "other.ts"), "nothing here\n");
            await File.WriteAllBytesAsync(Path.Combine(root, "img.bin"), new byte[] { (byte)'f', (byte)'o', (byte)'o', 0, 1 });

            var service = Service(workspace);
            var result = await service.ReplaceAsync(chatId, new ProjectReplaceRequest("foo", "baz", false, false, null, null));

            TestRegistry.Assert(result.Success, "replace succeeded: " + result.Error);
            TestRegistry.Assert(result.FilesChanged == 1 && result.Replacements == 2, $"one file, two replacements, got {result.FilesChanged}/{result.Replacements}");
            TestRegistry.Assert((await File.ReadAllTextAsync(Path.Combine(root, "src", "a.ts"))) == "baz baz bar\n", "the file was rewritten");
            TestRegistry.Assert((await File.ReadAllTextAsync(Path.Combine(root, "src", "other.ts"))) == "nothing here\n", "a non-matching file is untouched");
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static async Task ReplaceRegexAsync()
    {
        var (root, workspace, chatId) = await SetupAsync();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "src", "a.ts"), "foo(1) foo(2)\n");
            var service = Service(workspace);

            var result = await service.ReplaceAsync(chatId, new ProjectReplaceRequest(@"foo\((\d)\)", "bar[$1]", true, true, null, null));

            TestRegistry.Assert(result.Success, "regex replace succeeded: " + result.Error);
            TestRegistry.Assert(result.Replacements == 2, "two replacements, got " + result.Replacements);
            TestRegistry.Assert((await File.ReadAllTextAsync(Path.Combine(root, "src", "a.ts"))) == "bar[1] bar[2]\n", "$1 group reference applied");
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static async Task ReplaceSinglePathAsync()
    {
        var (root, workspace, chatId) = await SetupAsync();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "src", "a.ts"), "foo\n");
            await File.WriteAllTextAsync(Path.Combine(root, "src", "b.ts"), "foo\n");

            var service = Service(workspace);
            var result = await service.ReplaceAsync(chatId, new ProjectReplaceRequest("foo", "x", false, false, null, "src/a.ts"));

            TestRegistry.Assert(result.Success && result.FilesChanged == 1, "only the scoped file changes, got " + result.FilesChanged);
            TestRegistry.Assert((await File.ReadAllTextAsync(Path.Combine(root, "src", "a.ts"))) == "x\n", "scoped file replaced");
            TestRegistry.Assert((await File.ReadAllTextAsync(Path.Combine(root, "src", "b.ts"))) == "foo\n", "other file untouched");

            var traversal = await service.ReplaceAsync(chatId, new ProjectReplaceRequest("foo", "x", false, false, null, "../escape.txt"));
            TestRegistry.Assert(!traversal.Success && traversal.Error == "INVALID_PATH", "path traversal is refused, got " + traversal.Error);
        }
        finally
        {
            Cleanup(root);
        }
    }

    // --- helpers -----------------------------------------------------------------------------

    private static ConexyProjectSearchService Service(ConexyWorkspaceService workspace) =>
        new(workspace, new SandboxActivity(), NullLogger<ConexyProjectSearchService>.Instance);

    private static async Task<(string Root, ConexyWorkspaceService Workspace, Guid ChatId)> SetupAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "conexy-search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workspace = new ConexyWorkspaceService(
            Options.Create(new WorkspaceOptions { RootPath = root }),
            NullLogger<ConexyWorkspaceService>.Instance);
        var chatId = Guid.NewGuid();
        var workspaceDir = workspace.GetTaskWorkspacePath(chatId);
        Directory.CreateDirectory(Path.Combine(workspaceDir, "src"));
        await Task.CompletedTask;
        return (workspaceDir, workspace, chatId);
    }

    private static void Cleanup(string root)
    {
        try
        {
            // root points at the chat workspace; clean its parent (the temp root).
            var parent = Directory.GetParent(root)?.FullName;
            if (parent is not null) Directory.Delete(parent, recursive: true);
        }
        catch
        {
            /* best effort */
        }
    }
}
