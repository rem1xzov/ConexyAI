using System.Text;

namespace ConexyAI.Service;

/// <summary>
/// SPILL: большой результат инструмента выносится в файл, а в контекст уходит превью и путь.
/// Перенесено из deepseek-harness (<c>packages/spill</c>): порог <c>maxInlineBytes</c> = 50 000 байт,
/// файл <c>&lt;workspace&gt;/.conexy/spill/session-&lt;hash&gt;/&lt;random&gt;-&lt;tool&gt;.txt</c>,
/// notice с подсказкой читать через read/грепать по пути. <c>read</c> не спиллится — иначе получается
/// цикл read → spill → read.
/// </summary>
/// <remarks>
/// Второй перенос — из <c>packages/compaction/compaction-tool-result-pruner</c>: у результата длиннее 8192
/// символов вырезается середина (head 4096 + маркер + tail 1024). Это снимает давление на контекст без
/// вызова суммаризатора и без потери начала (где обычно путь/заголовок) и хвоста (где ошибка).
/// </remarks>
public static class ToolResultSpill
{
    /// <summary>Порог выноса в файл (UTF-8 байты), как в harness.</summary>
    public const int MaxInlineBytes = 50_000;

    /// <summary>Сколько символов превью показывать (head + tail пополам).</summary>
    public const int PreviewChars = 8_000;

    /// <summary>С какого размера режется середина результата.</summary>
    public const int MiddlePruneChars = 8_192;

    private const int HeadChars = 4_096;
    private const int TailChars = 1_024;

    private static readonly string[] NoSpillTools = { "file_read", "file_read_bytes", "read" };

    /// <summary>Маркер вырезанной середины — дословно из harness.</summary>
    public const string PrunedMarker = "\n\n[... tool result middle pruned ...]\n\n";

    /// <summary>Итоговый текст для истории: либо хвост с превью, либо вырезанная середина.</summary>
    public static string Apply(string tool, string? output, string? workspaceRoot, Guid taskId)
    {
        if (string.IsNullOrEmpty(output))
            return output ?? string.Empty;

        if (workspaceRoot is not null
            && Encoding.UTF8.GetByteCount(output) > MaxInlineBytes
            && !NoSpillTools.Contains(tool, StringComparer.Ordinal))
        {
            return Spill(tool, output, workspaceRoot, taskId);
        }

        return PruneMiddle(output);
    }

    private static string Spill(string tool, string output, string workspaceRoot, Guid taskId)
    {
        try
        {
            var dir = Path.Combine(workspaceRoot, ".conexy", "spill", "session-" + taskId.ToString("N")[..12]);
            Directory.CreateDirectory(dir);
            var safeTool = string.Concat(tool.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
            var path = Path.Combine(dir, Guid.NewGuid().ToString("N")[..12] + "-" + safeTool + ".txt");
            File.WriteAllText(path, output, new UTF8Encoding(false));

            var half = PreviewChars / 2;
            var head = output[..Math.Min(half, output.Length)];
            var tail = output.Length > half ? output[^Math.Min(half, output.Length - half)..] : string.Empty;
            var omitted = Encoding.UTF8.GetByteCount(output);
            return head + PrunedMarker + tail +
                   $"\n\n(Omitted {omitted} bytes. Full formatted result stored at: {path}. " +
                   "Use read with offset/limit, or grep this path to search within it.)";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Нет прав/места — не теряем результат, отдаём хотя бы обрезанный хвост.
            return PruneMiddle(output);
        }
    }

    private static string PruneMiddle(string output)
    {
        if (output.Length <= MiddlePruneChars)
            return output;
        var head = output[..HeadChars];
        var tail = output[^Math.Min(TailChars, output.Length - HeadChars)..];
        return head + PrunedMarker + tail;
    }
}
