using System.Runtime.CompilerServices;
using ConexyAI.Configuration;
using ConexyAI.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

// REPO_MAP: добавлено 2026-11-19
/// <summary>
/// Карта проекта: harness строит компактное описание рабочей области (структура, языки, сборка,
/// точки входа, объявления), инжектит его в контекст и отдаёт инструментом `repo_map`, чтобы агент
/// не тратил токены на перебор файлов. Проверяется на реальной рабочей области в temp-каталоге.
/// </summary>
internal static class RepoMapTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("repo map: the injected block has structure, entry point, build hint and no noise dirs", BlockAsync);
        TestRegistry.Add("repo map: the tool answers a subtree with files and symbols", QueryAsync);
        TestRegistry.Add("repo map: an empty workspace injects nothing and says so", EmptyAsync);
        TestRegistry.Add("repo map: invalidate rebuilds after a new file", InvalidateAsync);
        TestRegistry.Add("repo map: disabled option injects nothing", DisabledAsync);
        TestRegistry.Add("repo map: the block respects the character cap", CapAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static async Task<(RepoMapService Map, IConexyWorkspaceService Workspace, Guid ChatId, string Root)> SetupAsync(
        RepoMapOptions? options = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "repomap_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workspace = new ConexyWorkspaceService(
            Options.Create(new WorkspaceOptions { RootPath = root }),
            NullLogger<ConexyWorkspaceService>.Instance);
        var chatId = Guid.NewGuid();
        var map = new RepoMapService(
            workspace,
            Options.Create(options ?? new RepoMapOptions { RebuildSeconds = 0 }),
            NullLogger<RepoMapService>.Instance);
        await Task.CompletedTask;
        return (map, workspace, chatId, workspace.GetTaskWorkspacePath(chatId));
    }

    private static void Seed(string root)
    {
        Write(root, "src/Program.cs", """
            namespace Demo;

            public class Program
            {
                public static void Main(string[] args) { }
            }

            public sealed class Bootstrap { }
            """);
        Write(root, "src/Service/Agent.cs", """
            namespace Demo.Service;

            public class AgentService
            {
                public void Run() { }
            }
            """);
        Write(root, "frontend/src/App.tsx", "export function App() { return null; }\n");
        Write(root, "README.md", "# Demo project\n");
        Write(root, "package.json", "{ \"name\": \"demo\" }\n");
        // Шум, который карта обязана игнорировать.
        Write(root, "node_modules/lib/index.js", "module.exports = 1;\n");
        Write(root, "bin/Debug/output.dll", "MZ\n");
    }

    private static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static async Task BlockAsync()
    {
        var (map, _, chatId, root) = await SetupAsync();
        try
        {
            Seed(root);
            var block = await map.GetInjectedBlockAsync(chatId);

            Assert(block is not null, "a non-empty workspace must produce a map block");
            Assert(block!.Contains("[Project map]", StringComparison.Ordinal), "the block must be labelled");
            Assert(block.Contains("src/", StringComparison.Ordinal), "top-level directories must be listed");
            Assert(block.Contains("frontend/", StringComparison.Ordinal), "all top-level directories must be listed");
            Assert(block.Contains("Entry points:", StringComparison.Ordinal)
                   && block.Contains("src/Program.cs", StringComparison.Ordinal),
                $"the C# entry point must be named, got: {block}");
            Assert(block.Contains("npm run build", StringComparison.Ordinal), "the build command hint must be present");
            Assert(block.Contains("Symbols:", StringComparison.Ordinal), "declared symbols must be present");
            Assert(block.Contains("Program.cs", StringComparison.Ordinal), "source files must be visible");
            Assert(!block.Contains("node_modules", StringComparison.Ordinal), "noise directories must be excluded");
            Assert(!block.Contains("output.dll", StringComparison.Ordinal), "binary files must be excluded");
            Assert(block.Contains("repo_map", StringComparison.Ordinal), "the block must point to the tool");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task QueryAsync()
    {
        var (map, _, chatId, root) = await SetupAsync();
        try
        {
            Seed(root);
            var text = await map.QueryAsync(chatId, "src", depth: 2, withSymbols: true, maxChars: 4000);

            Assert(text.Contains("[repo_map]", StringComparison.Ordinal), "the answer must be labelled");
            Assert(text.Contains("Program.cs", StringComparison.Ordinal), "files of the subtree must be listed");
            Assert(text.Contains("Agent.cs", StringComparison.Ordinal), "nested files must be listed");
            Assert(text.Contains("AgentService", StringComparison.Ordinal), "symbols must be included");
            Assert(!text.Contains("README.md", StringComparison.Ordinal), "other trees must not leak in");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task EmptyAsync()
    {
        var (map, _, chatId, root) = await SetupAsync();
        try
        {
            Assert(await map.GetInjectedBlockAsync(chatId) is null, "an empty workspace injects nothing");
            var text = await map.QueryAsync(chatId, null, 2, true, 2000);
            Assert(text.Contains("проект ещё пуст", StringComparison.Ordinal), "the tool must explain the empty project: " + text);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task InvalidateAsync()
    {
        var (map, _, chatId, root) = await SetupAsync();
        try
        {
            Seed(root);
            var first = await map.GetInjectedBlockAsync(chatId);
            Assert(first is not null && !first.Contains("Fresh.cs", StringComparison.Ordinal), "the new file is absent before it exists");

            Write(root, "src/Fresh.cs", "namespace Demo;\n\npublic class FreshService { }\n");
            map.Invalidate(chatId);
            var second = await map.GetInjectedBlockAsync(chatId);

            Assert(second is not null && second.Contains("Fresh.cs", StringComparison.Ordinal),
                "the map must reflect a file created after invalidation: " + second);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task DisabledAsync()
    {
        var (map, _, chatId, root) = await SetupAsync(new RepoMapOptions { Enabled = false });
        try
        {
            Seed(root);
            Assert(await map.GetInjectedBlockAsync(chatId) is null, "a disabled map injects nothing");
            var text = await map.QueryAsync(chatId, null, 2, true, 2000);
            Assert(text.Contains("отключён", StringComparison.Ordinal), "the tool must say the map is disabled: " + text);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task CapAsync()
    {
        var (map, _, chatId, root) = await SetupAsync(new RepoMapOptions { RebuildSeconds = 0, MaxChars = 2000 });
        try
        {
            for (var index = 0; index < 60; index++)
            {
                Write(root, $"src/Feature{index:00}/Component{index:00}.cs",
                    $"namespace Demo;\n\npublic class Component{index:00} {{ }}\n");
            }

            var block = await map.GetInjectedBlockAsync(chatId);
            Assert(block is not null, "the map must exist");
            Assert(block!.Length <= 2000 + 64, $"the block must respect MaxChars, got {block.Length}");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
