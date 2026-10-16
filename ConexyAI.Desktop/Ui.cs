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

    /// <summary>Для самопроверки отрисовки (teamtest) при перенаправленном выводе.</summary>
    public static void ForceAnsi() => Ansi = true;

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

    // Пока команда работает, внизу окна живёт панель: карточка ведущего на всю ширину и по
    // карточке на каждого субагента (в две колонки). Она занимает место однострочного спиннера:
    // Idle() стирает её перед любым выводом, Busy() рисует снова — так она всегда остаётся последней.

    private static readonly string[] AgentColors = ["38;5;80", "38;5;213", "38;5;114", "38;5;215", "38;5;147"];
    private const string LeadColor = "38;5;209";
    private const int MaxBoxLines = 6;

    private sealed class AgentBox(string id, string role, string label, string color)
    {
        public string Id { get; } = id;
        public string Role { get; set; } = role;
        public string Label { get; } = label;
        public string Color { get; } = color;
        public List<(string Text, string Kind)> Lines { get; } = [];
        public DateTime Started { get; } = DateTime.UtcNow;
        public DateTime? Finished { get; set; }
        public int Actions { get; set; }

        public void Push(string text, string kind = "step")
        {
            text = Sanitize(string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
            if (text.Length == 0 || (Lines.Count > 0 && Lines[^1].Text == text))
                return;
            Lines.Add((text, kind));
            if (Lines.Count > MaxBoxLines)
                Lines.RemoveAt(0);
        }
    }

    private sealed class TeamState
    {
        public AgentBox Lead { get; } = new("lead", "ведущий", "orchestrator", LeadColor);
        public List<AgentBox> Workers { get; } = [];
        public DateTime Started { get; } = DateTime.UtcNow;
    }

    private static TeamState? _team;
    private static int _drawn; // сколько строк занимает нарисованная панель (0 — не нарисована)
    private static string _project = string.Empty;

    /// <summary>Субагент, чей инструмент сейчас выполняется на ПК (null — ведущий).</summary>
    public static readonly AsyncLocal<(string Id, string Role)?> CurrentAgent = new();

    public static void SetProject(string root) => _project = Path.GetFileName(root.TrimEnd('\\', '/'));

    private static AgentBox Worker(TeamState team, string id, string role)
    {
        var box = team.Workers.FirstOrDefault(w => w.Id == id);
        if (box is null)
        {
            var n = team.Workers.Count;
            box = new AgentBox(id, role, "#" + (n + 1), AgentColors[n % AgentColors.Length]);
            team.Workers.Add(box);
        }
        else if (role.Length > 0 && role != id)
        {
            box.Role = role;
        }
        return box;
    }

    public static void TeamRoster(IReadOnlyList<(string Id, string Role, string Brief, string Scope)> agents)
    {
        Idle();
        FlushText();
        var width = Width();
        Console.WriteLine();
        Console.WriteLine(C(Orange, "● ") + Bold($"Оркестр: ведущий + {agents.Count} субагента(ов)"));
        lock (Gate)
        {
            var team = _team ??= new TeamState();
            foreach (var a in agents)
            {
                var box = Worker(team, a.Id, a.Role);
                var brief = a.Brief.Replace('\n', ' ');
                if (brief.Length > width - 8)
                    brief = brief[..Math.Max(0, width - 9)] + "…";
                Console.WriteLine("  " + C(box.Color, ">_ " + box.Role) + (a.Scope.Length > 0 ? Muted("  [" + a.Scope + "]") : string.Empty));
                Console.WriteLine("     " + Muted(brief));
            }
            team.Lead.Push("собрал команду: " + string.Join(", ", agents.Select(a => a.Role)));
        }
        Console.WriteLine();
    }

    public static void AgentStatus(string id, string role, string stage, string label)
    {
        if (label.Length == 0)
            return;
        if (!Ansi && stage != "thinking")
            Console.WriteLine($"  [{role}] {(stage == "idle" ? "√ " : string.Empty)}{label}");
        var finishedAll = false;
        lock (Gate)
        {
            var team = _team ??= new TeamState();
            var box = Worker(team, id, role);
            if (stage == "idle")
            {
                box.Finished ??= DateTime.UtcNow;
                box.Push(label, "done");
                finishedAll = team.Workers.All(w => w.Finished is not null);
            }
            else if (stage != "thinking" || box.Lines.Count == 0)
            {
                box.Push(label);
            }
        }
        if (finishedAll)
            TeamEnd();
    }

    /// <summary>Локальный инструмент субагента завершился — короткий итог в его карточку, а не в общую ленту.</summary>
    public static void AgentToolResult(string id, string role, ToolResult result)
    {
        var first = result.Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        lock (Gate)
        {
            var box = Worker(_team ??= new TeamState(), id, role);
            box.Push(first ?? (result.IsError ? "ошибка" : "готово"), result.IsError ? "error" : "result");
        }
    }

    /// <summary>
    /// Команда закончила (или ход завершён): панель остаётся в истории последним снимком,
    /// а дальше снова работает обычный спиннер ведущего.
    /// </summary>
    public static void TeamEnd()
    {
        lock (Gate)
        {
            if (_team is null)
                return;
            if (Ansi && _team.Workers.Count > 0)
            {
                DrawTeam(frame: 0, final: true);
                Console.WriteLine();
            }
            _drawn = 0;
            _team = null;
            _label = "Думаю";
        }
    }

    private static string Fit(string text, int width) =>
        text.Length > width ? text[..Math.Max(0, width - 1)] + "…" : text.PadRight(width);

    private static string Clock(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{t.Minutes}m {t.Seconds:00}s";

    private static string LineColor(string kind) => kind switch
    {
        "error" => Red,
        "done" => Green,
        "result" => Dim,
        _ => "38;5;252",
    };

    // Карточка шириной width: заголовок, body строк действий, строка состояния, нижняя граница.
    private static List<string> RenderBox(AgentBox box, int width, int body, int frame, bool final, bool lead)
    {
        var inner = width - 4;
        var rows = new List<string>();

        var title = ">_ " + box.Role;
        var tail = " " + box.Label + " ";
        if (title.Length + tail.Length + 6 > width)
            title = Fit(title, Math.Max(4, width - tail.Length - 6)).TrimEnd();
        var dashes = Math.Max(1, width - 5 - title.Length - tail.Length);
        rows.Add(C(box.Color, "╭─ ") + Bold(C(box.Color, title)) + C(box.Color, " " + new string('─', dashes)) + Muted(tail) + C(box.Color, "╮"));

        var shown = box.Lines.TakeLast(body).ToList();
        for (var i = 0; i < body; i++)
        {
            string content;
            if (i < shown.Count)
            {
                var (text, kind) = shown[i];
                var mark = kind switch { "error" => "× ", "done" => "√ ", "result" => "  ", _ => "• " };
                content = C(LineColor(kind), Fit(mark + text, inner));
            }
            else
            {
                content = new string(' ', inner);
            }
            rows.Add(C(box.Color, "│ ") + content + C(box.Color, " │"));
        }

        var done = box.Finished is not null;
        var elapsed = (box.Finished ?? DateTime.UtcNow) - box.Started;
        var spinner = new[] { "◐", "◓", "◑", "◒" };
        var icon = done ? "√" : final ? (lead ? "●" : "■") : spinner[frame % spinner.Length];
        var state = done ? "готово" : lead ? (final ? "собирает отчёты" : "координирует") : final ? "остановлен" : "работает";
        var actions = box.Actions > 0 ? $" · {box.Actions} действ." : string.Empty;
        var status = Fit($"{icon} {state} {Clock(elapsed)}{actions}", inner);
        rows.Add(C(box.Color, "│ ") + C(done ? Green : Dim, status) + C(box.Color, " │"));
        rows.Add(C(box.Color, "╰" + new string('─', width - 2) + "╯"));
        return rows;
    }

    // Вызывается под lock(Gate). Перерисовывает панель на месте предыдущей (без мерцания).
    private static void DrawTeam(int frame, bool final = false)
    {
        var team = _team!;
        var width = Width();
        var cols = width >= 72 && team.Workers.Count > 1 ? 2 : 1;
        var colWidth = cols == 2 ? (width - 1) / 2 : width;
        var gridRows = (team.Workers.Count + cols - 1) / cols;

        // Панель должна помещаться в окно, иначе курсор не сможет вернуться к её началу.
        int height;
        try { height = Console.WindowHeight; }
        catch (IOException) { height = 40; }
        var budget = height - 2 - 1 - 5; // подвал + карточка ведущего с 2 строками
        var body = gridRows == 0 ? 3 : Math.Clamp(budget / gridRows - 3, 1, 3);
        var leadBody = budget - gridRows * (body + 3) >= 2 ? 2 : 1;

        var lines = new List<string>();
        lines.AddRange(RenderBox(team.Lead, width, leadBody, frame, final, lead: true));
        for (var r = 0; r < gridRows; r++)
        {
            var left = RenderBox(team.Workers[r * cols], colWidth, body, frame, final, lead: false);
            if (cols == 2 && r * cols + 1 < team.Workers.Count)
            {
                var right = RenderBox(team.Workers[r * cols + 1], colWidth, body, frame, final, lead: false);
                for (var i = 0; i < left.Count; i++)
                    lines.Add(left[i] + " " + right[i]);
            }
            else
            {
                lines.AddRange(left);
            }
        }

        var working = team.Workers.Count(w => w.Finished is null);
        var dot = working > 0 && !final ? C(Green, "●") : C(Dim, "●");
        var footer = final
            ? $" {team.Workers.Count - working}/{team.Workers.Count} готово · {_project} · {Clock(DateTime.UtcNow - team.Started)}"
            : $" {working}/{team.Workers.Count} работают · {_project} · {Clock(DateTime.UtcNow - team.Started)}";
        lines.Add(dot + Muted(footer + (final ? string.Empty : " · Ctrl+C — остановить")));

        var sb = new StringBuilder();
        if (_drawn > 0)
            sb.Append('\r').Append(_drawn > 1 ? $"\x1b[{_drawn - 1}A" : string.Empty);
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append("\x1b[2K").Append(lines[i]);
        }
        sb.Append("\x1b[J");
        Console.Write(sb.ToString());
        _drawn = lines.Count;
    }

    private static void EraseTeam()
    {
        if (_drawn == 0)
            return;
        Console.Write("\r" + (_drawn > 1 ? $"\x1b[{_drawn - 1}A" : string.Empty) + "\x1b[J");
        _drawn = 0;
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
    public static string? ReadPrompt(bool autoMode, string root)
    {
        if (!Ansi || Console.IsInputRedirected)
        {
            Console.Write("> ");
            return Console.ReadLine();
        }

        var modes = (autoMode ? C(Yellow, ">> авто") + " " : string.Empty)
                    + (TeamMode ? C("38;5;213", "* команда") + " " : string.Empty);
        var footer = "  " + modes + (_status.Length > 0 ? C(Cyan, _status) + Muted(" · ") : string.Empty) + Muted("/help");

        // Ввод возможен ТОЛЬКО в строке внутри рамки: набранное пока агент работал отбрасываем, а саму строку
        // редактируем сами (ReadKey), чтобы текст никогда не попадал на рамку или в подсказку.
        while (Console.KeyAvailable)
            Console.ReadKey(intercept: true);

        Console.WriteLine();
        if (Attachments.Pending.Count > 0)
        {
            var chips = Attachments.Describe();
            Console.WriteLine("  " + C(Cyan, chips.Length > Width() - 4 ? chips[..(Width() - 5)] + "…" : chips) + Muted("  /detach"));
        }
        Console.WriteLine(Rule());
        Console.WriteLine(C(Orange, "› "));
        Console.WriteLine(Rule());
        Console.Write(footer);
        var row = Console.CursorTop - 2; // строка ввода (абсолютный номер, не зависит от прокрутки)

        var text = new StringBuilder();
        var pos = 0;
        var start = 0;

        void Redraw()
        {
            var avail = Math.Max(10, Width() - 3);
            if (pos < start) start = pos;
            if (pos > start + avail) start = pos - avail;
            var shown = text.ToString(start, Math.Min(avail, text.Length - start));
            Console.SetCursorPosition(0, row);
            Console.Write(C(Orange, "› ") + shown + "\x1b[K");
            Console.SetCursorPosition(2 + pos - start, row);
        }

        // Сообщение в строку подсказки под рамкой («Прикреплено: …» или ошибка).
        void Hint(string message, bool ok)
        {
            var full = ok
                ? $"{message} · вложений: {Attachments.Pending.Count} · добавьте ещё или допишите текст, Enter — отправить"
                : message;
            if (full.Length > Width() - 3)
                full = full[..(Width() - 4)] + "…";
            Console.SetCursorPosition(0, row + 2);
            Console.Write("\x1b[2K  " + C(ok ? Green : Red, full));
        }

        // Терминал присылает вставку (Ctrl+V в Windows Terminal, перетаскивание файла) как пачку нажатий.
        // В ней может быть перевод строки — раньше он срабатывал как Enter и картинка сразу улетала
        // агенту. Теперь Enter внутри пачки — пробел, а после пачки пути файлов сразу становятся
        // вложениями: можно добавить ещё и дописать текст.
        var burst = 0;

        void AttachPasted()
        {
            var before = Attachments.Pending.Count;
            var errors = new List<string>();
            var rest = Attachments.ExtractFiles(text.ToString(), root, errors);
            if (Attachments.Pending.Count > before)
            {
                text.Clear().Append(rest.Length > 0 ? rest + " " : string.Empty);
                pos = text.Length;
                Hint("Прикреплено: " + string.Join(", ", Attachments.Pending.Skip(before).Select(a => a.FileName)), true);
            }
            else if (errors.Count > 0)
            {
                Hint(errors[0], false);
            }
        }

        Redraw();
        while (true)
        {
            var queued = Console.KeyAvailable;
            var key = Console.ReadKey(intercept: true);
            if (queued)
                burst++;

            if (key.Key == ConsoleKey.Enter)
            {
                // Человек нажал Enter только тогда, когда за ним ничего не идёт сразу же.
                if (!queued)
                    Thread.Sleep(15);
                if (queued || Console.KeyAvailable)
                {
                    if (text.Length > 0 && pos > 0 && text[pos - 1] != ' ')
                        text.Insert(pos++, ' ');
                    burst++;
                }
                else if (burst > 0)
                {
                    // Вставка закончилась переводом строки — не отправляем.
                    burst = 0;
                    AttachPasted();
                    Redraw();
                    continue;
                }
                else
                {
                    break;
                }
            }
            // Ctrl+V: картинка или файлы из буфера прикрепляются (без отправки), текст вставляется в строку.
            // (Когда терминал сам перехватывает Ctrl+V для текста, текст приходит как пачка нажатий.)
            else if (key.KeyChar == '\u0016' || (key.Key == ConsoleKey.V && key.Modifiers.HasFlag(ConsoleModifiers.Control)))
            {
                var (pasted, message) = Attachments.PasteFromClipboard();
                if (pasted is not null)
                {
                    text.Insert(pos, pasted);
                    pos += pasted.Length;
                    AttachPasted();
                }
                else if (message is not null)
                {
                    Hint(message, message.StartsWith("Прикреплено"));
                }
                // Если терминал вдобавок прислал ту же вставку сам, она уже лежит в очереди целиком
                // (PowerShell читает буфер дольше), поэтому её Enter попадёт в ветку «внутри пачки».
                burst = 0;
                Redraw();
                continue;
            }
            else
            {
                HandleEditKey(key);
            }

            if (Console.KeyAvailable)
                continue; // идёт вставка — перерисуем один раз в конце
            if (burst > 1)
                AttachPasted();
            burst = 0;
            Redraw();
        }

        void HandleEditKey(ConsoleKeyInfo key)
        {

            switch (key.Key)
            {
                case ConsoleKey.Backspace:
                    if (pos > 0) text.Remove(--pos, 1);
                    break;
                case ConsoleKey.Delete:
                    if (pos < text.Length) text.Remove(pos, 1);
                    break;
                case ConsoleKey.LeftArrow:
                    if (pos > 0) pos--;
                    break;
                case ConsoleKey.RightArrow:
                    if (pos < text.Length) pos++;
                    break;
                case ConsoleKey.Home:
                    pos = 0;
                    break;
                case ConsoleKey.End:
                    pos = text.Length;
                    break;
                case ConsoleKey.Escape:
                    text.Clear();
                    pos = 0;
                    break;
                default:
                    if (key.Key == ConsoleKey.U && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                    {
                        text.Remove(0, pos);
                        pos = 0;
                    }
                    else if (!char.IsControl(key.KeyChar))
                    {
                        text.Insert(pos++, key.KeyChar);
                    }
                    break;
            }
        }

        var line = text.ToString();

        // Заменяем рамку на выделенную строку ввода и чистим нижнюю линию с подсказкой.
        Console.SetCursorPosition(0, row);
        if (line.Length > 0 && line.Length + 3 < Width())
            Console.Write($"\x1b[{PromptBg}m\x1b[38;5;255m› {line}\x1b[K\x1b[0m");
        else
            Console.Write(C(Orange, "› ") + line + "\x1b[K");
        Console.SetCursorPosition(0, row + 1);
        Console.Write("\x1b[2K");
        Console.SetCursorPosition(0, row + 2);
        Console.Write("\x1b[2K");
        Console.SetCursorPosition(0, row + 1);
        if (Attachments.Pending.Count > 0)
            Console.WriteLine("  " + C(Cyan, "+ " + Attachments.Describe()));
        return line;
    }

    // ── Спиннер ─────────────────────────────────────────────────────────────

    private static string _label = "Думаю";

    public static void Busy(string? label = null)
    {
        if (!Ansi)
            return;

        CancellationTokenSource cts;
        lock (Gate)
        {
            if (label is not null)
                _label = label;
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
                var teamMode = false;
                lock (Gate)
                {
                    if (cts.IsCancellationRequested)
                        break;
                    // Пока работает команда — вместо строки спиннера живая панель агентов.
                    if (_team is { Workers.Count: > 0 })
                    {
                        teamMode = true;
                        DrawTeam(i++);
                    }
                    else
                    {
                        Console.Write($"\r\x1b[2K{C(Orange, frames[i++ % frames.Length])} {C(Dim, $"{_label}… ({(int)watch.Elapsed.TotalSeconds} с · Ctrl+C — остановить)")}");
                    }
                }
                await Task.Delay(teamMode ? 250 : 120);
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
            if (!Ansi)
                return;
            if (_drawn > 0)
                EraseTeam();
            else
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
        // Инструмент субагента — в его карточку, общую ленту ведущего не засоряем.
        if (CurrentAgent.Value is { } worker)
        {
            lock (Gate)
            {
                var box = Worker(_team ??= new TeamState(), worker.Id, worker.Role);
                box.Actions++;
                box.Push(ToolLabel(described));
            }
            return;
        }

        lock (Gate)
        {
            if (_team is not null)
            {
                _team.Lead.Actions++;
                _team.Lead.Push(ToolLabel(described));
            }
        }

        Idle();
        FlushText();
        var space = described.IndexOf(' ');
        var tool = space < 0 ? described : described[..space];
        var arg = space < 0 ? string.Empty : described[(space + 1)..];
        Console.WriteLine("\n" + C(Green, "● ") + Bold(ToolName(tool)) + (arg.Length > 0 ? Muted("(" + arg + ")") : string.Empty));
    }

    private static string ToolName(string tool) =>
        tool switch
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
            "cmd_exec" => "Cmd",
            "bg_start" => "Start",
            "bg_logs" => "Logs",
            "bg_stop" => "Stop",
            "http_request" => "HTTP",
            "page_screenshot" => "Screenshot",
            "pc_list_dir" => "PC List",
            "pc_read_file" => "PC Read",
            "pc_copy_to_project" => "Copy",
            "download_file" => "Download",
            "install_software" => "Install",
            "web_search" => "Web Search",
            "fetch_web_page" => "Fetch",
            "search_user_chats" => "Chats",
            "search_documents" => "Docs",
            _ => tool,
        };

    private static string ToolLabel(string described)
    {
        var space = described.IndexOf(' ');
        var tool = space < 0 ? described : described[..space];
        var arg = space < 0 ? string.Empty : described[(space + 1)..];
        return ToolName(tool) + (arg.Length > 0 ? " " + arg : string.Empty);
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
    public static async Task<int> ConfirmAsync(string title, string command, string folder, CancellationToken ct)
    {
        Idle();
        FlushText();
        // Команду просит субагент — пишем какой, чтобы было понятно, кому разрешаем.
        if (CurrentAgent.Value is { } asker)
            title = $"{title} · {asker.Role}";
        var width = Width();
        Console.WriteLine();
        Console.WriteLine(C(Yellow, $"╭─ {title} ") + C(Yellow, new string('─', Math.Max(0, width - 4 - title.Length))));
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
