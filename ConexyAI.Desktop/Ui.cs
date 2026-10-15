using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ConexyAI.Desktop.Tools;

namespace ConexyAI.Desktop;

/// <summary>Цвета, баннер, спиннер и оформление вызовов инструментов в консоли (ANSI).</summary>
public static class Ui
{
    private const string Orange = "38;5;209";
    private const string Dim = "38;5;245";
    private const string Green = "38;5;42";
    private const string Red = "38;5;203";
    private const string Yellow = "38;5;221";
    private const string Cyan = "38;5;80";
    private const string PromptBg = "48;5;237";

    private static readonly object Gate = new();
    private static CancellationTokenSource? _spin;

    public static bool Ansi { get; private set; }

    public static string Version =>
        typeof(Ui).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0";

    public static void Init()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        if (Console.IsOutputRedirected || Environment.GetEnvironmentVariable("NO_COLOR") is { Length: > 0 })
            return;

        if (!OperatingSystem.IsWindows())
        {
            Ansi = true;
            return;
        }

        var handle = GetStdHandle(-11);
        Ansi = GetConsoleMode(handle, out var mode) && SetConsoleMode(handle, mode | 0x0004);
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int handle);

    [DllImport("kernel32.dll")]
    private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleMode(IntPtr handle, uint mode);

    private static string C(string color, string text) => Ansi ? $"\x1b[{color}m{text}\x1b[0m" : text;

    public static string Accent(string text) => C(Orange, text);
    public static string Muted(string text) => C(Dim, text);
    public static string Bold(string text) => Ansi ? $"\x1b[1m{text}\x1b[0m" : text;

    private static int Width()
    {
        try { return Math.Clamp(Console.WindowWidth - 1, 30, 110); }
        catch (IOException) { return 80; }
    }

    private static string Rule() => C(Dim, new string('─', Width()));

    public static void Banner(string root, string server)
    {
        Console.WriteLine();
        var art = new[] { " ╭─────╮ ", " │ ◉ ◉ │ ", " ╰─┬─┬─╯ " };
        var info = new[]
        {
            Bold("ConexyAI") + " " + Muted("v" + Version),
            Muted("Conexy Coder · " + server),
            Muted(root),
        };
        for (var i = 0; i < 3; i++)
            Console.WriteLine("  " + C(Orange, art[i]) + "  " + info[i]);
        Console.WriteLine();
    }

    public static void Info(string text) => Console.WriteLine(Muted(text));

    public static void Error(string text)
    {
        Idle();
        Console.Error.WriteLine(C(Red, "✗ " + text));
    }

    // ── Ввод ────────────────────────────────────────────────────────────────

    /// <summary>Рисует рамку ввода с подсказкой под ней и возвращает введённую строку.</summary>
    public static string? ReadPrompt(bool autoMode)
    {
        if (!Ansi)
        {
            Console.Write("> ");
            return Console.ReadLine();
        }

        var footer = autoMode
            ? C(Yellow, "  ⏵⏵ авто-режим: команды выполняются без вопросов") + Muted("  (/auto — выключить)")
            : Muted("  /help — команды · /auto — без подтверждений · Ctrl+C — остановить");

        Console.WriteLine();
        Console.WriteLine(Rule());
        Console.WriteLine(C(Orange, "❯ "));
        Console.WriteLine(Rule());
        Console.Write(footer);
        Console.Write("\x1b[2A\r\x1b[2C"); // на строку ввода, после «❯ »

        var line = Console.ReadLine();

        // Курсор теперь на нижней линии: затираем её и подсказку, выделяем введённое.
        Console.Write("\x1b[1A\r\x1b[2K");
        if (line is { Length: > 0 } && line.Length + 3 < Width())
            Console.Write($"\x1b[{PromptBg}m\x1b[38;5;255m❯ {line}\x1b[K\x1b[0m");
        else
            Console.Write(C(Orange, "❯ ") + line);
        Console.WriteLine();
        Console.Write("\x1b[2K\x1b[1B\x1b[2K\x1b[1A\r");
        return line;
    }

    // ── Спиннер ─────────────────────────────────────────────────────────────

    public static void Busy(string label = "Думаю")
    {
        if (!Ansi)
            return;

        CancellationTokenSource cts;
        lock (Gate)
        {
            if (_spin is not null)
                return;
            cts = _spin = new CancellationTokenSource();
        }

        var watch = Stopwatch.StartNew();
        _ = Task.Run(async () =>
        {
            var frames = new[] { "✻", "✽", "✶", "✳", "✢", "·" };
            var i = 0;
            while (!cts.IsCancellationRequested)
            {
                lock (Gate)
                {
                    if (cts.IsCancellationRequested)
                        break;
                    Console.Write($"\r\x1b[2K{C(Orange, frames[i++ % frames.Length])} {C(Dim, $"{label}… ({(int)watch.Elapsed.TotalSeconds} с · Ctrl+C — остановить)")}");
                }
                await Task.Delay(120);
            }
        });
    }

    public static void Idle()
    {
        lock (Gate)
        {
            if (_spin is null)
                return;
            _spin.Cancel();
            _spin = null;
            if (Ansi)
                Console.Write("\r\x1b[2K");
        }
    }

    // ── Ответ агента и инструменты ──────────────────────────────────────────

    public static void AssistantStart()
    {
        Idle();
        Console.Write("\n" + C("1;37", "● "));
    }

    public static void ToolCall(string described)
    {
        Idle();
        var space = described.IndexOf(' ');
        var tool = space < 0 ? described : described[..space];
        var arg = space < 0 ? string.Empty : described[(space + 1)..];
        var name = tool switch
        {
            "file_read" => "Read",
            "file_write" => "Write",
            "file_patch" or "apply_patch" or "str_replace_editor" => "Edit",
            "workspace_list_files" => "List",
            "grep" => "Search",
            "glob" => "Glob",
            "bash" or "terminal_exec" => "Bash",
            "run_tests" => "Tests",
            "get_diagnostics" => "Diagnostics",
            _ => tool,
        };
        Console.WriteLine("\n" + C(Green, "● ") + Bold(name) + (arg.Length > 0 ? Muted("(" + arg + ")") : string.Empty));
    }

    public static void ShowToolResult(ToolResult result)
    {
        var lines = result.Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var color = result.IsError ? Red : Dim;
        if (lines.Length == 0)
        {
            Console.WriteLine(C(color, "  ⎿  " + (result.IsError ? "ошибка" : "готово")));
            return;
        }

        const int show = 3;
        for (var i = 0; i < Math.Min(show, lines.Length); i++)
        {
            var text = lines[i].Length > 110 ? lines[i][..110] + "…" : lines[i];
            Console.WriteLine(C(color, (i == 0 ? "  ⎿  " : "     ") + text));
        }
        if (lines.Length > show)
            Console.WriteLine(C(Dim, $"     … ещё {lines.Length - show} строк"));
    }

    // ── Подтверждение команды ───────────────────────────────────────────────

    /// <summary>Меню «Да / Да, всегда / Нет» со стрелками. Возвращает 0, 1 или 2; -1 — отмена.</summary>
    public static async Task<int> ConfirmAsync(string command, string folder, CancellationToken ct)
    {
        Idle();
        var width = Width();
        Console.WriteLine();
        Console.WriteLine(C(Yellow, "╭─ Bash ") + C(Yellow, new string('─', Math.Max(0, width - 9))));
        foreach (var line in command.Split('\n'))
            Console.WriteLine(C(Yellow, "│ ") + line.TrimEnd('\r'));
        Console.WriteLine(C(Yellow, "╰" + new string('─', width - 1)));
        Console.WriteLine(Muted("  в " + folder));

        string[] options = ["Да", "Да, и больше не спрашивать (авто-режим)", "Нет"];
        var index = 0;

        void Draw(bool redraw)
        {
            if (redraw)
                Console.Write($"\x1b[{options.Length}A");
            for (var i = 0; i < options.Length; i++)
            {
                var text = $"{i + 1}. {options[i]}";
                Console.WriteLine("\x1b[2K" + (i == index ? C(Orange, "  ❯ " + text) : "    " + text));
            }
        }

        Draw(false);
        while (!ct.IsCancellationRequested)
        {
            if (!Console.KeyAvailable)
            {
                await Task.Delay(30, CancellationToken.None);
                continue;
            }

            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    index = (index + options.Length - 1) % options.Length;
                    break;
                case ConsoleKey.DownArrow:
                    index = (index + 1) % options.Length;
                    break;
                case ConsoleKey.Enter:
                    return index;
                case ConsoleKey.Escape:
                    return 2;
                default:
                    switch (char.ToLowerInvariant(key.KeyChar))
                    {
                        case '1' or 'y' or 'д': return 0;
                        case '2' or 'a' or 'ф': return 1;
                        case '3' or 'n' or 'н': return 2;
                    }
                    break;
            }
            Draw(true);
        }
        return -1;
    }

    public static void Decision(bool allowed) =>
        Console.WriteLine(allowed ? C(Green, "  ✓ разрешено") : C(Red, "  ✗ отклонено"));
}
