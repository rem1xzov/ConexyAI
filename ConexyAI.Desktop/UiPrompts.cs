using System.Text;

namespace ConexyAI.Desktop;

/// <summary>Интерактивные меню: выбор варианта, вопрос агента (ask_user), ввод строки с кириллицей.</summary>
public static partial class Ui
{
    /// <summary>
    /// Меню со стрелками и цифрами. Возвращает индекс варианта, -2 — Esc, null — отмена хода (Ctrl+C).
    /// <paramref name="hotkey"/> — дополнительные буквы (y/n/a), возвращающие индекс.
    /// </summary>
    private static async Task<int?> PickAsync(string[] options, CancellationToken ct, Func<char, int?>? hotkey = null)
    {
        var index = 0;
        // MENU_WRAP: сколько ФИЗИЧЕСКИХ строк терминала занимает меню. Раньше перерисовка поднималась на
        // options.Length, а длинный вариант терминал переносил на 2-3 строки сам — курсор оказывался ниже,
        // и каждое нажатие стрелки оставляло копию меню (лесенка одинаковых строк).
        var drawnLines = 0;

        void Draw(bool redraw)
        {
            var layout = LayoutMenu(options, index);
            if (redraw && drawnLines > 0)
                Console.Write($"\r\x1b[{drawnLines}A");
            foreach (var line in layout)
                Console.WriteLine("\x1b[2K" + line);
            // Если новая раскладка короче прежней (сузилось окно), стираем остаток ниже.
            Console.Write("\x1b[J");
            drawnLines = layout.Count;
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
                    return -2;
                default:
                    if (key.KeyChar is >= '1' and <= '9' && key.KeyChar - '1' < options.Length)
                        return key.KeyChar - '1';
                    if (hotkey?.Invoke(key.KeyChar) is { } hot and >= 0)
                        return hot;
                    break;
            }
            Draw(true);
        }
        return null;
    }

    /// <summary>
    /// Строки меню под текущую ширину окна. Каждый вариант переносится по словам, продолжение стоит под
    /// текстом (а не под номером). Если меню не помещается по высоте, перенос отключается и каждый
    /// вариант сокращается до одной строки: многострочное меню выше окна невозможно перерисовать на месте.
    /// </summary>
    private static List<string> LayoutMenu(string[] options, int selected)
    {
        var rows = 25;
        try { rows = Console.WindowHeight; }
        catch (IOException) { }
        return LayoutMenu(options, selected, Width(), rows);
    }

    /// <summary>Та же раскладка для заданных размеров окна — отдельно, чтобы её можно было проверить тестом.</summary>
    public static List<string> LayoutMenu(string[] options, int selected, int width, int rows)
    {
        var wrapped = new List<List<string>>(options.Length);
        var total = 0;
        for (var i = 0; i < options.Length; i++)
        {
            var prefix = 4 + $"{i + 1}. ".Length;
            var parts = WrapPlain(Sanitize(options[i]), Math.Max(10, width - prefix));
            wrapped.Add(parts);
            total += parts.Count;
        }

        var fitsHeight = total <= Math.Max(options.Length, rows - 4);

        var lines = new List<string>(total);
        for (var i = 0; i < options.Length; i++)
        {
            var number = $"{i + 1}. ";
            var indent = new string(' ', 4 + number.Length);
            var parts = fitsHeight
                ? wrapped[i]
                : [CutLine(string.Join(' ', wrapped[i]), Math.Max(10, width - 4 - number.Length))];
            for (var p = 0; p < parts.Count; p++)
            {
                var text = p == 0 ? (i == selected ? "  › " : "    ") + number + parts[p] : indent + parts[p];
                lines.Add(i == selected ? C(Orange, text) : text);
            }
        }
        return lines;
    }

    /// <summary>Перенос по словам до <paramref name="max"/> символов; слишком длинное слово рубится жёстко.</summary>
    private static List<string> WrapPlain(string text, int max)
    {
        var result = new List<string>();
        var line = new StringBuilder();
        foreach (var raw in text.Split(['\r', '\n'], StringSplitOptions.None))
        {
            line.Clear();
            foreach (var word in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var w = word;
                while (w.Length > max)
                {
                    if (line.Length > 0)
                    {
                        result.Add(line.ToString());
                        line.Clear();
                    }
                    result.Add(w[..max]);
                    w = w[max..];
                }
                if (line.Length > 0 && line.Length + 1 + w.Length > max)
                {
                    result.Add(line.ToString());
                    line.Clear();
                }
                if (line.Length > 0) line.Append(' ');
                line.Append(w);
            }
            result.Add(line.ToString());
        }
        return result.Count == 0 ? [string.Empty] : result;
    }

    private static string CutLine(string text, int max) =>
        text.Length <= max ? text : text[..Math.Max(1, max - 1)] + "…";

    /// <summary>Рамка с заголовком и текстом, затем меню. Возвращает индекс или -1 (Esc/отмена).</summary>
    public static async Task<int> ChooseAsync(string title, string body, string[] options, CancellationToken ct)
    {
        lock (Gate)
            _confirming = true;
        try
        {
            StopSpinner();
            FlushText();
            while (Console.KeyAvailable)
                Console.ReadKey(intercept: true);
            DrawBox(title, body, Cyan);
            var picked = await PickAsync(options, ct);
            return picked is >= 0 ? picked.Value : -1;
        }
        finally
        {
            _confirming = false;
        }
    }

    /// <summary>Вопрос агента: варианты + «свой ответ». null — пользователь отказался отвечать (Esc).</summary>
    public static async Task<string?> AskUserAsync(string question, string[] options, CancellationToken ct)
    {
        lock (Gate)
            _confirming = true;
        try
        {
            StopSpinner();
            FlushText();
            while (Console.KeyAvailable)
                Console.ReadKey(intercept: true);
            var title = CurrentAgent.Value is { } asker ? $"Вопрос агента · {asker.Role}" : "Вопрос агента";
            DrawBox(title, question, Cyan);

            string? answer;
            if (options.Length == 0)
            {
                answer = ReadLineEcho(ct);
            }
            else
            {
                var all = options.Append("Свой ответ…").ToArray();
                var picked = await PickAsync(all, ct);
                answer = picked switch
                {
                    null or -2 => null,
                    var i when i == all.Length - 1 => ReadLineEcho(ct),
                    var i => options[i.Value],
                };
            }
            Console.WriteLine(answer is null ? C(Red, "  × без ответа") : C(Green, "  √ " + answer));
            return string.IsNullOrWhiteSpace(answer) ? null : answer.Trim();
        }
        finally
        {
            lock (Gate)
            {
                _confirming = false;
                if (_toolSince is not null)
                    _toolSince = DateTime.UtcNow;
            }
        }
    }

    private static void DrawBox(string title, string body, string color)
    {
        var width = Width();
        Console.WriteLine();
        Console.WriteLine(C(color, $"╭─ {title} ") + C(color, new string('─', Math.Max(0, width - 4 - title.Length))));
        // MENU_WRAP: длинная строка вопроса переносится по словам, и рамка "│ " стоит на КАЖДОЙ строке —
        // раньше продолжение выпадало из рамки и шло от левого края.
        foreach (var line in WrapPlain(Sanitize(body), Math.Max(10, width - 2)))
            Console.WriteLine(C(color, "│ ") + line.TrimEnd());
        Console.WriteLine(C(color, "╰" + new string('─', width - 1)));
    }

    /// <summary>Ввод строки с эхом через ReadKey: Console.ReadLine в conhost ломает кириллицу.</summary>
    public static string? ReadLineEcho(CancellationToken ct)
    {
        Console.Write(C(Orange, "  › "));
        var sb = new StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    return sb.ToString();
                case ConsoleKey.Escape:
                    Console.WriteLine();
                    return null;
                case ConsoleKey.Backspace:
                    if (sb.Length > 0)
                    {
                        sb.Length--;
                        Console.Write("\b \b");
                    }
                    break;
                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        sb.Append(key.KeyChar);
                        Console.Write(key.KeyChar);
                    }
                    break;
            }
        }
        return null;
    }
}
