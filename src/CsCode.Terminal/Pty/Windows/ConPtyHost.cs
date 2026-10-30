using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CsCode.Terminal.Pty.Windows;

/// <summary>
/// A program on a Windows pseudo console (ConPTY, Windows 10 1809+): <c>CreatePseudoConsole</c> over two anonymous pipes, the process
/// started with <c>PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE</c> inside a kill-on-close job object (so killing the terminal ends the whole
/// tree). Same threads as the Unix host: a reader, a writer draining the bounded queue, and a waiter for the exit code.
/// <para><b>Written but untested</b>: CsCode is developed and tested on Linux only (docs/TERMINAL.md).</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ConPtyHost : IPtyHost
{
    private readonly PtyWriteQueue _queue = new(16L * 1024 * 1024);
    private readonly object _gate = new();
    private readonly AutoResetEvent _writerWake = new(false);
    private IntPtr _console;
    private IntPtr _process;
    private IntPtr _job;
    private SafeFileHandle? _input;   // we write the program's input here
    private SafeFileHandle? _output;  // we read the program's output here
    private IPtyListener? _listener;
    private int _pid;
    private int _exitCode = -1;
    private volatile bool _exited;
    private volatile bool _disposed;

    public int ProcessId => _pid;
    public bool HasExited => _exited;
    public int? ExitCode => _exited ? _exitCode : null;
    public long PendingWriteBytes => _queue.Pending;
    public long MaxPendingWriteBytes => _queue.Capacity;

    public void Start(PtyStartInfo info, IPtyListener listener)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _listener = listener;
            if (!Native.CreatePipe(out var inRead, out var inWrite, IntPtr.Zero, 0) || !Native.CreatePipe(out var outRead, out var outWrite, IntPtr.Zero, 0))
                throw new PtySpawnException("Could not create the pseudo console's pipes.", Marshal.GetLastWin32Error());
            var hr = Native.CreatePseudoConsole(new Native.Coord((short)info.Columns, (short)info.Rows), inRead, outWrite, 0, out _console);
            inRead.Dispose();
            outWrite.Dispose();
            if (hr != 0) throw new PtySpawnException($"CreatePseudoConsole failed (0x{hr:X8}).", hr);
            _input = inWrite;
            _output = outRead;
            _job = Native.CreateJobObject(IntPtr.Zero, null);
            var limits = new Native.JobObjectExtendedLimitInformation { BasicLimitInformation = new Native.JobObjectBasicLimitInformation { LimitFlags = 0x2000 /* KILL_ON_JOB_CLOSE */ } };
            Native.SetInformationJobObject(_job, 9 /* ExtendedLimitInformation */, ref limits, (uint)Marshal.SizeOf<Native.JobObjectExtendedLimitInformation>());
            StartProcess(info);
            new Thread(ReaderLoop) { IsBackground = true, Name = $"cscode-conpty-read-{_pid}" }.Start();
            new Thread(WriterLoop) { IsBackground = true, Name = $"cscode-conpty-write-{_pid}" }.Start();
            new Thread(WaitLoop) { IsBackground = true, Name = $"cscode-conpty-wait-{_pid}" }.Start();
        }
    }

    private void StartProcess(PtyStartInfo info)
    {
        var size = IntPtr.Zero;
        Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var list = Marshal.AllocHGlobal(size);
        try
        {
            if (!Native.InitializeProcThreadAttributeList(list, 1, 0, ref size)) throw new Win32Exception();
            if (!Native.UpdateProcThreadAttribute(list, 0, (IntPtr)0x00020016 /* PSEUDOCONSOLE */, _console, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();
            var si = new Native.StartupInfoEx { StartupInfo = new Native.StartupInfo { cb = Marshal.SizeOf<Native.StartupInfoEx>() }, AttributeList = list };
            var commandLine = new StringBuilder(Quote(info.FileName));
            foreach (var a in info.Arguments) commandLine.Append(' ').Append(Quote(a));
            var env = new StringBuilder();
            foreach (var (k, v) in info.Environment.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)) env.Append(k).Append('=').Append(v).Append('\0');
            env.Append('\0');
            if (!Native.CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, 0x00080000 | 0x00000400 /* EXTENDED_STARTUPINFO_PRESENT | UNICODE_ENVIRONMENT */,
                    env.ToString(), info.WorkingDirectory, ref si, out var pi))
                throw new PtySpawnException($"Could not start '{info.FileName}'.", Marshal.GetLastWin32Error());
            Native.AssignProcessToJobObject(_job, pi.hProcess);
            Native.CloseHandle(pi.hThread);
            _process = pi.hProcess;
            _pid = pi.dwProcessId;
        }
        finally
        {
            Native.DeleteProcThreadAttributeList(list);
            Marshal.FreeHGlobal(list);
        }
    }

    private static string Quote(string arg) =>
        arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0 ? arg : "\"" + arg.Replace("\\\"", "\\\\\"", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private void ReaderLoop()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            using var stream = new FileStream(_output!, FileAccess.Read, 1, isAsync: false);
            int n;
            while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                if (!_disposed) _listener?.OnOutput(buffer.AsSpan(0, n));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private void WriterLoop()
    {
        try
        {
            using var stream = new FileStream(_input!, FileAccess.Write, 1, isAsync: false);
            while (!_disposed && !_exited)
            {
                var chunk = _queue.Peek(out var token);
                if (chunk.IsEmpty) { _writerWake.WaitOne(); continue; }
                stream.Write(chunk.Span); // blocks only this thread when the program does not read
                stream.Flush();
                _queue.Advance(token, chunk.Length);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        finally { _queue.Close(); }
    }

    private void WaitLoop()
    {
        Native.WaitForSingleObject(_process, 0xFFFFFFFF);
        Native.GetExitCodeProcess(_process, out var code);
        _exitCode = (int)code;
        _exited = true;
        _writerWake.Set();
        // Closing the console flushes the remaining output and ends the reader with EOF.
        lock (_gate)
        {
            if (_console != IntPtr.Zero) { Native.ClosePseudoConsole(_console); _console = IntPtr.Zero; }
        }
        try { _listener?.OnExited(_exitCode); } catch (Exception) { }
    }

    public bool Write(ReadOnlySpan<byte> data, bool urgent = false)
    {
        if (_disposed || _exited || !_queue.Enqueue(data, urgent)) return false;
        _writerWake.Set();
        return true;
    }

    public void Resize(int columns, int rows)
    {
        lock (_gate)
        {
            if (_console != IntPtr.Zero) Native.ResizePseudoConsole(_console, new Native.Coord((short)Math.Clamp(columns, 1, short.MaxValue), (short)Math.Clamp(rows, 1, short.MaxValue)));
        }
    }

    public void Kill(TimeSpan? grace = null)
    {
        lock (_gate)
        {
            if (_job != IntPtr.Zero) Native.TerminateJobObject(_job, 1);
            else if (_process != IntPtr.Zero && !_exited) Native.TerminateProcess(_process, 1);
        }
    }

    public string? GetForegroundProcessName()
    {
        try { return _exited ? null : System.Diagnostics.Process.GetProcessById(_pid).ProcessName; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return null; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Kill();
        _writerWake.Set();
        lock (_gate)
        {
            if (_console != IntPtr.Zero) { Native.ClosePseudoConsole(_console); _console = IntPtr.Zero; }
            if (_job != IntPtr.Zero) { Native.CloseHandle(_job); _job = IntPtr.Zero; }
        }
        _input?.Dispose();
        _output?.Dispose();
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public readonly struct Coord(short x, short y)
        {
            public readonly short X = x;
            public readonly short Y = y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct StartupInfo
        {
            public int cb;
            public string? lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct StartupInfoEx
        {
            public StartupInfo StartupInfo;
            public IntPtr AttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ProcessInformation
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, int size);
        [DllImport("kernel32.dll")] public static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);
        [DllImport("kernel32.dll")] public static extern int ResizePseudoConsole(IntPtr console, Coord size);
        [DllImport("kernel32.dll")] public static extern void ClosePseudoConsole(IntPtr console);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);
        [DllImport("kernel32.dll")] public static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcessW(string? application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles,
            uint flags, string environment, string currentDirectory, ref StartupInfoEx startupInfo, out ProcessInformation processInformation);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobObjectExtendedLimitInformation info, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool TerminateJobObject(IntPtr job, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool TerminateProcess(IntPtr process, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr handle);
    }
}
