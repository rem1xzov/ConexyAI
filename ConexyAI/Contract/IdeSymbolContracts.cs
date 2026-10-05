namespace ConexyAI.Contract;

// LSP_LITE: добавлено 2026-10-05 — для редактора IDE: Outline (дерево символов), go-to-definition,
// hover и автодополнение по символам проекта. Полноценный языковой сервер (постоянный процесс,
// типы в реальном времени) в модели одноразовой песочницы невозможен, поэтому это облегчённый
// вариант: разбор символов и поиск объявлений по исходникам рабочей области.

/// <summary>A node of the file outline (class / method / function / …). Lines and columns are 1-based.</summary>
public sealed class IdeSymbol
{
    public string Name { get; set; } = string.Empty;

    /// <summary>class | interface | struct | enum | record | trait | module | type | method | function | property | field | variable</summary>
    public string Kind { get; set; } = "variable";

    /// <summary>The declaration line, trimmed (used as the outline tooltip / signature).</summary>
    public string? Detail { get; set; }

    public int Line { get; set; }
    public int Column { get; set; }
    public int EndLine { get; set; }
    public int EndColumn { get; set; }
    public List<IdeSymbol> Children { get; set; } = new();
}

public sealed record IdeSymbolsResult(
    bool Success,
    string? Path,
    string? Language,
    IReadOnlyList<IdeSymbol> Symbols,
    string? Error);

/// <summary>Body of an outline request; <paramref name="Content"/> is the live editor buffer.</summary>
public sealed record IdeSymbolRequest(string Path, string? Content);

public sealed record IdeDefinitionRequest(string Path, string Word, string? Content);

/// <summary>A declaration location, workspace-relative.</summary>
public sealed record IdeLocation(string Path, int Line, int Column, string Kind, string Text);

public sealed record IdeDefinitionResult(
    bool Found,
    string Word,
    IReadOnlyList<IdeLocation> Locations,
    string? Error);

public sealed record IdeHoverRequest(string Path, string Word, string? Content);

public sealed record IdeHoverResult(
    bool Found,
    string Word,
    string? Kind,
    string? Signature,
    string? Documentation,
    string? Path,
    int Line,
    string? Error);

public sealed record IdeSymbolSearchRequest(string Query, int Limit = 30);

public sealed record IdeSymbolSearchItem(string Name, string Kind, string Path, int Line, int Column);

public sealed record IdeSymbolSearchResult(bool Success, IReadOnlyList<IdeSymbolSearchItem> Symbols, string? Error);
