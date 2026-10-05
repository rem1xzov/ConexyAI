using System.Text.RegularExpressions;
using ConexyAI.Contract;

namespace ConexyAI.Service;

// LSP_LITE: добавлено 2026-10-05 — облегчённый «языковой сервер» для IDE: дерево символов файла
// (Outline), переход к объявлению (go-to-definition), hover с сигнатурой/доком и подсказки по
// символам проекта. Работает поверх исходников рабочей области (grep) и лёгкого регекс-парсера,
// без постоянного процесса языкового сервера — он невозможен в одноразовой песочнице и выполнял бы
// код рабочей области на хосте.
public interface ISymbolService
{
    Task<IdeSymbolsResult> GetDocumentSymbolsAsync(Guid chatId, string path, string? content, CancellationToken ct = default);

    Task<IdeDefinitionResult> FindDefinitionsAsync(Guid chatId, string path, string word, string? content, CancellationToken ct = default);

    Task<IdeHoverResult> GetHoverAsync(Guid chatId, string path, string word, string? content, CancellationToken ct = default);

    Task<IdeSymbolSearchResult> SearchSymbolsAsync(Guid chatId, string query, int limit, CancellationToken ct = default);
}

public class ConexySymbolService : ISymbolService
{
    private readonly IConexyWorkspaceService _workspace;

    public ConexySymbolService(IConexyWorkspaceService workspace)
    {
        _workspace = workspace;
    }

    public async Task<IdeSymbolsResult> GetDocumentSymbolsAsync(Guid chatId, string path, string? content, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new IdeSymbolsResult(false, path, null, Array.Empty<IdeSymbol>(), "Path is required.");

        var text = content;
        if (text is null)
        {
            text = await ReadFileAsync(chatId, path, ct);
            if (text is null)
                return new IdeSymbolsResult(false, path, null, Array.Empty<IdeSymbol>(), "File not found.");
        }

        var language = SourceSymbolParser.DetectLanguage(path);
        var symbols = SourceSymbolParser.Parse(path, text);
        return new IdeSymbolsResult(true, path, language, symbols, null);
    }

    public async Task<IdeDefinitionResult> FindDefinitionsAsync(Guid chatId, string path, string word, string? content, CancellationToken ct = default)
    {
        word = word?.Trim() ?? string.Empty;
        if (!SourceSymbolParser.IsSearchable(word))
            return new IdeDefinitionResult(false, word, Array.Empty<IdeLocation>(), null);

        var found = new List<(IdeLocation Location, int Priority)>();

        // The live buffer first: unsaved declarations are found even before a Ctrl+S.
        var buffer = content ?? await ReadFileAsync(chatId, path, ct);
        if (!string.IsNullOrEmpty(buffer))
        {
            foreach (var match in SourceSymbolParser.FindInContent(path, buffer, word))
                found.Add((match.Location, match.Priority));
        }

        // Then the rest of the workspace, with the current file ranked first.
        var grep = await _workspace.GrepAsync(chatId, SourceSymbolParser.BuildGrepPattern(word), maxResults: 200, ct: ct);
        if (grep.Success)
        {
            foreach (var m in grep.Matches)
            {
                var decl = SourceSymbolParser.MatchDeclaration(m.Text, word, exact: true);
                if (decl is null) continue;
                found.Add((new IdeLocation(
                    m.Path.Replace('\\', '/'),
                    m.Line,
                    decl.Value.Column,
                    decl.Value.Kind,
                    m.Text.Trim()),
                    decl.Value.Priority));
            }
        }

        var sameFile = (path ?? string.Empty).Replace('\\', '/');
        var locations = found
            .GroupBy(f => (f.Location.Path, f.Location.Line))
            .Select(g => g.First())
            .OrderBy(f => string.Equals(f.Location.Path, sameFile, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(f => f.Priority)
            .ThenBy(f => f.Location.Line)
            .Take(12)
            .Select(f => f.Location)
            .ToList();

        return new IdeDefinitionResult(locations.Count > 0, word, locations, null);
    }

    public async Task<IdeHoverResult> GetHoverAsync(Guid chatId, string path, string word, string? content, CancellationToken ct = default)
    {
        var definition = await FindDefinitionsAsync(chatId, path, word, content, ct);
        if (!definition.Found || definition.Locations.Count == 0)
            return new IdeHoverResult(false, word, null, null, null, null, 0, null);

        var best = definition.Locations[0];
        var signature = StripTrailingBody(best.Text);

        string? documentation = null;
        string? source;
        if (string.Equals(best.Path, (path ?? string.Empty).Replace('\\', '/'), StringComparison.OrdinalIgnoreCase) && content is not null)
            source = content;
        else
            source = await ReadFileAsync(chatId, best.Path, ct);

        if (!string.IsNullOrEmpty(source))
            documentation = SourceSymbolParser.ExtractDocumentation(source, best.Line);

        return new IdeHoverResult(true, word, best.Kind, signature, documentation, best.Path, best.Line, null);
    }

    public async Task<IdeSymbolSearchResult> SearchSymbolsAsync(Guid chatId, string query, int limit, CancellationToken ct = default)
    {
        query = query?.Trim() ?? string.Empty;
        if (!SourceSymbolParser.IsSearchable(query))
            return new IdeSymbolSearchResult(true, Array.Empty<IdeSymbolSearchItem>(), null);

        limit = Math.Clamp(limit <= 0 ? 30 : limit, 1, 100);
        var grep = await _workspace.GrepAsync(chatId, SourceSymbolParser.BuildGrepPattern(query, prefix: true), maxResults: limit * 6, ct: ct);
        if (!grep.Success)
            return new IdeSymbolSearchResult(false, Array.Empty<IdeSymbolSearchItem>(), grep.Error);

        var items = new List<IdeSymbolSearchItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in grep.Matches)
        {
            var decl = SourceSymbolParser.MatchDeclaration(m.Text, query, exact: false);
            if (decl is null) continue;
            var path = m.Path.Replace('\\', '/');
            if (!seen.Add($"{decl.Value.Name}\u0000{path}\u0000{m.Line}")) continue;
            items.Add(new IdeSymbolSearchItem(decl.Value.Name, decl.Value.Kind, path, m.Line, decl.Value.Column));
            if (items.Count >= limit * 2) break;
        }

        var ordered = items
            .OrderBy(i => string.Equals(i.Name, query, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(i => i.Name.Length)
            .ThenBy(i => i.Name, StringComparer.Ordinal)
            .Take(limit)
            .ToList();

        return new IdeSymbolSearchResult(true, ordered, null);
    }

    private async Task<string?> ReadFileAsync(Guid chatId, string path, CancellationToken ct)
    {
        var read = await _workspace.ReadFileAsync(chatId, path, ct);
        return read.Success ? read.Content : null;
    }

    private static string StripTrailingBody(string text)
    {
        var trimmed = text.Trim();
        foreach (var marker in new[] { " {", " =>", "=> {" })
        {
            var index = trimmed.IndexOf(marker, StringComparison.Ordinal);
            if (index > 0) trimmed = trimmed[..index].TrimEnd();
        }
        while (trimmed.EndsWith('{')) trimmed = trimmed[..^1].TrimEnd();
        return trimmed.Length > 240 ? trimmed[..240] + "…" : trimmed;
    }
}

/// <summary>
/// Language-agnostic, line-based declaration parser. It recognises the common declaration shapes
/// (types, functions, methods, variables) with a priority-ordered regex set and nests symbols by
/// indentation, which is enough for a project outline without a real language server.
/// </summary>
public static class SourceSymbolParser
{
    // Common modifiers/keywords that may precede a declaration name.
    private const string Modifiers =
        "public|private|protected|internal|static|virtual|override|abstract|sealed|async|extern|unsafe|" +
        "partial|new|final|synchronized|native|readonly|export|declare|default|pub|open|required|file|const|volatile";
    private const string TypeKeywords = "class|interface|struct|enum|record|trait|type|namespace|module|union";
    private const string FuncKeywords = "function|func|fn|def|sub|fun";
    private const string VarKeywords = "const|let|var|val";

    // Control-flow words that must never be mistaken for method/function names in the bare `name() {` shape.
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "if", "for", "while", "switch", "catch", "foreach", "do", "else", "return", "new", "typeof", "using",
        "lock", "when", "with", "case", "default", "try", "finally", "await", "throw", "assert", "print",
        "super", "this", "base", "match", "yield", "unchecked", "checked", "sizeof", "nameof", "defer",
    };

    private sealed record Spec(Regex Regex, string Kind, int Priority);

    /// <summary>Outline patterns with a capture-all name group, ordered by priority.</summary>
    private static readonly Lazy<Spec[]> OutlineSpecs = new(() => Compile(string.Empty, prefix: false, captureAll: true));

    private static readonly Regex IdentifierRegex = new(@"^[A-Za-z_$][A-Za-z0-9_$]*$", RegexOptions.Compiled);

    // Directives that resemble a `type name;` declaration but define nothing.
    private static readonly string[] DirectivePrefixes =
    {
        "using ", "using(", "global using ", "import ", "from ", "require ", "require(",
        "include ", "#include", "#region", "#endregion", "#pragma", "#using", "#import",
        "package ", "use ", "typedef ", "extern ", "export {", "export *", "export type {",
    };

    private readonly record struct PatternSpec(string Pattern, string Kind, int Priority);

    /// <summary>The declaration shapes, with a <c>%N%</c> token standing in for the name expression.</summary>
    private static IReadOnlyList<PatternSpec> PatternSpecs() => new[]
    {
        // 0 — type declarations (class/interface/struct/enum/record/trait/type/namespace/module/union).
        new PatternSpec($@"^[ \t]*(?:\[[^\]]*\][ \t]*)*(?:(?:{Modifiers})[ \t]+)*(?<kw>{TypeKeywords})[ \t]+%N%\b", "{kw}", 0),
        // Go style: `type Name struct|interface`.
        new PatternSpec($@"^[ \t]*type[ \t]+%N%[ \t]+(?<kw>struct|interface)\b", "{kw}", 0),
        // Rust impl blocks.
        new PatternSpec($@"^[ \t]*impl(?:<[^>]*>)?[ \t]+(?:[\w:<>]+[ \t]+for[ \t]+)?%N%", "class", 0),
        // 1 — keyword functions (function/func/fn/def/sub/fun).
        new PatternSpec($@"^[ \t]*(?:(?:{Modifiers})[ \t]+)*(?<kw>{FuncKeywords})[ \t]+\*?%N%\b", "function", 1),
        // 2 — typed method/function: return type before the name.
        new PatternSpec($@"^[ \t]*(?:(?:{Modifiers})[ \t]+)*[\w<>\[\]?.,]+[ \t]+%N%[ \t]*\(", "method", 2),
        // 3 — assignment to a function/arrow.
        new PatternSpec($@"^[ \t]*(?:(?:{Modifiers})[ \t]+)*%N%[ \t]*(?::[^=]+)?=[ \t]*(?:async[ \t]+)?(?:function\b|\([^)]*\)[ \t]*(?::[^=]+)?=>)", "function", 3),
        // 4 — variable keywords.
        new PatternSpec($@"^[ \t]*(?:(?:{Modifiers})[ \t]+)*(?<kw>{VarKeywords})[ \t]+%N%\b", "variable", 4),
        // 5 — field: type name =/;/:
        new PatternSpec($@"^[ \t]*(?:(?:{Modifiers})[ \t]+)*[\w<>\[\]?.,]+[ \t]+%N%[ \t]*[=;:]", "field", 5),
        // 6 — bare method/constructor: name(...) {|=>.
        new PatternSpec($@"^[ \t]*(?:(?:{Modifiers})[ \t]+)*%N%[ \t]*\([^)]*\)[ \t]*(?::[^={{]+)?(?:=>|\{{)", "method", 6),
    };

    private static string NameExpression(string name, bool prefix, bool captureAll) => captureAll
        ? @"(?<n>\w+)"
        : $@"(?<n>{Regex.Escape(name)}{(prefix ? @"\w*" : string.Empty)})";

    private static Spec[] Compile(string name, bool prefix, bool captureAll)
    {
        var nameExpr = NameExpression(name, prefix, captureAll);
        return PatternSpecs()
            .Select(s => new Spec(
                new Regex(s.Pattern.Replace("%N%", nameExpr), RegexOptions.Compiled, TimeSpan.FromSeconds(2)),
                s.Kind,
                s.Priority))
            .OrderBy(s => s.Priority)
            .ToArray();
    }

    /// <summary>Builds one alternation of exact-word (or prefix) declaration patterns for a single grep pass.</summary>
    public static string BuildGrepPattern(string name, bool prefix = false)
    {
        var nameExpr = NameExpression(name, prefix, captureAll: false);
        return string.Join("|", PatternSpecs().Select(s => "(?:" + s.Pattern.Replace("%N%", nameExpr) + ")"));
    }

    public static bool IsSearchable(string word) =>
        !string.IsNullOrEmpty(word) && word.Length >= 2 && word.Length <= 80 && IdentifierRegex.IsMatch(word);

    public static string DetectLanguage(string path)
    {
        var ext = Path.GetExtension(path ?? string.Empty).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "cs" => "csharp",
            "ts" or "tsx" or "mts" or "cts" => "typescript",
            "js" or "jsx" or "mjs" or "cjs" => "javascript",
            "py" or "pyi" => "python",
            "go" => "go",
            "rs" => "rust",
            "php" => "php",
            "rb" => "ruby",
            "java" => "java",
            _ => "generic",
        };
    }

    public static IReadOnlyList<IdeSymbol> Parse(string path, string content)
    {
        var lines = SplitLines(content);
        var flat = new List<(IdeSymbol Symbol, int Indent)>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            var decl = MatchDeclaration(line, _ => true);
            if (decl is null) continue;

            flat.Add((new IdeSymbol
            {
                Name = decl.Value.Name,
                Kind = decl.Value.Kind,
                Detail = TrimDetail(line),
                Line = i + 1,
                Column = decl.Value.Column,
                EndLine = i + 1,
                EndColumn = decl.Value.Column + decl.Value.Name.Length,
            }, CountIndent(line)));
        }

        var roots = new List<IdeSymbol>();
        var stack = new List<(int Indent, IdeSymbol Node)>();
        foreach (var (symbol, indent) in flat)
        {
            while (stack.Count > 0 && stack[^1].Indent >= indent)
                stack.RemoveAt(stack.Count - 1);
            if (stack.Count > 0)
                stack[^1].Node.Children.Add(symbol);
            else
                roots.Add(symbol);
            stack.Add((indent, symbol));
        }

        foreach (var root in roots)
            ComputeEndLine(root);

        return roots;
    }

    /// <summary>Declarations of <paramref name="word"/> inside the given buffer, with ranking priority.</summary>
    public static IEnumerable<(IdeLocation Location, int Priority)> FindInContent(string path, string content, string word)
    {
        var lines = SplitLines(content);
        for (var i = 0; i < lines.Length; i++)
        {
            var decl = MatchDeclaration(lines[i], name => string.Equals(name, word, StringComparison.Ordinal));
            if (decl is null) continue;
            yield return (new IdeLocation(path.Replace('\\', '/'), i + 1, decl.Value.Column, decl.Value.Kind, lines[i].Trim()), decl.Value.Priority);
        }
    }

    /// <summary>Extracts a declaration from a single line; <paramref name="exact"/> matches the whole name, otherwise a prefix.</summary>
    public static IdeDeclaration? MatchDeclaration(string line, string name, bool exact) =>
        MatchDeclaration(line, candidate => exact
            ? string.Equals(candidate, name, StringComparison.Ordinal)
            : candidate.StartsWith(name, StringComparison.Ordinal));

    private static IdeDeclaration? MatchDeclaration(string line, Func<string, bool> nameFilter)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var trimmed = line.TrimStart();
        foreach (var prefix in DirectivePrefixes)
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal)) return null;

        foreach (var spec in OutlineSpecs.Value)
        {
            Match match;
            try
            {
                match = spec.Regex.Match(line);
            }
            catch (RegexMatchTimeoutException)
            {
                continue;
            }

            if (!match.Success) continue;
            var name = match.Groups["n"].Value;
            if (name.Length == 0 || Reserved.Contains(name)) continue;
            if (!nameFilter(name)) continue;

            return new IdeDeclaration(name, ResolveKind(spec.Kind, match), match.Groups["n"].Index + 1, spec.Priority);
        }

        return null;
    }

    /// <summary>Pulls the comment block directly above <paramref name="line"/> (1-based) as hover documentation.</summary>
    public static string? ExtractDocumentation(string content, int line)
    {
        var lines = SplitLines(content);
        if (line < 2 || line > lines.Length) return null;

        var collected = new List<string>();
        for (var i = line - 2; i >= 0 && collected.Count < 12; i--)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0) break;
            if (!IsComment(trimmed)) break;
            var cleaned = CleanComment(trimmed);
            if (cleaned.Length > 0) collected.Insert(0, cleaned);
        }

        if (collected.Count == 0) return null;
        var text = string.Join('\n', collected).Trim();
        return text.Length > 600 ? text[..600] + "…" : text;
    }

    private static string ResolveKind(string kind, Match match)
    {
        if (kind != "{kw}" || !match.Groups["kw"].Success) return kind;
        return match.Groups["kw"].Value.ToLowerInvariant() switch
        {
            "record" => "class",
            "trait" => "interface",
            "namespace" or "module" => "module",
            "union" => "struct",
            "class" or "interface" or "struct" or "enum" or "type" => match.Groups["kw"].Value.ToLowerInvariant(),
            _ => "symbol",
        };
    }

    private static int ComputeEndLine(IdeSymbol symbol)
    {
        var end = symbol.Line;
        foreach (var child in symbol.Children)
            end = Math.Max(end, ComputeEndLine(child));
        symbol.EndLine = Math.Max(end, symbol.EndLine);
        return symbol.EndLine;
    }

    private static int CountIndent(string line)
    {
        var count = 0;
        foreach (var ch in line)
        {
            if (ch == ' ' || ch == '\t') count++;
            else break;
        }
        return count;
    }

    private static string TrimDetail(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length > 200 ? trimmed[..200] + "…" : trimmed;
    }

    private static string[] SplitLines(string? content) =>
        (content ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static bool IsComment(string trimmed) =>
        trimmed.StartsWith("//", StringComparison.Ordinal) ||
        trimmed.StartsWith("/*", StringComparison.Ordinal) ||
        trimmed.StartsWith("*", StringComparison.Ordinal) ||
        trimmed.StartsWith("#", StringComparison.Ordinal) ||
        trimmed.StartsWith("--", StringComparison.Ordinal) ||
        trimmed.StartsWith("\"\"\"", StringComparison.Ordinal) ||
        trimmed.StartsWith("'''", StringComparison.Ordinal);

    private static string CleanComment(string trimmed)
    {
        if (trimmed.StartsWith("///", StringComparison.Ordinal)) trimmed = trimmed[3..];
        else if (trimmed.StartsWith("//", StringComparison.Ordinal)) trimmed = trimmed[2..];
        else if (trimmed.StartsWith("/*", StringComparison.Ordinal)) trimmed = trimmed[2..];
        else if (trimmed.StartsWith("*/", StringComparison.Ordinal)) trimmed = trimmed[2..];
        else if (trimmed.StartsWith("*", StringComparison.Ordinal)) trimmed = trimmed[1..];
        else if (trimmed.StartsWith("#", StringComparison.Ordinal)) trimmed = trimmed[1..];
        else if (trimmed.StartsWith("--", StringComparison.Ordinal)) trimmed = trimmed[2..];
        return trimmed.Trim();
    }
}

/// <summary>A declaration found on a single source line.</summary>
public readonly record struct IdeDeclaration(string Name, string Kind, int Column, int Priority);
