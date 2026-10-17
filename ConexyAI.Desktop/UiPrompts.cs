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
        foreach (var line in body.Split('\n'))
            Console.WriteLine(C(color, "│ ") + line.TrimEnd('\r'));
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
