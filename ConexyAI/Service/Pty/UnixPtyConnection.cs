using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ConexyAI.Service.Pty;

/// <summary>
/// POSIX pseudo-terminal implemented with direct P/Invoke on
/// <c>posix_openpt</c>/<c>grantpt</c>/<c>unlockpt</c>/<c>ptsname_r</c> plus
/// <c>posix_spawn</c>. This is the fallback documented in the task spec for when a
/// managed pty package is unavailable or unstable on the Ubuntu target.
///
/// <para>
/// RUN_CRASH: исправлено 2026-09-23. Раньше здесь был <c>fork()</c> из управляемого кода, и он
/// ронял ВЕСЬ процесс бэкенда по SIGSEGV (Cloudflare отвечал 502 на «Запустить», обрывались
/// SignalR и все идущие задачи). В .NET 7+ по умолчанию включён W^X: память под JIT-код отображена
/// как MAP_SHARED, поэтому после fork она ОБЩАЯ у родителя и потомка. Потомок продолжал исполнять
/// управляемый код (заглушки P/Invoke для setsid/chdir/open/... компилировались JIT-ом прямо в нём)
/// и писал в общую исполняемую память — у родителя это портило код. На рантайме 9.0.20 падение
/// воспроизводилось в 9 запусках из 10, с <c>DOTNET_EnableWriteXorExecute=0</c> — ни разу.
/// </para>
/// <para>
/// <c>posix_spawn</c> делает clone+exec внутри glibc: между ними не исполняется ни одной
/// инструкции управляемого кода, поэтому общей памяти рантайма потомок не касается. setsid,
/// переход в рабочую директорию и перенаправление stdio на slave-сторону pty выполняет сама
/// glibc (атрибуты и file actions), в том же порядке, в каком их делал старый код.
/// </para>
/// </summary>
internal sealed class UnixPtyConnection : IPtyConnection
{
    private const int O_RDWR = 2;
    private const int O_NOCTTY = 0x100;
    // Master fd must not leak into the shells of OTHER sessions spawned later.
    private const int O_CLOEXEC = 0x80000;
    private const int SIGKILL = 9;
    private const int TIOCSWINSZ = 0x5414;

    // glibc <spawn.h>
    private const short POSIX_SPAWN_SETSIGDEF = 0x04;
    private const short POSIX_SPAWN_SETSIGMASK = 0x08;
    private const short POSIX_SPAWN_SETSID = 0x80;

    // Opaque glibc structs, allocated generously: posix_spawn_file_actions_t is 80 bytes,
    // posix_spawnattr_t 336 and sigset_t 128 on x86_64/arm64.
    private const int SpawnStructSize = 1024;

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
        var masterFd = posix_openpt(O_RDWR | O_NOCTTY | O_CLOEXEC);
        if (masterFd < 0)
            throw new IOException($"posix_openpt failed (errno {Marshal.GetLastPInvokeError()}).");

        var allocated = new List<IntPtr>();
        var fileActions = IntPtr.Zero;
        var attributes = IntPtr.Zero;

        try
        {
            grantpt(masterFd);
            unlockpt(masterFd);

            var slaveBuf = new byte[256];
            if (ptsname_r(masterFd, slaveBuf, (IntPtr)slaveBuf.Length) != 0)
                throw new IOException($"ptsname_r failed (errno {Marshal.GetLastPInvokeError()}).");
            var slavePath = Encoding.UTF8.GetString(slaveBuf, 0, Array.IndexOf(slaveBuf, (byte)0));

            var appPtr = Allocate(options.App, allocated);
            var argv = BuildNullTerminated(
                new[] { options.App }.Concat(options.CommandLine ?? Array.Empty<string>()),
                allocated);
            var envp = BuildNullTerminated(BuildEnvironment(options), allocated);

            fileActions = AllocateZeroed(allocated);
            attributes = AllocateZeroed(allocated);
            Check(posix_spawn_file_actions_init(fileActions), "posix_spawn_file_actions_init");
            Check(posix_spawnattr_init(attributes), "posix_spawnattr_init");

            // Same steps the old child performed by hand, now executed by glibc in the child:
            // new session → cwd → slave pty as stdin/stdout/stderr (opening it as a session leader
            // makes it the controlling terminal, so Ctrl+C reaches the foreground job).
            if (options.WorkingDirectory != null)
            {
                Check(
                    posix_spawn_file_actions_addchdir_np(fileActions, Allocate(options.WorkingDirectory, allocated)),
                    "posix_spawn_file_actions_addchdir_np");
            }
            Check(posix_spawn_file_actions_addopen(fileActions, 0, Allocate(slavePath, allocated), O_RDWR, 0),
                "posix_spawn_file_actions_addopen");
            Check(posix_spawn_file_actions_adddup2(fileActions, 0, 1), "posix_spawn_file_actions_adddup2");
            Check(posix_spawn_file_actions_adddup2(fileActions, 0, 2), "posix_spawn_file_actions_adddup2");

            // The runtime ignores SIGPIPE and may block signals on this thread; an interactive
            // shell must start with default dispositions and an empty mask.
            var defaultSignals = AllocateZeroed(allocated);
            var emptyMask = AllocateZeroed(allocated);
            sigfillset(defaultSignals);
            sigemptyset(emptyMask);
            Check(posix_spawnattr_setsigdefault(attributes, defaultSignals), "posix_spawnattr_setsigdefault");
            Check(posix_spawnattr_setsigmask(attributes, emptyMask), "posix_spawnattr_setsigmask");
            Check(posix_spawnattr_setflags(attributes, POSIX_SPAWN_SETSID | POSIX_SPAWN_SETSIGDEF | POSIX_SPAWN_SETSIGMASK),
                "posix_spawnattr_setflags");

            Check(posix_spawn(out var pid, appPtr, fileActions, attributes, argv, envp), "posix_spawn");
            return new UnixPtyConnection(masterFd, pid);
        }
        catch
        {
            close(masterFd);
            throw;
        }
        finally
        {
            if (fileActions != IntPtr.Zero) posix_spawn_file_actions_destroy(fileActions);
            if (attributes != IntPtr.Zero) posix_spawnattr_destroy(attributes);
            foreach (var p in allocated) Marshal.FreeHGlobal(p);
        }
    }

    /// <summary>The parent's environment plus TERM, which a shell needs for line editing.</summary>
    private static IEnumerable<string> BuildEnvironment(PtyOptions options)
    {
        var environment = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? string.Empty, StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(options.Name))
        {
            environment["TERM"] = options.Name;
        }

        return environment.Select(e => $"{e.Key}={e.Value}");
    }

    private static IntPtr BuildNullTerminated(IEnumerable<string> values, List<IntPtr> allocated)
    {
        var pointers = values.Select(v => Allocate(v, allocated)).ToList();
        var array = Marshal.AllocHGlobal(IntPtr.Size * (pointers.Count + 1));
        allocated.Add(array);
        for (var i = 0; i < pointers.Count; i++)
        {
            Marshal.WriteIntPtr(array, i * IntPtr.Size, pointers[i]);
        }
        Marshal.WriteIntPtr(array, pointers.Count * IntPtr.Size, IntPtr.Zero);
        return array;
    }

    private static IntPtr Allocate(string value, List<IntPtr> allocated)
    {
        var bytes = Encoding.UTF8.GetBytes(value + "\0");
        var p = Marshal.AllocHGlobal(bytes.Length);
        allocated.Add(p);
        Marshal.Copy(bytes, 0, p, bytes.Length);
        return p;
    }

    private static IntPtr AllocateZeroed(List<IntPtr> allocated)
    {
        var p = Marshal.AllocHGlobal(SpawnStructSize);
        allocated.Add(p);
        Marshal.Copy(new byte[SpawnStructSize], 0, p, SpawnStructSize);
        return p;
    }

    /// <summary>posix_spawn* return the error number directly instead of setting errno.</summary>
    private static void Check(int rc, string call)
    {
        if (rc != 0)
            throw new IOException($"{call} failed (errno {rc}).");
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
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
    [DllImport("libc")] private static extern int posix_spawn(out int pid, IntPtr path, IntPtr fileActions, IntPtr attributes, IntPtr argv, IntPtr envp);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_init(IntPtr fileActions);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_destroy(IntPtr fileActions);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_addopen(IntPtr fileActions, int fd, IntPtr path, int flags, uint mode);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_adddup2(IntPtr fileActions, int fd, int newFd);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_addchdir_np(IntPtr fileActions, IntPtr path);
    [DllImport("libc")] private static extern int posix_spawnattr_init(IntPtr attributes);
    [DllImport("libc")] private static extern int posix_spawnattr_destroy(IntPtr attributes);
    [DllImport("libc")] private static extern int posix_spawnattr_setflags(IntPtr attributes, short flags);
    [DllImport("libc")] private static extern int posix_spawnattr_setsigdefault(IntPtr attributes, IntPtr signals);
    [DllImport("libc")] private static extern int posix_spawnattr_setsigmask(IntPtr attributes, IntPtr signals);
    [DllImport("libc")] private static extern int sigfillset(IntPtr set);
    [DllImport("libc")] private static extern int sigemptyset(IntPtr set);
    [DllImport("libc", SetLastError = true)] private static extern int waitpid(int pid, out int status, int options);
    [DllImport("libc", SetLastError = true)] private static extern int kill(int pid, int sig);
    [DllImport("libc", SetLastError = true)] private static extern int ioctl(int fd, int request, ref WinSize winsize);
}
