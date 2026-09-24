namespace ConexyAI.Service;

// RAG: добавлено 2026-09-17
/// <summary>Splits document text into overlapping chunks.</summary>
public static class DocumentChunker
{
    /// <summary>
    /// Chunks <paramref name="text"/> into pieces of roughly <paramref name="chunkSize"/>
    /// characters with <paramref name="overlap"/> characters of shared context between
    /// adjacent chunks. Returns each chunk's text.
    /// </summary>
    /// <remarks>
    /// TEXT_DECODING: изменено 2026-09-24 (ревью M6) — граница чанка больше не режет суррогатную пару
    /// (одиночный суррогат Npgsql не может закодировать, и вставка документа падала) и по возможности
    /// приходится на пробел или перенос строки, а не на середину слова.
    /// </remarks>
    public static IReadOnlyList<string> Chunk(string text, int chunkSize, int overlap)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<string>();

        if (chunkSize <= 0) chunkSize = 800;
        if (overlap < 0) overlap = 0;
        if (overlap >= chunkSize) overlap = chunkSize / 4;

        var chunks = new List<string>();
        var start = 0;
        while (start < normalized.Length)
        {
            var end = Math.Min(start + chunkSize, normalized.Length);
            if (end < normalized.Length)
            {
                end = BreakPoint(normalized, start, end, chunkSize);
            }

            var chunk = normalized[start..end].Trim();
            if (chunk.Length > 0)
            {
                chunks.Add(chunk);
            }

            if (end >= normalized.Length)
                break;

            // Step back by the overlap, but always move forward and never into a surrogate pair.
            var next = Math.Max(start + 1, end - overlap);
            var previous = start;
            start = SafeBoundary(normalized, next);
            if (start <= previous) start = Math.Min(normalized.Length, next + 1);
        }

        return chunks;
    }

    /// <summary>Rough token estimate (~4 chars per token) for a chunk.</summary>
    public static int EstimateTokens(string content) =>
        string.IsNullOrEmpty(content) ? 0 : (content.Length + 3) / 4;

    /// <summary>
    /// A chunk end at or before <paramref name="end"/>: the last line break or space in the final fifth of
    /// the chunk, otherwise <paramref name="end"/> itself — moved off the middle of a surrogate pair.
    /// </summary>
    private static int BreakPoint(string text, int start, int end, int chunkSize)
    {
        var floor = start + chunkSize * 4 / 5;
        var newline = text.LastIndexOf('\n', end - 1, Math.Max(0, end - floor));
        if (newline > start) return newline + 1;
        var space = text.LastIndexOf(' ', end - 1, Math.Max(0, end - floor));
        if (space > start) return space + 1;
        return SafeBoundary(text, end);
    }

    private static int SafeBoundary(string text, int index)
    {
        if (index <= 0 || index >= text.Length) return index;
        return char.IsLowSurrogate(text[index]) && char.IsHighSurrogate(text[index - 1]) ? index - 1 : index;
    }
}
