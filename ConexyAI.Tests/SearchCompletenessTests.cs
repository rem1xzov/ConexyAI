using System.Runtime.CompilerServices;
using ConexyAI.Configuration;
using ConexyAI.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

// GREP_GLOB_COMPLETE: добавлено 2026-11-20
/// <summary>
/// Полнота выдачи поиска: обрезанный glob сообщает об этом и сэмплирует файлы по верхним каталогам,
/// чтобы лимит не прятал целый модуль; обрезанный grep тоже честно помечен.
/// </summary>
internal static class SearchCompletenessTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("search: a capped glob is marked truncated and samples every top-level directory", GlobTruncatedAsync);
        TestRegistry.Add("search: an uncapped glob is complete and untruncated", GlobCompleteAsync);
        TestRegistry.Add("search: a capped grep reports truncation", GrepTruncatedAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static async Task<(ConexyWorkspaceService Workspace, Guid ChatId, string Root)> SetupAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "search_complete_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workspace = new ConexyWorkspaceService(
            Options.Create(new WorkspaceOptions { RootPath = root }),
            NullLogger<ConexyWorkspaceService>.Instance);
        var chatId = Guid.NewGuid();
        var dir = workspace.GetTaskWorkspacePath(chatId);

        foreach (var top in new[] { "alpha", "beta" })
        {
            Directory.CreateDirectory(Path.Combine(dir, top));
            for (var index = 0; index < 5; index++)
                await File.WriteAllTextAsync(Path.Combine(dir, top, $"{top}{index}.txt"), $"needle {index}\n");
        }

        await Task.CompletedTask;
        return (workspace, chatId, root);
    }

    private static async Task GlobTruncatedAsync()
    {
        var (workspace, chatId, root) = await SetupAsync();
        try
        {
            var result = await workspace.GlobAsync(chatId, "**/*.txt", maxResults: 3);
            Assert(result.Success, "glob runs: " + result.Error);
            Assert(result.Files.Count == 3, $"the cap is respected, got {result.Files.Count}");
            Assert(result.Truncated, "a capped glob must report truncation");
            Assert(result.Files.Any(path => path.StartsWith("alpha/", StringComparison.Ordinal))
                   && result.Files.Any(path => path.StartsWith("beta/", StringComparison.Ordinal)),
                "sampling must cover both top-level directories: " + string.Join(",", result.Files));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task GlobCompleteAsync()
    {
        var (workspace, chatId, root) = await SetupAsync();
        try
        {
            var result = await workspace.GlobAsync(chatId, "**/*.txt", maxResults: 100);
            Assert(result.Success && result.Files.Count == 10, $"all ten files are returned, got {result.Files.Count}");
            Assert(!result.Truncated, "a complete glob is not truncated");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task GrepTruncatedAsync()
    {
        var (workspace, chatId, root) = await SetupAsync();
        try
        {
            var result = await workspace.GrepAsync(chatId, "needle", maxResults: 2);
            Assert(result.Success, "grep runs: " + result.Error);
            Assert(result.Matches.Count == 2, $"the match cap is respected, got {result.Matches.Count}");
            Assert(result.Truncated, "a capped grep must report truncation");
            Assert(result.FilesSearched > 0, "grep reports how many files it searched");

            var complete = await workspace.GrepAsync(chatId, "needle", maxResults: 50);
            Assert(!complete.Truncated && complete.Matches.Count == 10, "an uncapped grep is complete");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
