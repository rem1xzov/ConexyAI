using System.Text;
using System.Text.Json;

namespace ConexyAI.Service;

// REPEAT_REMINDER: добавлено 2026-11-20 — мягкая ступень перед жёстким отказом AgentLoopGuard.
// Отказ стражa срабатывает только на ИДЕНТИЧНЫЙ вывод, а повтор того же действия с чуть другим выводом
// (время, id, метрики) проходит мимо и сжигает шаги. Подсказка считает каноничные аргументы и срабатывает
// один раз на каждый порог: 3 — предупредить, 5 — потребовать перечитать вывод, 8 — потребовать назвать
// блокирующую причину. Для ПРОВЕРОК (тесты/сборка) порог ниже — второй запуск той же команды уже не даёт
// новых данных, а стресс-тест показал, что агент повторял успешный прогон тестов 2–4 раза подряд.
// Механизм намеренно не блокирующий: последнее слово за моделью.
public sealed class RepeatToolReminder
{
    /// <summary>Первый порог: мягкое предупреждение.</summary>
    public const int FirstThreshold = 3;

    /// <summary>Порог для повторного запуска тестов/сборки: второй прогон уже не даёт новых данных.</summary>
    public const int VerificationThreshold = 2;

    /// <summary>Второй порог: требование перечитать вывод и сменить гипотезу.</summary>
    public const int SecondThreshold = 5;

    /// <summary>Последний порог: назвать блокирующую причину вместо повтора.</summary>
    public const int FinalThreshold = 8;

    private const int MaxCanonicalChars = 1_000;

    private static readonly int[] DefaultThresholds = [FirstThreshold, SecondThreshold, FinalThreshold];
    private static readonly int[] VerificationThresholds = [VerificationThreshold, SecondThreshold, FinalThreshold];

    // Инструменты, для которых повтор — норма (служебные вызовы и наблюдение), а не топтание.
    private static readonly HashSet<string> Ignored = new(StringComparer.Ordinal)
    {
        "todo_write", "team_message", "agents_status", "read_skill", "spawn_agents",
        "computer_screen", "computer_wait", "ask_user", "present",
    };

    // VERIFICATION_REPEAT: проверочные команды, которые агент запускает через bash.
    private static readonly System.Text.RegularExpressions.Regex VerificationCommand = new(
        "(?i)(pytest|dotnet\\s+test|npm\\s+(run\\s+)?test|yarn\\s+test|pnpm\\s+(run\\s+)?test|" +
        "cargo\\s+test|go\\s+test|mvn\\s+test|gradle\\s+test|vitest|jest|ctest|phpunit|mix\\s+test|rspec)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _lastFired = new(StringComparer.Ordinal);

    /// <summary>
    /// Учитывает вызов и возвращает подсказку, если он пересёк новый порог. <c>null</c> — подсказки нет.
    /// </summary>
    public string? Record(string tool, string? argumentsJson)
    {
        if (Ignored.Contains(tool))
            return null;

        var key = Key(tool, argumentsJson);
        var count = _counts.GetValueOrDefault(key) + 1;
        _counts[key] = count;

        var thresholds = IsVerification(tool, argumentsJson) ? VerificationThresholds : DefaultThresholds;
        var threshold = thresholds.FirstOrDefault(candidate =>
            candidate == count && _lastFired.GetValueOrDefault(key) < candidate);
        if (threshold == 0)
            return null;

        _lastFired[key] = threshold;
        return threshold switch
        {
            VerificationThreshold =>
                $"[Repeat] Та же проверка `{tool}` уже завершилась ({count}-й запуск подряд с теми же аргументами). " +
                "Новых данных повтор не даст: процитируй последний вывод в отчёте и переходи дальше.",
            FirstThreshold =>
                $"[Repeat] Ты повторяешь одно и то же действие `{tool}` с теми же аргументами ({count}-й раз). " +
                "Если оно не даёт нового результата — смени подход: другой инструмент, другие аргументы или проверь факт окружением.",
            SecondThreshold =>
                $"[Repeat] То же действие `{tool}` выполняется {count}-й раз. Результат уже известен — перечитай последний вывод, " +
                "выдвини другую гипотезу, проверь версию/зависимость/путь. Повторять этот вызов бессмысленно.",
            _ =>
                $"[Repeat] Одно и то же действие `{tool}` — {count}-й раз. Прекрати повтор: либо докажи, что причина внешняя " +
                "(и назови её в отчёте), либо переходи к другой части работы.",
        };
    }

    /// <summary>
    /// Проверка ли это (тесты/сборка/диагностика): повтор успешного прогона не даёт новых данных.
    /// Для bash-команд проверяем текст команды, а не имя инструмента.
    /// </summary>
    public static bool IsVerification(string tool, string? argumentsJson)
    {
        if (tool is "run_tests" or "get_diagnostics")
            return true;
        if (tool is not ("bash" or "terminal_exec" or "cmd_exec"))
            return false;
        try
        {
            using var document = JsonDocument.Parse(argumentsJson ?? "{}");
            return document.RootElement.TryGetProperty("command", out var command)
                   && command.ValueKind == JsonValueKind.String
                   && VerificationCommand.IsMatch(command.GetString() ?? string.Empty);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Сбрасывает счётчики — после смены подхода или нового сообщения пользователя.</summary>
    public void Reset()
    {
        _counts.Clear();
        _lastFired.Clear();
    }

    private static string Key(string tool, string? argumentsJson) =>
        tool + "\u0000" + Canonicalize(argumentsJson);

    /// <summary>
    /// Каноничный вид аргументов: ключи объектов отсортированы, пробелы нормализованы. Один и тот же
    /// вызов в другой раскладке JSON не должен считаться новым действием.
    /// </summary>
    public static string Canonicalize(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return string.Empty;

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var builder = new StringBuilder();
            WriteCanonical(document.RootElement, builder);
            var text = builder.ToString();
            return text.Length <= MaxCanonicalChars ? text : text[..MaxCanonicalChars];
        }
        catch (JsonException)
        {
            var trimmed = argumentsJson.Trim();
            return trimmed.Length <= MaxCanonicalChars ? trimmed : trimmed[..MaxCanonicalChars];
        }
    }

    private static void WriteCanonical(JsonElement element, StringBuilder builder)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var first = true;
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first) builder.Append(',');
                    first = false;
                    builder.Append(JsonSerializer.Serialize(property.Name)).Append(':');
                    WriteCanonical(property.Value, builder);
                }
                builder.Append('}');
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                var firstItem = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!firstItem) builder.Append(',');
                    firstItem = false;
                    WriteCanonical(item, builder);
                }
                builder.Append(']');
                break;
            case JsonValueKind.String:
                builder.Append(JsonSerializer.Serialize(element.GetString()));
                break;
            default:
                builder.Append(element.GetRawText());
                break;
        }
    }
}
