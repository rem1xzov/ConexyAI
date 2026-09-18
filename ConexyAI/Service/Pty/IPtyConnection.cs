namespace ConexyAI.Service.Pty;

/// <summary>
/// A live pseudo-terminal connection to an interactive shell.
///
/// <para>
/// The naming follows the <c>Pty.Net</c> convention: <see cref="InputStream"/> is the
/// write side (client keystrokes go in) and <see cref="OutputStream"/> is the read side
/// (raw terminal output, including ANSI escape sequences, comes out). The two streams
/// may be backed by the same underlying handle and may be read/written concurrently.
/// </para>
/// </summary>
public interface IPtyConnection : IDisposable
{
    /// <summary>Write keystrokes here.</summary>
    Stream InputStream { get; }

    /// <summary>Read raw pty output (ANSI escape codes preserved) here.</summary>
    Stream OutputStream { get; }

    /// <summary>OS process id of the shell.</summary>
    int ProcessId { get; }

    /// <summary>Exit code once the shell has terminated, otherwise <c>null</c>.</summary>
    int? ExitCode { get; }

    /// <summary>Raised when the shell process exits.</summary>
    event EventHandler? Exited;

    /// <summary>Updates the terminal window size.</summary>
    void Resize(int cols, int rows);

    /// <summary>Terminates the shell and its whole process group/tree.</summary>
    void Kill();
}
