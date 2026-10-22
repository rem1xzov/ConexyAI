using System.Runtime.CompilerServices;
using ConexyAI.Service;

// BASH_COMPRESS: вывод сборки и тестов едет в контекст на каждом шаге — проверяем, что от него
// остаётся итог и окно вокруг ошибки, а не сорок тысяч символов лога.
internal static class BashOutputCompressorTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("bash output: ANSI and toolchain banners are stripped", StripsNoiseAsync);
        TestRegistry.Add("bash output: a build keeps the summary and the window around errors", BuildKeepsErrorsAsync);
        TestRegistry.Add("bash output: ordinary output is cut by edges, raw mode keeps it", OrdinaryOutputAsync);
        TestRegistry.Add("agent text: raw tool-call markup never reaches the answer", StripsToolMarkupAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static Task StripsNoiseAsync()
    {
        var raw = "\u001b[31mDetermining projects to restore...\u001b[0m\r\n"
                  + "Welcome to .NET 8.0!\r\n"
                  + "\u001b[32mBuild succeeded.\u001b[0m\r\n";
        var (text, _) = BashOutputCompressor.Compress(raw, "dotnet build");
        Assert(!text.Contains('\u001b'), "ANSI escapes must be removed");
        Assert(!text.Contains("Determining projects", StringComparison.Ordinal), "restore banner is noise");
        Assert(!text.Contains("Welcome to .NET", StringComparison.Ordinal), "toolchain welcome is noise");
        Assert(text.Contains("Build succeeded", StringComparison.Ordinal), "the real result stays");
        return Task.CompletedTask;
    }

    private static Task BuildKeepsErrorsAsync()
    {
        var lines = new List<string> { "MSBuild version 17.9" };
        for (var i = 0; i < 300; i++)
            lines.Add($"  Compiling file{i}.cs ... ok");
        lines.Add("Program.cs(12,5): error CS0103: The name 'x' does not exist");
        for (var i = 0; i < 300; i++)
            lines.Add($"  Linking module{i}.dll ... ok");
        lines.Add("    2 Error(s)");
        lines.Add("Time Elapsed 00:00:12.34");

        var (text, truncated) = BashOutputCompressor.Compress(string.Join('\n', lines), "dotnet build");
        Assert(truncated, "a long build log must be reported as compressed");
        Assert(text.Length < 3_000, $"the compact form must be small, got {text.Length}");
        Assert(text.Contains("error CS0103", StringComparison.Ordinal), "the error line must survive");
        Assert(text.Contains("Compiling file299", StringComparison.Ordinal), "two lines of context above stay");
        Assert(text.Contains("Error(s)", StringComparison.Ordinal), "the build summary must survive");
        Assert(text.Contains("Time Elapsed", StringComparison.Ordinal), "the timing line must survive");
        Assert(!text.Contains("Linking module100.dll", StringComparison.Ordinal), "far-away noise must be dropped");
        return Task.CompletedTask;
    }

    private static Task StripsToolMarkupAsync()
    {
        var leaked = "Готово: тестов нет.\n<||DSML|| calls>\n<||DSML|| invoke name=\"grep\">";
        Assert(ConexyAgentRunner.StripToolMarkup(leaked) == "Готово: тестов нет.",
            "raw tool markup must be cut off the answer");
        Assert(ConexyAgentRunner.StripToolMarkup("Обычный ответ без разметки") == "Обычный ответ без разметки",
            "plain text stays intact");
        return Task.CompletedTask;
    }

    private static Task OrdinaryOutputAsync()
    {
        var text = string.Join('\n', Enumerable.Range(1, 2_000).Select(i => $"line {i}"));
        var (compressed, truncated) = BashOutputCompressor.Compress(text, "ls -R");
        Assert(truncated, "a huge listing is truncated");
        Assert(compressed.Contains("line 1\n"), "the head is kept");
        Assert(compressed.Contains("line 2000"), "the tail is kept");
        Assert(!compressed.Contains("line 1000\n"), "the middle is dropped");

        var (raw, rawTruncated) = BashOutputCompressor.Compress(text, "dotnet test", keepAlmostAll: true);
        Assert(!rawTruncated && raw.Length == text.Length, "the raw mode keeps the whole output");
        return Task.CompletedTask;
    }
}
