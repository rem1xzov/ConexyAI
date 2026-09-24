using System.Text.RegularExpressions;
using ConexyAI.DbContext;
using Microsoft.EntityFrameworkCore;

namespace ConexyAI.Repository;

// SEARCH_USER_CHATS: добавлено 2026-09-24
/// <summary>
/// Keyword search over the user's past chats. Every query is filtered by <c>UserId</c> first — the
/// only security boundary here — and never includes the chat the search is made from.
/// <para>
/// Matching is case-insensitive substring matching per keyword (PostgreSQL <c>ILIKE</c>; a portable
/// <c>ToLower().Contains</c> on other providers such as the in-memory one used by the tests). Long
/// Russian words are matched by their first letters so that "настройка" also finds "настройки" and
/// "настроить" — a crude stem, but full-text search would need a migration and a language guess.
/// </para>
/// </summary>
public sealed class ChatSearchRepository : IChatSearchRepository
{
    public const int DefaultChats = 5;
    public const int MaxChats = 10;

    private const int MaxTerms = 6;
    private const int RowsPerTerm = 80;
    private const int SnippetRadius = 200;
    private const int MaxSnippetsPerChat = 3;
    private const int TitleChars = 80;

    // Слова запроса, которые есть почти в любом сообщении и ничего не ищут.
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "как", "мы", "мне", "меня", "что", "это", "этот", "эта", "эти", "там", "тут", "так", "где", "когда",
        "для", "про", "при", "над", "под", "без", "или", "его", "её", "их", "был", "была", "было", "были",
        "в", "во", "на", "и", "а", "но", "с", "со", "к", "ко", "по", "из", "от", "до", "о", "об", "у", "же", "ли",
        "не", "ни", "да", "нет", "ты", "вы", "я", "он", "она", "они", "мой", "твой", "наш", "ваш",
        "прошлом", "прошлый", "прошлого", "прошлой", "прошлые", "прошлых", "раньше", "тогда",
        "недавно", "помнишь", "помню", "чат", "чате", "чата", "чатах", "чатов", "разговор", "разговоре",
        "диалог", "диалоге", "обсуждали", "говорили", "делали", "сделали", "решили", "писали",
        "the", "a", "an", "and", "or", "of", "to", "in", "on", "at", "for", "with", "how", "what", "we",
        "did", "do", "does", "was", "were", "is", "are", "our", "my", "last", "previous", "chat", "time",
    };

    private static readonly Regex TokenPattern = new(@"[\p{L}\p{N}][\p{L}\p{N}_.#+\-]*", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private readonly DbConexy _context;

    public ChatSearchRepository(DbConexy context)
    {
        _context = context;
    }

    public async Task<ChatSearchResult> SearchAsync(
        Guid userId,
        Guid excludeChatId,
        string query,
        int maxChats,
        CancellationToken ct = default)
    {
        var terms = ExtractTerms(query);
        if (terms.Count == 0)
            return new ChatSearchResult(terms, Array.Empty<ChatSearchHit>());

        var limit = Math.Clamp(maxChats <= 0 ? DefaultChats : maxChats, 1, MaxChats);
        var isNpgsql = _context.Database.IsNpgsql();

        // One bounded query per keyword keeps each statement a plain parameterised filter.
        var rows = new Dictionary<Guid, MessageRow>();
        foreach (var term in terms)
        {
            var scoped = _context.ChatMessages
                .AsNoTracking()
                .Where(m => m.UserId == userId && m.ChatId != excludeChatId);

            if (isNpgsql)
            {
                var pattern = "%" + EscapeLike(term) + "%";
                scoped = scoped.Where(m => EF.Functions.ILike(m.Content, pattern, "\\"));
            }
            else
            {
                scoped = scoped.Where(m => m.Content.ToLower().Contains(term));
            }

            var matches = await scoped
                .OrderByDescending(m => m.CreatedAt)
                .Take(RowsPerTerm)
                .Select(m => new MessageRow(m.Id, m.ChatId, m.Role, m.Content, m.CreatedAt))
                .ToListAsync(ct);

            foreach (var row in matches)
            {
                rows.TryAdd(row.Id, row);
            }
        }

        if (rows.Count == 0)
            return new ChatSearchResult(terms, Array.Empty<ChatSearchHit>());

        // Rank chats: more distinct keywords first, then more matching messages, then recency.
        var ranked = rows.Values
            .GroupBy(r => r.ChatId)
            .Select(g =>
            {
                var messages = g
                    .Select(r => (Row: r, Terms: terms.Where(t => Contains(r.Content, t)).ToList()))
                    .Where(x => x.Terms.Count > 0)
                    .ToList();
                var matched = terms.Where(t => messages.Any(x => x.Terms.Contains(t))).ToList();
                return (ChatId: g.Key, Messages: messages, Matched: matched, Newest: g.Max(r => r.CreatedAt));
            })
            .Where(c => c.Matched.Count > 0)
            .OrderByDescending(c => c.Matched.Count)
            .ThenByDescending(c => Math.Min(c.Messages.Count, 5))
            .ThenByDescending(c => c.Newest)
            .Take(limit)
            .ToList();

        var chatIds = ranked.Select(c => c.ChatId).ToList();
        var meta = await _context.ChatMessages
            .AsNoTracking()
            .Where(m => m.UserId == userId && chatIds.Contains(m.ChatId))
            .GroupBy(m => m.ChatId)
            .Select(g => new
            {
                ChatId = g.Key,
                LastActivityAt = g.Max(m => m.CreatedAt),
                Title = g.Max(m => m.Title),
                FirstUser = g.Where(m => m.Role == "user")
                    .OrderBy(m => m.CreatedAt)
                    .Select(m => m.Content)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);
        var metaById = meta.ToDictionary(m => m.ChatId);

        var hits = new List<ChatSearchHit>(ranked.Count);
        foreach (var chat in ranked)
        {
            metaById.TryGetValue(chat.ChatId, out var info);
            var title = !string.IsNullOrWhiteSpace(info?.Title)
                ? Collapse(info!.Title!)
                : Shorten(Collapse(info?.FirstUser ?? string.Empty), TitleChars);

            var snippets = chat.Messages
                .OrderByDescending(x => x.Terms.Count)
                .ThenByDescending(x => x.Row.CreatedAt)
                .Take(MaxSnippetsPerChat)
                .Select(x => new ChatSearchSnippet(x.Row.Role, x.Row.CreatedAt, Snippet(x.Row.Content, x.Terms)))
                .ToList();

            hits.Add(new ChatSearchHit(
                chat.ChatId,
                title.Length == 0 ? "Без названия" : title,
                info?.LastActivityAt ?? chat.Newest,
                chat.Matched,
                snippets));
        }

        return new ChatSearchResult(terms, hits);
    }

    /// <summary>
    /// Distinct lower-case keywords of the query, stop words removed, long words cut to a stem.
    /// Public so the tool can tell the model exactly what was searched.
    /// </summary>
    public static IReadOnlyList<string> ExtractTerms(string? query)
    {
        var terms = new List<string>();
        foreach (Match match in TokenPattern.Matches(query ?? string.Empty))
        {
            var token = match.Value.TrimEnd('.', '-', '_').ToLowerInvariant();
            if (token.Length < 2 || StopWords.Contains(token))
                continue;

            var stem = Stem(token);
            if (!terms.Contains(stem))
                terms.Add(stem);
            if (terms.Count == MaxTerms)
                break;
        }

        return terms;
    }

    private static string Stem(string token)
    {
        if (token.Any(c => c is >= 'а' and <= 'я' or 'ё'))
        {
            // Русское слоговое окончание меняется по падежам и временам: 5 первых букв длинного слова
            // находят «настройка / настройки / настроить».
            if (token.Length >= 6)
                return token[..5];
            if (token.Length == 5 && "аяоеыиуюьй".Contains(token[^1]))
                return token[..4];
            return token;
        }

        if (token.Length >= 6 && token.EndsWith("ing", StringComparison.Ordinal))
            return token[..^3];
        if (token.Length >= 5 && token.EndsWith("ed", StringComparison.Ordinal))
            return token[..^2];
        if (token.Length >= 5 && token.EndsWith('s') && !token.EndsWith("ss", StringComparison.Ordinal))
            return token[..^1];
        return token;
    }

    private static bool Contains(string content, string term) =>
        content.Contains(term, StringComparison.OrdinalIgnoreCase);

    /// <summary>±<see cref="SnippetRadius"/> characters around the first hit, whitespace collapsed.</summary>
    private static string Snippet(string content, IReadOnlyList<string> terms)
    {
        var position = terms
            .Select(t => content.IndexOf(t, StringComparison.OrdinalIgnoreCase))
            .Where(i => i >= 0)
            .DefaultIfEmpty(0)
            .Min();

        var start = Math.Max(0, position - SnippetRadius);
        var end = Math.Min(content.Length, position + SnippetRadius);
        // Do not cut a surrogate pair in half.
        if (start > 0 && char.IsLowSurrogate(content[start])) start--;
        if (end < content.Length && end > 0 && char.IsHighSurrogate(content[end - 1])) end++;

        var text = Collapse(content[start..end]);
        return (start > 0 ? "…" : string.Empty) + text + (end < content.Length ? "…" : string.Empty);
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static string Collapse(string value) => Whitespace.Replace(value, " ").Trim();

    private static string Shorten(string value, int max) =>
        value.Length <= max ? value : value[..max].TrimEnd() + "…";

    private sealed record MessageRow(Guid Id, Guid ChatId, string Role, string Content, DateTime CreatedAt);
}
