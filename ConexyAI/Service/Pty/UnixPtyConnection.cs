using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ConexyAI.Service.Pty;

/// <summary>
/// POSIX pseudo-terminal implemented with direct P/Invoke on
/// <c>posix_openpt</c>/<c>grantpt</c>/<c>unlockpt</c>/<c>ptsname_r</c> plus
/// <c>fork</c>+<c>execv</c>. This is the fallback documented in the task spec for when a
/// managed pty package is unavailable or unstable on the Ubuntu target.
///
/// <para>
/// Every string the child touches is marshalled (allocated/pinned) before <c>fork</c> and
/// passed as a raw pointer, so the child runs only async-signal-safe operations
/// (setsid, chdir, open, dup2, close, execv) before execing the shell by absolute path.
/// </para>
/// </summary>
internal sealed class UnixPtyConnection : IPtyConnection
{
    private const int O_RDWR = 2;
    private const int O_NOCTTY = 0x100;
    private const int SIGKILL = 9;
    private const int TIOCSWINSZ = 0x5414;

    private readonly FileStream _stream;
    private readonly int _childPid;
    private int? _exitCode;
    private bool _disposed;
    private readonly object _disposeLock = new();

    public Stream InputStream => _stream;
    public Stream OutputStream => _stream;
    public int ProcessId => _childPid;
    public int? ExitCode => _exitCode;
    public event EventHandler? Exited;

    private UnixPtyConnection(int masterFd, int childPid)
    {
        _childPid = childPid;
        var handle = new SafeFileHandle((IntPtr)masterFd, ownsHandle: true);
        _stream = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 4096, isAsync: false);

        // Reap the child on a background thread so it never lingers as a zombie.
        _ = Task.Run(() =>
        {
            var status = 0;
            var rc = waitpid(_childPid, out status, 0);
            if (rc > 0)
            {
                _exitCode = status & 0x7f;
                Exited?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    public static UnixPtyConnection Spawn(PtyOptions options)
    {
        var masterFd = posix_openpt(O_RDWR | O_NOCTTY);
        if (masterFd < 0)
            throw new IOException($"posix_openpt failed (errno {Marshal.GetLastPInvokeError()}).");

        try
        {
            grantpt(masterFd);
            unlockpt(masterFd);

            var slaveBuf = new byte[256];
            if (ptsname_r(masterFd, slaveBuf, (IntPtr)slaveBuf.Length) != 0)
                throw new IOException($"ptsname_r failed (errno {Marshal.GetLastPInvokeError()}).");

            var appPtr = Allocate(options.App);
            var args = options.CommandLine ?? Array.Empty<string>();
            var argv = new IntPtr[args.Count + 2];
            var allocated = new List<IntPtr> { appPtr };

            try
            {
                argv[0] = appPtr;
                for (var i = 0; i < args.Count; i++)
                {
                    var p = Allocate(args[i]);
                    allocated.Add(p);
                    argv[i + 1] = p;
                }
                argv[args.Count + 1] = IntPtr.Zero;

                var argvHandle = GCHandle.Alloc(argv, GCHandleType.Pinned);
                var slaveHandle = GCHandle.Alloc(slaveBuf, GCHandleType.Pinned);
                var cwdPtr = options.WorkingDirectory != null
                    ? Allocate(options.WorkingDirectory)
                    : IntPtr.Zero;

                try
                {
                    var pid = fork();
                    if (pid < 0)
                        throw new IOException($"fork failed (errno {Marshal.GetLastPInvokeError()}).");

                    if (pid == 0)
                    {
                        setsid();
                        if (cwdPtr != IntPtr.Zero) chdir(cwdPtr);
                        var slaveFd = open(slaveHandle.AddrOfPinnedObject(), O_RDWR);
                        if (slaveFd >= 0)
                        {
                            dup2(slaveFd, 0);
                            dup2(slaveFd, 1);
                            dup2(slaveFd, 2);
                            if (slaveFd > 2) close(slaveFd);
                        }
                        close(masterFd);
                        execv(appPtr, argvHandle.AddrOfPinnedObject());
                        _exit(127);
                    }

                    return new UnixPtyConnection(masterFd, pid);
                }
                finally
                {
                    if (cwdPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(cwdPtr);
                    if (slaveHandle.IsAllocated) slaveHandle.Free();
                    if (argvHandle.IsAllocated) argvHandle.Free();
                }
            }
            finally
            {
                foreach (var p in allocated) Marshal.FreeCoTaskMem(p);
            }
        }
        catch
        {
            close(masterFd);
            throw;
        }
    }

    private static IntPtr Allocate(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value + "\0");
        var p = Marshal.AllocCoTaskMem(bytes.Length);
        Marshal.Copy(bytes, 0, p, bytes.Length);
        return p;
    }

    public void Resize(int cols, int rows)
    {
        var masterFd = (int)_stream.SafeFileHandle.DangerousGetHandle();
        if (masterFd < 0) return;

        var winsize = new WinSize { Row = (ushort)Math.Max(rows, 1), Col = (ushort)Math.Max(cols, 1) };
        ioctl(masterFd, TIOCSWINSZ, ref winsize);
    }

    public void Kill()
    {
        // Negative pid kills the whole process group, mirroring Process.Kill(entireProcessTree: true).
        kill(-_childPid, SIGKILL);
    }

    public void Dispose()
    {
        lock (_disposeLock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        try { Kill(); } catch { /* best effort */ }
        try { _stream.Dispose(); } catch { /* best effort */ }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinSize
    {
        public ushort Row;
        public ushort Col;
        public ushort Xpixel;
        public ushort Ypixel;
    }

    [DllImport("libc", SetLastError = true)] private static extern int posix_openpt(int flags);
    [DllImport("libc", SetLastError = true)] private static extern int grantpt(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int unlockpt(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int ptsname_r(int fd, byte[] buf, IntPtr buflen);
    [DllImport("libc", SetLastError = true)] private static extern int fork();
    [DllImport("libc", SetLastError = true)] private static extern int setsid();
    [DllImport("libc", SetLastError = true)] private static extern int chdir(IntPtr path);
    [DllImport("libc", SetLastError = true)] private static extern int open(IntPtr path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int dup2(int oldfd, int newfd);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int execv(IntPtr path, IntPtr argv);
    [DllImport("libc", SetLastError = true)] private static extern void _exit(int status);
    [DllImport("libc", SetLastError = true)] private static extern int waitpid(int pid, out int status, int options);
    [DllImport("libc", SetLastError = true)] private static extern int kill(int pid, int sig);
    [DllImport("libc", SetLastError = true)] private static extern int ioctl(int fd, int request, ref WinSize winsize);
}
