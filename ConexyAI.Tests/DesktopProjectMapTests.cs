using System.Runtime.CompilerServices;
using ConexyAI.Desktop.Tools;

// REPO_MAP: добавлено 2026-11-19
/// <summary>
/// Карта проекта для десктоп-режима: файлы лежат на ПК, поэтому карту строит клиент. Проверяется на
/// реальной папке: структура, точки входа, сборка, объявления и отсутствие мусорных каталогов.
/// </summary>
internal static class DesktopProjectMapTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("desktop repo map: structure, entry point, build hint and no noise dirs", FullMapAsync);
        TestRegistry.Add("desktop repo map: a path narrows the map to one subtree", SubtreeAsync);
        TestRegistry.Add("desktop repo map: symbols can be skipped and the answer is capped", OptionsAsync);
        TestRegistry.Add("desktop repo map: an empty project says so", EmptyAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static string Seed()
    {
        var root = Path.Combine(Path.GetTempPath(), "projectmap_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Write(root, "src/Program.cs", "namespace Demo;\n\npublic class Program { public static void Main() { } }\npublic sealed class Bootstrap { }\n");
        Write(root, "src/Service/Agent.cs", "namespace Demo;\n\npublic class AgentService { public void Run() { } }\n");
        Write(root, "frontend/src/App.tsx", "export function App() { return null; }\n");
        Write(root, "README.md", "# demo\n");
        Write(root, "package.json", "{\"name\":\"demo\"}\n");
        Write(root, "node_modules/lib/index.js", "module.exports = 1;\n");
        Write(root, "bin/Debug/output.dll", "MZ");
        return root;
    }

    private static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static Task FullMapAsync()
    {
        var root = Seed();
        try
        {
            var result = new ProjectMapTool(root).Run(ToolArgs.Parse("{}"));
            Assert(!result.IsError, "the map must build: " + result.Output);
            Assert(result.Output.Contains("[repo_map]", StringComparison.Ordinal), "the answer is labelled");
            Assert(result.Output.Contains("src/", StringComparison.Ordinal), "top-level directories are listed");
            Assert(result.Output.Contains("Точки входа:", StringComparison.Ordinal)
                   && result.Output.Contains("src/Program.cs", StringComparison.Ordinal), "the entry point is named");
            Assert(result.Output.Contains("npm run build", StringComparison.Ordinal), "the build command hint is present");
            Assert(result.Output.Contains("Объявления:", StringComparison.Ordinal)
                   && result.Output.Contains("Program", StringComparison.Ordinal), "declarations are listed");
            Assert(!result.Output.Contains("node_modules", StringComparison.Ordinal), "noise directories are excluded");
            Assert(!result.Output.Contains("output.dll", StringComparison.Ordinal), "binary files are excluded");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static Task SubtreeAsync()
    {
        var root = Seed();
        try
        {
            var result = new ProjectMapTool(root).Run(ToolArgs.Parse("""{"path":"src"}"""));
            Assert(result.Output.Contains("Program.cs", StringComparison.Ordinal), "the subtree lists its files");
            Assert(result.Output.Contains("Agent.cs", StringComparison.Ordinal), "nested files are listed");
            Assert(!result.Output.Contains("App.tsx", StringComparison.Ordinal), "other trees do not leak in");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static Task OptionsAsync()
    {
        var root = Seed();
        try
        {
            var map = new ProjectMapTool(root);
            var withoutSymbols = map.Run(ToolArgs.Parse("""{"symbols":false}"""));
            Assert(!withoutSymbols.Output.Contains("Объявления:", StringComparison.Ordinal), "symbols can be skipped");

            var capped = map.Run(ToolArgs.Parse("""{"max_chars":800}"""));
            Assert(capped.Output.Length <= 900, $"the answer respects max_chars, got {capped.Output.Length}");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static Task EmptyAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "projectmap_empty_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var result = new ProjectMapTool(root).Run(ToolArgs.Parse("{}"));
            Assert(!result.IsError && result.Output.Contains("пуст", StringComparison.Ordinal),
                "an empty project must say so: " + result.Output);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }
}
