namespace ConexyAI.Contract;

// SEARCH_REPLACE: добавлено 2026-10-05 — глобальный поиск и замена по всему проекту (как Find &
// Replace в VS Code): поиск строки или регулярного выражения во всех файлах рабочей области и
// массовая замена. Всё ограничено jail рабочей области, бинарные и слишком большие файлы пропускаются.

/// <summary>Body of a project-wide search.</summary>
public sealed record ProjectSearchRequest(
    string? Query,
    bool Regex,
    bool CaseSensitive,
    string? IncludeGlob,
    int MaxResults = 0);

/// <summary>One match: workspace-relative path, 1-based line/column, match length and the line preview.</summary>
public sealed record SearchMatch(string Path, int Line, int Column, int Length, string Preview);

public sealed record ProjectSearchResult(
    bool Success,
    IReadOnlyList<SearchMatch> Matches,
    int TotalMatches,
    int FilesSearched,
    bool Truncated,
    string? Error);

/// <summary>Body of a project-wide replace; <paramref name="Path"/> restricts it to one file.</summary>
public sealed record ProjectReplaceRequest(
    string? Query,
    string? Replacement,
    bool Regex,
    bool CaseSensitive,
    string? IncludeGlob,
    string? Path);

public sealed record ReplaceFileResult(string Path, int Count);

public sealed record ProjectReplaceResult(
    bool Success,
    int FilesChanged,
    int Replacements,
    IReadOnlyList<ReplaceFileResult> Files,
    string? Error);
