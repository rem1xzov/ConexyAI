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

    /// <summary>Включён ли оркестр агентов (для подсказки под вводом).</summary>
    public static bool TeamMode { get; set; }

    public static string Version =>
        typeof(Ui).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0";

    public static void Init()
    {
        // InputEncoding намеренно не трогаем: UTF-8 на входе в conhost превращает кириллицу в нули (0x00).
        Console.OutputEncoding = Encoding.UTF8;

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

    /// <summary>Ответ агента в виде оформленного markdown.</summary>
    public static readonly MarkdownWriter Md = new();

    public static void FlushText() => Md.Flush();

    /// <summary>
    /// Убирает символы, которых нет в шрифте старой консоли Windows (эмодзи и символы из блока Dingbats рисуются
    /// квадратиками). В Windows Terminal всё отображается, поэтому там текст не трогаем.
    /// </summary>
    public static string Sanitize(string text)
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("WT_SESSION") is { Length: > 0 })
            return text;

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c))
            {
                i++; // вторая половина пары: эмодзи — пропускаем целиком
                continue;
            }
            switch (c)
            {
                case '\u2713' or '\u2714': sb.Append('√'); break;
                case '\u2717' or '\u2718' or '\u274c': sb.Append('×'); break;
                case '\uFE0F' or '\u200D': break;
                case >= '\u2600' and <= '\u27BF': break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    public static int Width()
    {
        try { return Math.Clamp(Console.WindowWidth - 1, 30, 110); }
        catch (IOException) { return 80; }
    }

    private static string Rule() => C(Dim, new string('─', Width()));

    // Логотип — двойной шеврон, пиксель-арт 16×10; две строки пикселей = одна строка терминала («▀» с цветом фона).
    private static readonly string[] Crow =
    [
        ".......ww.......",
        ".....wwwwww.....",
        "...wwww..wwww...",
        ".wwww......wwww.",
        "................",
        ".......ww.......",
        ".....wwwwww.....",
        "...wwww..wwww...",
        ".wwww......wwww.",
        "................",
    ];

    private static int CrowColor(char c) => c == 'w' ? 255 : -1;

    private static string CrowRow(int row)
    {
        var sb = new StringBuilder();
        for (var x = 0; x < Crow[0].Length; x++)
        {
            var top = CrowColor(Crow[row * 2][x]);
            var bottom = CrowColor(Crow[row * 2 + 1][x]);
            if (top < 0 && bottom < 0)
                sb.Append(' ');
            else if (bottom < 0)
                sb.Append($"\x1b[38;5;{top}m▀\x1b[0m");
            else if (top < 0)
                sb.Append($"\x1b[38;5;{bottom}m▄\x1b[0m");
            else
                sb.Append($"\x1b[38;5;{top};48;5;{bottom}m▀\x1b[0m");
        }
        return sb.ToString();
    }

    public static void Banner(string root, string server)
    {
        Console.WriteLine();
        var info = new[]
        {
            string.Empty,
            Bold("ConexyAI") + " " + Muted("v" + Version),
            Muted("Conexy Coder · " + server),
            Muted(root),
            string.Empty,
        };
        for (var i = 0; i < 5; i++)
            Console.WriteLine("  " + (Ansi ? CrowRow(i) : new string(' ', 16)) + "  " + info[i]);
        Console.WriteLine();
    }

    // ── Тариф и токены (строка состояния под вводом) ───────────────────────────────────────

    private static string _status = string.Empty;

    public static string FormatTokens(long n) => n switch
    {
        >= 1_000_000 => (n / 1_000_000d).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (n / 1_000d).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "K",
        _ => n.ToString(),
    };

    public static void SetUsage(UsageInfo? usage)
    {
        if (usage is null)
            return;
        var tokens = usage.Unlimited
            ? $"{FormatTokens(usage.AgentUsed)} / ∞"
            : $"{FormatTokens(usage.AgentUsed)} / {FormatTokens(usage.AgentLimit)}";
        var reset = usage.AgentResetsAt.Year > 2000 ? $" · сброс {usage.AgentResetsAt.ToLocalTime():dd.MM}" : string.Empty;
        _status = $"{usage.Tier} · агент: {tokens} токенов{reset}";
    }

    public static void PrintStatus()
    {
        if (_status.Length > 0)
            Console.WriteLine("  " + C(Cyan, _status));
    }

    // ── Оркестр агентов ──────────────────────────────────────────────────────────

    private static readonly string[] AgentColors = ["38;5;80", "38;5;213", "38;5;114", "38;5;215", "38;5;147"];
    private static readonly Dictionary<string, int> AgentIndex = new();
    private static readonly Dictionary<string, string> AgentLast = new();

    private static string AgentTag(string id, string role)
    {
        if (!AgentIndex.TryGetValue(id, out var index))
            AgentIndex[id] = index = AgentIndex.Count;
        return C(AgentColors[index % AgentColors.Length], ">_ " + role);
    }

    public static void TeamRoster(IReadOnlyList<(string Id, string Role, string Brief, string Scope)> agents)
    {
        Idle();
        FlushText();
        AgentIndex.Clear();
        AgentLast.Clear();
        var width = Width();
        Console.WriteLine();
        Console.WriteLine(C(Orange, "● ") + Bold($"Команда из {agents.Count} агентов"));
        foreach (var a in agents)
        {
            var tag = AgentTag(a.Id, a.Role);
            var brief = a.Brief.Replace('\n', ' ');
            if (brief.Length > width - 20)
                brief = brief[..Math.Max(0, width - 21)] + "…";
            Console.WriteLine("  " + tag + (a.Scope.Length > 0 ? Muted("  [" + a.Scope + "]") : string.Empty));
            Console.WriteLine("     " + Muted(brief));
        }
        Console.WriteLine();
    }

    public static void AgentStatus(string id, string role, string stage, string label)
    {
        // «Думает» — шум: показываем только реальные действия и завершение.
        if (stage == "thinking" || label.Length == 0)
            return;
        lock (AgentLast)
        {
            var key = stage + label;
            if (AgentLast.TryGetValue(id, out var last) && last == key)
                return;
            AgentLast[id] = key;
        }

        Idle();
        var mark = stage == "idle" ? C(Green, "√") : C(Dim, "•");
        Console.WriteLine($"  {AgentTag(id, role)} {mark} {(stage == "idle" ? C(Green, label) : label)}");
        Busy("Команда работает");
    }

    public static void HistoryLine(string role, string content)
    {
        content = Sanitize(content);
        var flat = string.Join(' ', content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var max = role == "user" ? 160 : 260;
        if (flat.Length > max)
            flat = flat[..max] + "…";
        Console.WriteLine(role == "user"
            ? "  " + C(Orange, "› ") + C(Dim, flat)
            : "  " + C("1;37", "● ") + C(Dim, flat));
    }

    public static string Highlight(string text) => C(Orange, text);

    public static string Green_(string text) => C(Green, text);

    public static void Info(string text) => Console.WriteLine(Muted(text));

    public static void Error(string text)
    {
        Idle();
        Console.Error.WriteLine(C(Red, "× " + text));
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

        var modes = (autoMode ? C(Yellow, ">> авто") + " " : string.Empty)
                    + (TeamMode ? C("38;5;213", "* команда") + " " : string.Empty);
        var footer = "  " + modes + (_status.Length > 0 ? C(Cyan, _status) + Muted(" · ") : string.Empty) + Muted("/help");

        Console.WriteLine();
        Console.WriteLine(Rule());
        Console.WriteLine(C(Orange, "› "));
        Console.WriteLine(Rule());
        Console.Write(footer);
        Console.Write("\x1b[2A\r\x1b[2C"); // на строку ввода, после «› »

        var line = Console.ReadLine();

        // Курсор теперь на нижней линии: затираем её и подсказку, выделяем введённое.
        Console.Write("\x1b[1A\r\x1b[2K");
        if (line is { Length: > 0 } && line.Length + 3 < Width())
            Console.Write($"\x1b[{PromptBg}m\x1b[38;5;255m› {line}\x1b[K\x1b[0m");
        else
            Console.Write(C(Orange, "› ") + line);
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
            var frames = new[] { "·", "•", "●", "•" };
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
        Md.Begin();
    }

    public static void ToolCall(string described)
    {
        Idle();
        FlushText();
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
            "web_search" => "Web Search",
            "fetch_web_page" => "Fetch",
            "search_user_chats" => "Chats",
            "search_documents" => "Docs",
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
            Console.WriteLine(C(color, "  └  " + (result.IsError ? "ошибка" : "готово")));
            return;
        }

        const int show = 3;
        for (var i = 0; i < Math.Min(show, lines.Length); i++)
        {
            var text = lines[i].Length > 110 ? lines[i][..110] + "…" : lines[i];
            Console.WriteLine(C(color, (i == 0 ? "  └  " : "     ") + text));
        }
        if (lines.Length > show)
            Console.WriteLine(C(Dim, $"     … ещё {lines.Length - show} строк"));
    }

    // ── Подтверждение команды ───────────────────────────────────────────────

    /// <summary>Меню «Да / Да, всегда / Нет» со стрелками. Возвращает 0, 1 или 2; -1 — отмена.</summary>
    public static async Task<int> ConfirmAsync(string command, string folder, CancellationToken ct)
    {
        Idle();
        FlushText();
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
                Console.WriteLine("\x1b[2K" + (i == index ? C(Orange, "  › " + text) : "    " + text));
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
        Console.WriteLine(allowed ? C(Green, "  √ разрешено") : C(Red, "  × отклонено"));
}
