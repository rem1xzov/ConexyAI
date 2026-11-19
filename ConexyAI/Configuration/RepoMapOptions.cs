namespace ConexyAI.Configuration;

// REPO_MAP: добавлено 2026-11-19 — карта проекта строится harness'ом и отдаётся агенту, чтобы он
// тратил меньше токенов на блуждание по файлам. Все пределы — здесь, а не константами в коде.
/// <summary>Configuration of the generated repository map (<c>RepoMap</c> section).</summary>
public class RepoMapOptions
{
    public const string SectionName = "RepoMap";

    /// <summary>Whether the map is built and injected at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Hard cap of the injected map block (characters, ~4 chars per token).</summary>
    public int MaxChars { get; set; } = 10_000;

    /// <summary>Default cap of one <c>repo_map</c> tool answer.</summary>
    public int QueryMaxChars { get; set; } = 6_000;

    /// <summary>Files above this count are not indexed at all (the map reports the cap).</summary>
    public int MaxFiles { get; set; } = 5_000;

    /// <summary>How many source files get symbol parsing (the rest are listed by name only).</summary>
    public int MaxSymbolFiles { get; set; } = 150;

    /// <summary>Symbols kept per file in the map.</summary>
    public int MaxSymbolsPerFile { get; set; } = 12;

    /// <summary>How many leading characters of one file are parsed for symbols.</summary>
    public int SymbolReadChars { get; set; } = 6_000;

    /// <summary>Minimum interval between two rebuilds when nothing reported a change.</summary>
    public int RebuildSeconds { get; set; } = 120;

    /// <summary>Directory names never indexed (matched as a path segment, case-insensitive).</summary>
    public string[] ExcludeDirectories { get; set; } =
    [
        "node_modules", "bin", "obj", "dist", "build", "publish", "coverage", ".git",
        ".idea", ".vs", "__pycache__", ".venv", "venv", "target", "vendor", ".next",
        ".nuxt", ".output", ".gradle", ".dart_tool", "Pods", ".terraform",
    ];
}
