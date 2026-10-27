using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ConexyAI.Desktop;

// MENU_WRAP: меню ask_user в терминале десктопа. Длинные варианты переносились самим терминалом, а
// перерисовка поднимала курсор на число ВАРИАНТОВ, а не СТРОК — на экране оставалась «лесенка» копий.
// Раскладка теперь считает физические строки сама, поэтому проверяем её на реальных данных скриншота.
internal static class DesktopMenuTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("desktop menu: long options wrap under their text and never exceed the window", WrapsAsync);
        TestRegistry.Add("desktop menu: a menu taller than the window collapses to one line per option", CollapsesAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static readonly string[] LongOptions =
    [
        "Сделай без внешних ключей: метро с цветом ветки и минутами пешком, цены из каталога с честным priceSource. Маршрут — потом добавь Яндекс Maps JS API — я дам ключ, рисуй маршрут как в Яндекс.Картах",
        "Добавь Яндекс Maps JS API — я дам ключ, рисуй маршрут как в Яндекс.Картах",
        "Сначала только живые цены через парсер — метро отложим",
        "Сначала только метро и точки приёма — цены отложим",
        "Свой ответ…",
    ];

    private static string Plain(string line) => Regex.Replace(line, "\x1b\\[[0-9;]*[A-Za-z]", string.Empty);

    private static Task WrapsAsync()
    {
        const int width = 80;
        var lines = Ui.LayoutMenu(LongOptions, selected: 0, width, rows: 40).Select(Plain).ToList();

        Assert(lines.All(l => l.Length <= width), "no menu line may be wider than the window, or the terminal wraps it again");
        Assert(lines.Count > LongOptions.Length, "the long option must take several lines");

        // Номер варианта стоит только в первой строке, продолжение сдвинуто под текст.
        // Выбранная строка начинается с маркера «›», поэтому его убираем перед подсчётом.
        static string NoMarker(string l) => l.TrimStart().TrimStart('›').TrimStart();
        Assert(lines.Count(l => NoMarker(l).StartsWith("1. ", StringComparison.Ordinal)) == 1, "option 1 is numbered once");
        var continuation = lines[1];
        Assert(continuation.StartsWith("       ", StringComparison.Ordinal) && !continuation.TrimStart().StartsWith("1.", StringComparison.Ordinal),
            $"a continuation line sits under the text, got '{continuation}'");

        // Всё содержимое сохранено: ничего не потеряно при переносе.
        var joined = string.Join(' ', lines);
        Assert(joined.Contains("priceSource") && joined.Contains("Яндекс.Картах") && joined.Contains("метро отложим"),
            "wrapping must not drop words");
        return Task.CompletedTask;
    }

    private static Task CollapsesAsync()
    {
        // Окно в 6 строк: развёрнутое меню не влезет, поэтому по одной строке на вариант и без переноса.
        var lines = Ui.LayoutMenu(LongOptions, selected: 2, width: 60, rows: 6).Select(Plain).ToList();
        Assert(lines.Count == LongOptions.Length, $"a menu that cannot fit must be one line per option, got {lines.Count}");
        Assert(lines.All(l => l.Length <= 60), "collapsed lines still fit the window");
        Assert(lines[0].EndsWith("…", StringComparison.Ordinal), "a cut option must show the ellipsis");
        Assert(lines[2].TrimStart().StartsWith("›", StringComparison.Ordinal), "the selected option keeps its marker");
        return Task.CompletedTask;
    }
}
