using System.Globalization;
using System.Text.Json;

namespace ConexyAI.Service;

// COMPUTER_USE: добавлено 2026-11-19 — защита от «застревания» на экране по НАБЛЮДАЕМОМУ состоянию,
// а не по тексту ответа. Три независимых признака:
//   1) одно и то же действие в той же точке несколько раз подряд НЕ изменило кадр — цель не принимает
//      клик (или координаты уехали после смены DPI/окна);
//   2) кадр не меняется N действий подряд — приложение застыло или действует другое окно;
//   3) одна и та же цель повторяется, при том что эффекта не было — модель ходит по кругу.
// Отказ одноразовый (счётчики сбрасываются), чтобы после смены подхода работа продолжалась.
public sealed class ComputerUseGuard
{
    /// <summary>Сколько действий без изменения кадра в одной точке считаются «клик в пустоту».</summary>
    public const int NoEffectThreshold = 2;

    /// <summary>Сколько действий подряд с одинаковым кадром считаются зависшим экраном.</summary>
    public const int FrozenThreshold = 3;

    /// <summary>Сколько повторений одной и той же цели без эффекта считаются хождением по кругу.</summary>
    public const int SameTargetThreshold = 3;

    private readonly Dictionary<string, TargetEntry> _targets = new(StringComparer.Ordinal);
    private ulong? _lastFrame;
    private int _frozen;

    /// <summary>Изменился ли экран после последнего действия (основа учёта прогресса).</summary>
    public bool LastActionChanged { get; private set; } = true;

    /// <summary>Считается ли инструмент действием на экране (наблюдение и пауза — нет).</summary>
    public static bool Applies(string tool) =>
        tool.StartsWith("computer_", StringComparison.Ordinal)
        && tool is not ("computer_screen" or "computer_wait");

    /// <summary>
    /// Проверка ПЕРЕД действием: <c>null</c> — можно, иначе готовый текст отказа с требованием
    /// сменить подход (а не повторять то же самое).
    /// </summary>
    public string? Refuse(string tool, string arguments)
    {
        if (!Applies(tool))
            return null;

        var target = _targets.GetValueOrDefault(Key(tool, arguments));
        if (target is { NoEffect: >= NoEffectThreshold })
        {
            Reset();
            return "[Computer Use Guard] Экран не изменился после твоего действия в этой точке несколько раз — " +
                "цель не принимает клик. Не повторяй: пересмотри активное окно (`computer_screen`), используй " +
                "клавиатуру (`computer_key` / `computer_type`), прокрутку или подожди, пока интерфейс отрисуется. " +
                "Если элемент всё равно не реагирует — опиши это и спроси пользователя.";
        }

        if (_frozen >= FrozenThreshold)
        {
            Reset();
            return "[Computer Use Guard] Кадр экрана не меняется уже несколько действий — приложение зависло " +
                "или работает другое окно. Не кликай по кругу: сними экран, проверь активное окно " +
                "(`computer_screen`), при необходимости открой нужную программу заново; если кадр так и не " +
                "меняется — опиши ситуацию и спроси пользователя.";
        }

        if (target is { Count: >= SameTargetThreshold, NoEffect: > 0 })
        {
            Reset();
            return "[Computer Use Guard] Ты несколько раз действуешь в одну и ту же точку без видимого " +
                "результата. Смени подход: другая цель на экране, клавиатура вместо мыши, либо уточни задачу " +
                "у пользователя.";
        }

        return null;
    }

    /// <summary>Учитывает результат действия по состоянию кадра из вывода инструмента.</summary>
    public void Record(string tool, string arguments, string? output, bool isError)
    {
        var state = ScreenStateParser.Parse(output);
        var changed = state?.Changed;

        if (!Applies(tool))
        {
            // Наблюдение (computer_screen) тоже обновляет кадр, чтобы «застывший экран» был виден и без кликов.
            TrackFrame(state?.Frame);
            return;
        }

        LastActionChanged = changed ?? true;

        var key = Key(tool, arguments);
        var entry = _targets.TryGetValue(key, out var existing) ? existing : new TargetEntry();
        entry.Count++;
        if (changed == false)
            entry.NoEffect++;
        else if (changed == true)
            entry.NoEffect = 0;
        _targets[key] = entry;

        TrackFrame(state?.Frame);
    }

    /// <summary>Сбрасывает счётчики — после ремонта/смены режима работа продолжается как обычно.</summary>
    public void Reset()
    {
        _targets.Clear();
        _frozen = 0;
        _lastFrame = null;
    }

    private void TrackFrame(string? frame)
    {
        if (string.IsNullOrWhiteSpace(frame)
            || !ulong.TryParse(frame, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bits))
            return;

        if (_lastFrame == bits)
        {
            _frozen++;
        }
        else
        {
            _frozen = 1;
            _lastFrame = bits;
        }
    }

    /// <summary>Ключ цели: действие + ячейка координат (8 px), иначе — нормализованные аргументы.</summary>
    private static string Key(string tool, string arguments)
    {
        int? x = null;
        int? y = null;
        try
        {
            using var document = JsonDocument.Parse(arguments);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("x", out var xElement) && xElement.TryGetInt32(out var xValue)) x = xValue;
                if (root.TryGetProperty("y", out var yElement) && yElement.TryGetInt32(out var yValue)) y = yValue;
            }
        }
        catch (JsonException)
        {
            // Невалидные аргументы — ниже отработает текстовый ключ.
        }

        if (x is null || y is null)
            return tool + "|txt:" + (arguments.Length <= 80 ? arguments : arguments[..80]);

        // Ячейка 8 px: «клик рядом» тем же действием — та же цель, а не новая.
        return $"{tool}|{x.Value / 8 * 8}:{y.Value / 8 * 8}";
    }

    private sealed class TargetEntry
    {
        public int Count { get; set; }
        public int NoEffect { get; set; }
    }
}
