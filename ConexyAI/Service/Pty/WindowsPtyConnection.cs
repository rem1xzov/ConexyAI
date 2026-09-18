using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ConexyAI.Service.Pty;

/// <summary>
/// Windows pseudo-terminal backed by the ConPTY API (<c>CreatePseudoConsole</c> +
/// <c>CreateProcess</c> with the pseudoconsole startup attribute).
/// </summary>
internal sealed class WindowsPtyConnection : IPtyConnection
{
    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x20016;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;

    private readonly FileStream _input;   // write keystrokes here
    private readonly FileStream _output;  // read shell output here
    private readonly IntPtr _hpc;
    private readonly SafeProcessHandle _process;
    private readonly int _processId;
    private int? _exitCode;
    private bool _disposed;
    private readonly object _disposeLock = new();

    public Stream InputStream => _input;
    public Stream OutputStream => _output;
    public int ProcessId => _processId;
    public int? ExitCode => _exitCode;
    public event EventHandler? Exited;

    private WindowsPtyConnection(
        FileStream input,
        FileStream output,
        IntPtr hpc,
        SafeProcessHandle process,
        int processId)
    {
        _input = input;
        _output = output;
        _hpc = hpc;
        _process = process;
        _processId = processId;

        _ = Task.Run(() =>
        {
            WaitForSingleObject(_process, 0xFFFFFFFF);
            if (GetExitCodeProcess(_process, out var code))
            {
                _exitCode = (int)code;
                Exited?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    public static WindowsPtyConnection Spawn(PtyOptions options)
    {
        var (hPtyInputRead, hPtyInputWrite) = CreatePipe();
        var (hPtyOutputRead, hPtyOutputWrite) = CreatePipe();

        IntPtr hpc = IntPtr.Zero;
        SafeProcessHandle process;
        int processId;
        try
        {
            var size = new Coord { X = (short)Math.Max(options.Cols, 1), Y = (short)Math.Max(options.Rows, 1) };

            // TEMP diagnostic: log every ConPTY Win32 return value. Remove after debugging.
            var cpcHresult = CreatePseudoConsole(size, hPtyInputRead, hPtyOutputWrite, 0, out hpc);
            var cpcError = Marshal.GetLastPInvokeError();
            Console.WriteLine($"[ConPTY-DEBUG] CreatePseudoConsole HRESULT=0x{cpcHresult:X8} ({(cpcHresult == 0 ? "S_OK" : "FAIL")}) lastError={cpcError} hpc=0x{hpc.ToInt64():X}");
            if (cpcHresult != 0)
                throw new Win32Exception(cpcError, "CreatePseudoConsole failed.");

            // Build the pseudoconsole startup attribute list.
            var attrSize = IntPtr.Zero;
            var sizeQueryOk = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrSize);
            var sizeQueryError = Marshal.GetLastPInvokeError();
            Console.WriteLine($"[ConPTY-DEBUG] InitProcThreadAttrList(size-query) ok={sizeQueryOk} requiredSize={attrSize.ToInt64()} lastError={sizeQueryError}");
            var attrList = Marshal.AllocHGlobal(attrSize);
            try
            {
                var initOk = InitializeProcThreadAttributeList(attrList, 1, 0, ref attrSize);
                var initError = Marshal.GetLastPInvokeError();
                Console.WriteLine($"[ConPTY-DEBUG] InitProcThreadAttrList(init) ok={initOk} lastError={initError}");
                if (!initOk)
                    throw new Win32Exception(initError, "InitializeProcThreadAttributeList failed.");

                var hpcValue = GCHandle.Alloc(hpc, GCHandleType.Pinned);
                try
                {
                    var updateOk = UpdateProcThreadAttribute(attrList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                            hpcValue.AddrOfPinnedObject(), (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero);
                    var updateError = Marshal.GetLastPInvokeError();
                    Console.WriteLine($"[ConPTY-DEBUG] UpdateProcThreadAttribute(PSEUDOCONSOLE=0x{PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE:X}) ok={updateOk} lastError={updateError}");
                    if (!updateOk)
                        throw new Win32Exception(updateError, "UpdateProcThreadAttribute failed.");
                }
                finally
                {
                    hpcValue.Free();
                }

                var startup = new StartupInfoEx
                {
                    StartupInfo = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>() },
                    AttributeList = attrList
                };
                Console.WriteLine($"[ConPTY-DEBUG] StartupInfoEx.Size={Marshal.SizeOf<StartupInfoEx>()} StartupInfo.cb={startup.StartupInfo.Size}");

                var commandLine = BuildCommandLine(options.App, options.CommandLine);
                var createOk = CreateProcess(
                        lpApplicationName: null,
                        lpCommandLine: commandLine,
                        lpProcessAttributes: IntPtr.Zero,
                        lpThreadAttributes: IntPtr.Zero,
                        bInheritHandles: true,
                        dwCreationFlags: EXTENDED_STARTUPINFO_PRESENT,
                        lpEnvironment: IntPtr.Zero,
                        lpCurrentDirectory: options.WorkingDirectory,
                        lpStartupInfo: ref startup,
                        lpProcessInformation: out var pi);
                var createError = Marshal.GetLastPInvokeError();
                Console.WriteLine($"[ConPTY-DEBUG] CreateProcess ok={createOk} dwCreationFlags=0x{EXTENDED_STARTUPINFO_PRESENT:X} lastError={createError} app='{options.App}'");
                if (!createOk)
                {
                    throw new Win32Exception(createError, $"CreateProcess failed for '{options.App}'.");
                }

                process = new SafeProcessHandle(pi.Process, ownsHandle: true);
                processId = (int)pi.ProcessId;
                if (pi.Thread != IntPtr.Zero) CloseHandle(pi.Thread);

                // ConPTY holds its own copies of these two ends; the parent no longer needs them.
                if (hPtyOutputWrite != IntPtr.Zero) CloseHandle(hPtyOutputWrite);
                if (hPtyInputRead != IntPtr.Zero) CloseHandle(hPtyInputRead);
            }
            finally
            {
                DeleteProcThreadAttributeList(attrList);
                Marshal.FreeHGlobal(attrList);
            }
        }
        catch
        {
            if (hpc != IntPtr.Zero) ClosePseudoConsole(hpc);
            throw;
        }

        // The anonymous-pipe handles from CreatePipe are synchronous, so we open them as
        // synchronous FileStreams. The terminal read loop runs on a dedicated thread, and
        // disposal of the stream unblocks a pending read on shutdown.
        var input = new FileStream(new SafeFileHandle(hPtyInputWrite, ownsHandle: true), FileAccess.Write, 4096, isAsync: false);
        var output = new FileStream(new SafeFileHandle(hPtyOutputRead, ownsHandle: true), FileAccess.Read, 4096, isAsync: false);

        return new WindowsPtyConnection(input, output, hpc, process, processId);
    }

    private static (IntPtr Read, IntPtr Write) CreatePipe()
    {
        if (!CreatePipe(out var read, out var write, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreatePipe failed.");
        return (read, write);
    }

    private static string BuildCommandLine(string app, IReadOnlyList<string>? args)
    {
        var quoted = Quote(app);
        if (args is { Count: > 0 })
        {
            quoted += " " + string.Join(" ", args.Select(Quote));
        }
        return quoted;
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    public void Resize(int cols, int rows)
    {
        if (_hpc != IntPtr.Zero)
        {
            ResizePseudoConsole(_hpc, new Coord { X = (short)Math.Max(cols, 1), Y = (short)Math.Max(rows, 1) });
        }
    }

    public void Kill()
    {
        if (!_process.IsInvalid)
        {
            try { TerminateProcess(_process, 1); } catch { /* best effort */ }
        }
    }

    public void Dispose()
    {
        lock (_disposeLock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        try { Kill(); } catch { /* best effort */ }
        try { _input.Dispose(); } catch { /* best effort */ }
        try { _output.Dispose(); } catch { /* best effort */ }
        if (_hpc != IntPtr.Zero) { try { ClosePseudoConsole(_hpc); } catch { /* best effort */ } }
        try { _process.Dispose(); } catch { /* best effort */ }
    }

    // ---- Native interop ----

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string Reserved;
        public string Desktop;
        public string Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2;
        public IntPtr Reserved2Handle;
        public IntPtr StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, IntPtr lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int CreatePseudoConsole(Coord size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, Coord size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, uint dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr Attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref StartupInfoEx lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(SafeProcessHandle hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle hProcess, out uint lpExitCode);
}
