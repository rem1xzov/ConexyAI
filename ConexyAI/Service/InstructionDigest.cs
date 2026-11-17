using System.Security.Cryptography;
using System.Text;

namespace ConexyAI.Service;

/// <summary>
/// INSTRUCTION_DIGEST: правила проекта не переотправляются, если не изменились.
/// Перенесено из deepseek-harness (<c>context/agent-instructions/src/state.ts:54-58</c>): на сессию
/// хранится <c>{path, digest = SHA-1}</c>, неизменённые файлы пропускаются, изменения приходят как
/// <c>replace</c>, исчезнувшие — как <c>remove</c>. У нас правила проекта читались один раз за прогон,
/// поэтому правка AGENTS.md посреди задачи не действовала, а повторная загрузка дублировала бы текст.
/// </summary>
public sealed class InstructionDigest
{
    private string? _digest;

    /// <summary>Текущий отпечаток или null, если правил ещё не было.</summary>
    public string? Digest => _digest;

    /// <summary>
    /// Что добавить в контекст: null — изменений нет; иначе готовое сообщение.
    /// </summary>
    /// <param name="text">Свежий полный текст правил (null — файлов больше нет).</param>
    /// <param name="files">Имена файлов правил для человекочитаемой строки.</param>
    public string? Delta(string? text, IReadOnlyList<string>? files = null)
    {
        var current = Hash(text);
        if (current == _digest)
            return null;

        var first = _digest is null;
        _digest = current;

        if (string.IsNullOrWhiteSpace(text))
        {
            return first
                ? null
                : "[Правила проекта] Файлы правил проекта больше не найдены. Earlier project-rule instructions no longer apply.";
        }

        var list = files is { Count: > 0 } ? " (" + string.Join(", ", files) + ")" : string.Empty;
        return first
            ? text
            : "[Правила проекта] Правила изменились" + list + ". Ниже актуальная версия — прежние правила проекта больше не действуют.\n\n" + text;
    }

    /// <summary>SHA-1 нормализованного текста (как в harness — без учёта хвостовых пробелов и переводов строк).</summary>
    public static string Hash(string? text)
    {
        var normalized = (text ?? string.Empty).Replace("\r\n", "\n").Trim();
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}
