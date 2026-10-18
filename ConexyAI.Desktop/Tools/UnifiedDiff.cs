using System.Text;

namespace ConexyAI.Desktop.Tools;

// WEB_DEVICE: добавлено 2026-10-18
/// <summary>
/// Построчный unified diff (3 строки контекста) без внешних зависимостей. Общие начало и конец отрезаются,
/// середина сравнивается через LCS; если середина слишком большая для LCS — она целиком считается заменённой.
/// </summary>
public static class UnifiedDiff
{
    private const int Context = 3;
    private const long MaxLcsCells = 4_000_000;

    private enum Op { Same, Del, Add }

    public static string[] SplitLines(string text)
    {
        if (text.Length == 0)
            return [];
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return text.EndsWith('\n') ? lines[..^1] : lines;
    }

    /// <summary>Diff двух текстов; пустая строка — изменений нет.</summary>
    public static string Create(string? oldText, string? newText, string path)
    {
        var a = SplitLines(oldText ?? string.Empty);
        var b = SplitLines(newText ?? string.Empty);
        var ops = Compute(a, b);
        if (ops.All(o => o.Op == Op.Same))
            return string.Empty;

        var sb = new StringBuilder();
        sb.Append(oldText is null ? "--- /dev/null\n" : $"--- a/{path}\n");
        sb.Append(newText is null ? "+++ /dev/null\n" : $"+++ b/{path}\n");

        // Изменения, между которыми не больше 2*Context общих строк, попадают в один ханк.
        var changes = Enumerable.Range(0, ops.Count).Where(x => ops[x].Op != Op.Same).ToList();
        var c = 0;
        while (c < changes.Count)
        {
            var start = Math.Max(0, changes[c] - Context);
            var last = changes[c++];
            while (c < changes.Count && changes[c] - last <= 2 * Context + 1)
                last = changes[c++];
            var end = Math.Min(ops.Count, last + 1 + Context);

            int oldStart = ops[start].OldIndex, newStart = ops[start].NewIndex, oldCount = 0, newCount = 0;
            var body = new StringBuilder();
            for (var x = start; x < end; x++)
            {
                var (op, line, _, _) = ops[x];
                switch (op)
                {
                    case Op.Same: body.Append(' ').Append(line).Append('\n'); oldCount++; newCount++; break;
                    case Op.Del: body.Append('-').Append(line).Append('\n'); oldCount++; break;
                    case Op.Add: body.Append('+').Append(line).Append('\n'); newCount++; break;
                }
            }
            // По соглашению unified diff пустой диапазон указывает на строку перед ним.
            sb.Append($"@@ -{(oldCount == 0 ? oldStart : oldStart + 1)},{oldCount} +{(newCount == 0 ? newStart : newStart + 1)},{newCount} @@\n");
            sb.Append(body);
        }
        return sb.ToString();
    }

    // OldIndex/NewIndex — позиции (с нуля) в исходных массивах на момент операции.
    private static List<(Op Op, string Line, int OldIndex, int NewIndex)> Compute(string[] a, string[] b)
    {
        int prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) prefix++;
        int suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix]) suffix++;

        var result = new List<(Op, string, int, int)>(a.Length + b.Length);
        for (var p = 0; p < prefix; p++)
            result.Add((Op.Same, a[p], p, p));

        int n = a.Length - prefix - suffix, m = b.Length - prefix - suffix;
        if ((long)n * m <= MaxLcsCells && n > 0 && m > 0)
        {
            // lcs[i,j] — длина LCS суффиксов a[i..], b[j..] средней части.
            var lcs = new int[n + 1, m + 1];
            for (var x = n - 1; x >= 0; x--)
                for (var y = m - 1; y >= 0; y--)
                    lcs[x, y] = a[prefix + x] == b[prefix + y] ? lcs[x + 1, y + 1] + 1 : Math.Max(lcs[x + 1, y], lcs[x, y + 1]);
            int i = 0, j = 0;
            while (i < n || j < m)
            {
                if (i < n && j < m && a[prefix + i] == b[prefix + j])
                {
                    result.Add((Op.Same, a[prefix + i], prefix + i, prefix + j)); i++; j++;
                }
                else if (j < m && (i == n || lcs[i, j + 1] > lcs[i + 1, j]))
                {
                    result.Add((Op.Add, b[prefix + j], prefix + i, prefix + j)); j++;
                }
                else
                {
                    result.Add((Op.Del, a[prefix + i], prefix + i, prefix + j)); i++;
                }
            }
        }
        else
        {
            for (var x = 0; x < n; x++) result.Add((Op.Del, a[prefix + x], prefix + x, prefix));
            for (var y = 0; y < m; y++) result.Add((Op.Add, b[prefix + y], prefix + n, prefix + y));
        }

        for (var s = 0; s < suffix; s++)
            result.Add((Op.Same, a[a.Length - suffix + s], a.Length - suffix + s, b.Length - suffix + s));
        return result;
    }
}
