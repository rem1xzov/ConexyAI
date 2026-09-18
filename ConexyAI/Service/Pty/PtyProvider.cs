namespace ConexyAI.Service.Pty;

/// <summary>Spawns an interactive shell in a pseudo-terminal for the current platform.</summary>
public static class PtyProvider
{
    /// <summary>
    /// Creates a pty connection. Uses the ConPTY API on Windows and the POSIX
    /// <c>posix_openpt</c> path on Linux/macOS.
    /// </summary>
    public static IPtyConnection Spawn(PtyOptions options)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsPtyConnection.Spawn(options);
        }

        return UnixPtyConnection.Spawn(options);
    }
}
