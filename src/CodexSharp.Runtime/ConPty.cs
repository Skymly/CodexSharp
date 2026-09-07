using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodexSharp.Runtime;

/// Windows ConPTY session for app-server command/exec tty=true.
internal sealed class ConPtyProcess : IDisposable
{
    private IntPtr _pseudo;
    private IntPtr _process;
    private IntPtr _thread;
    private readonly SafeFileHandle _inputWrite;
    private readonly SafeFileHandle _outputRead;
    private bool _disposed;

    public int Pid { get; }
    public bool HasExited => _process != IntPtr.Zero && WaitForSingleObject(_process, 0) == 0;

    private ConPtyProcess(IntPtr pseudo, IntPtr process, IntPtr thread, int pid, SafeFileHandle inputWrite, SafeFileHandle outputRead)
    {
        _pseudo = pseudo;
        _process = process;
        _thread = thread;
        Pid = pid;
        _inputWrite = inputWrite;
        _outputRead = outputRead;
        Input = new FileStream(inputWrite, FileAccess.Write, 4096, isAsync: true);
        Output = new FileStream(outputRead, FileAccess.Read, 4096, isAsync: true);
    }

    public Stream Input { get; }
    public Stream Output { get; }

    public static ConPtyProcess? TryStart(string fileName, string arguments, string workdir, short rows, short cols)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        if (!CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0))
        {
            return null;
        }

        if (!CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0))
        {
            inputRead.Dispose();
            inputWrite.Dispose();
            return null;
        }

        SetHandleInformation(inputWrite, 1, 0);
        SetHandleInformation(outputRead, 1, 0);

        var size = new Coord(cols < 1 ? (short)80 : cols, rows < 1 ? (short)24 : rows);
        var hr = CreatePseudoConsole(size, inputRead, outputWrite, 0, out var pty);
        inputRead.Dispose();
        outputWrite.Dispose();
        if (hr != 0 || pty == IntPtr.Zero)
        {
            inputWrite.Dispose();
            outputRead.Dispose();
            return null;
        }

        var si = new StartupInfoEx();
        si.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
        var attrSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrSize);
        si.lpAttributeList = Marshal.AllocHGlobal(attrSize);
        try
        {
            if (!InitializeProcThreadAttributeList(si.lpAttributeList, 1, 0, ref attrSize))
            {
                ClosePseudoConsole(pty);
                inputWrite.Dispose();
                outputRead.Dispose();
                Marshal.FreeHGlobal(si.lpAttributeList);
                return null;
            }

            var ptyBuf = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(ptyBuf, pty);
            var updated = UpdateProcThreadAttribute(si.lpAttributeList, 0, (IntPtr)0x00020016, ptyBuf, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero);
            Marshal.FreeHGlobal(ptyBuf);
            if (!updated)
            {
                DeleteProcThreadAttributeList(si.lpAttributeList);
                Marshal.FreeHGlobal(si.lpAttributeList);
                ClosePseudoConsole(pty);
                inputWrite.Dispose();
                outputRead.Dispose();
                return null;
            }

            si.StartupInfo.dwFlags = 0x00000100; // STARTF_USESTDHANDLES
            var cmd = string.IsNullOrWhiteSpace(arguments) ? fileName : fileName + " " + arguments;
            var pi = new ProcessInformation();
            var created = CreateProcess(
                null,
                cmd,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                0x00080000 | 0x00000400, // EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT
                IntPtr.Zero,
                string.IsNullOrWhiteSpace(workdir) ? null : workdir,
                ref si,
                out pi);

            DeleteProcThreadAttributeList(si.lpAttributeList);
            Marshal.FreeHGlobal(si.lpAttributeList);
            si.lpAttributeList = IntPtr.Zero;

            if (!created)
            {
                ClosePseudoConsole(pty);
                inputWrite.Dispose();
                outputRead.Dispose();
                return null;
            }

            return new ConPtyProcess(pty, pi.hProcess, pi.hThread, pi.dwProcessId, inputWrite, outputRead);
        }
        catch
        {
            if (si.lpAttributeList != IntPtr.Zero)
            {
                try { DeleteProcThreadAttributeList(si.lpAttributeList); } catch { /* ignore */ }
                Marshal.FreeHGlobal(si.lpAttributeList);
            }

            ClosePseudoConsole(pty);
            inputWrite.Dispose();
            outputRead.Dispose();
            return null;
        }
    }

    public void Resize(int rows, int cols)
    {
        if (_pseudo == IntPtr.Zero)
        {
            return;
        }

        ResizePseudoConsole(_pseudo, new Coord((short)Math.Clamp(cols, 1, 999), (short)Math.Clamp(rows, 1, 999)));
    }

    public void Terminate()
    {
        if (_process != IntPtr.Zero)
        {
            TerminateProcess(_process, 1);
        }
    }

    public async Task WaitForExitAsync(CancellationToken ct)
    {
        if (_process == IntPtr.Zero)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            var wait = WaitForSingleObject(_process, 200);
            if (wait == 0)
            {
                return;
            }

            await Task.Delay(50, ct);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try { Input.Dispose(); } catch { /* ignore */ }
        try { Output.Dispose(); } catch { /* ignore */ }
        if (_thread != IntPtr.Zero)
        {
            CloseHandle(_thread);
            _thread = IntPtr.Zero;
        }

        if (_process != IntPtr.Zero)
        {
            CloseHandle(_process);
            _process = IntPtr.Zero;
        }

        if (_pseudo != IntPtr.Zero)
        {
            ClosePseudoConsole(_pseudo);
            _pseudo = IntPtr.Zero;
        }
    }

    private static bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr sa, int size)
    {
        if (!CreatePipeNative(out read, out write, sa, size))
        {
            read = new SafeFileHandle(IntPtr.Zero, true);
            write = new SafeFileHandle(IntPtr.Zero, true);
            return false;
        }

        return true;
    }

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "CreatePipe")]
    private static extern bool CreatePipeNative(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(SafeFileHandle hObject, int dwMask, int dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(Coord size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, Coord size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string? lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, ref StartupInfoEx lpStartupInfo, out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
        public Coord(short x, short y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }
}
