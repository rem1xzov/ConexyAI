namespace ConexyAI.Service.Pty;

/// <summary>Parameters for spawning an interactive shell in a pseudo-terminal.</summary>
public sealed class PtyOptions
{
    /// <summary>Terminal type advertised to the shell (<c>$TERM</c>).</summary>
    public string Name { get; set; } = "xterm-256color";

    public int Cols { get; set; } = 80;
    public int Rows { get; set; } = 24;

    /// <summary>Absolute working directory for the shell.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Executable to launch (absolute path or resolvable via <c>PATH</c>).</summary>
    public required string App { get; set; }

    /// <summary>Arguments passed to <see cref="App"/> (excluding argv[0]).</summary>
    public IReadOnlyList<string>? CommandLine { get; set; }
}
