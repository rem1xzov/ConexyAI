using System.Text;

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
    public static IReadOnlyList<string> Chunk(string text, int chunkSize, int overlap)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<string>();

        if (chunkSize <= overlap) chunkSize = overlap + 1;
        if (chunkSize <= 0) chunkSize = 800;
        if (overlap < 0) overlap = 0;

        var step = chunkSize - overlap;
        if (step <= 0) step = chunkSize;

        var chunks = new List<string>();
        for (var start = 0; start < normalized.Length; start += step)
        {
            var end = Math.Min(start + chunkSize, normalized.Length);
            var chunk = normalized[start..end].Trim();
            if (chunk.Length > 0)
            {
                chunks.Add(chunk);
            }

            if (end >= normalized.Length)
                break;
        }

        return chunks;
    }

    /// <summary>Rough token estimate (~4 chars per token) for a chunk.</summary>
    public static int EstimateTokens(string content) =>
        string.IsNullOrEmpty(content) ? 0 : (content.Length + 3) / 4;
}
