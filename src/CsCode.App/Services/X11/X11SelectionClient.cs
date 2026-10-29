using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace CsCode.App.Services.X11;

/// <summary>
/// Owns and reads one X11 selection (PRIMARY for "select to copy, middle-click to paste"; CLIPBOARD in tests, as an X client independent of
/// Avalonia's) without xclip, xsel or wl-clipboard: its own XCB connection, an unmapped input-only window, and one dedicated event thread that
/// answers <c>SelectionRequest</c>s (TARGETS, UTF8_STRING, STRING, TEXT, text/plain; INCR for large text) and reads other owners' text
/// (INCR included). Under KDE Plasma on Wayland, CsCode runs through XWayland and KWin bridges this X11 PRIMARY to the Wayland primary selection.
/// <para>
/// Threading: every XCB call is made on the event thread. Callers (the UI thread) only enqueue work and wake the thread through a pipe, so nothing
/// here can block the caller on the X server or on another selection owner. The thread sleeps in <c>poll</c> when idle (no timers, no CPU).
/// </para>
/// </summary>
public sealed class X11SelectionClient : IDisposable
{
    /// <summary>Text above this size is sent with the INCR protocol, in chunks of this size (ICCCM: data larger than a request).</summary>
    internal const int IncrChunk = 256 * 1024;
    private static readonly TimeSpan TransferTimeout = TimeSpan.FromSeconds(30);

    private readonly string _selectionName;
    private readonly string? _display;
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly Dictionary<(uint Requestor, uint Property), Outgoing> _outgoing = new();
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly Thread _thread;
    private readonly int[] _wake = [-1, -1];
    private IntPtr _c;
    private uint _window, _selection, _utf8, _string, _text, _textPlain, _textPlainUtf8, _targets, _timestamp, _incr, _property, _wakeAtom;
    private int _maxRequestBytes = 256 * 1024;
    private volatile Func<string>? _provider;
    private volatile bool _owned, _disposed, _broken;
    private Func<string>? _encodedFor;
    private string? _encodedText;
    private PendingRead? _read;
    private long _progress;

    private X11SelectionClient(string selectionName, string? display)
    {
        _selectionName = selectionName;
        _display = display;
        _thread = new Thread(Run) { IsBackground = true, Name = "cscode-x11-" + selectionName.ToLowerInvariant() };
    }

    /// <summary>
    /// Starts a client for <paramref name="selectionName"/> on <paramref name="display"/> (default: <c>$DISPLAY</c>). Returns null when there is no
    /// X display or libxcb cannot be loaded. Never blocks: the connection is made on the client's own thread (see <see cref="WhenReady"/>).
    /// </summary>
    public static X11SelectionClient? TryStart(string selectionName = "PRIMARY", string? display = null)
    {
        if (!OperatingSystem.IsLinux()) return null;
        display ??= Environment.GetEnvironmentVariable("DISPLAY");
        if (string.IsNullOrEmpty(display)) return null;
        if (!NativeLibrary.TryLoad("libxcb.so.1", typeof(X11SelectionClient).Assembly, null, out var handle)) return null;
        NativeLibrary.Free(handle);
        var client = new X11SelectionClient(selectionName, display);
        if (Native.pipe2(client._wake, Native.OCloexec | Native.ONonblock) != 0) return null;
        client._thread.Start();
        return client;
    }

    /// <summary>Completes with true once connected, false if the X server could not be reached.</summary>
    public Task<bool> WhenReady => _ready.Task;

    /// <summary>False once the connection failed or the client was disposed.</summary>
    public bool IsAlive => !_disposed && !_broken;

    /// <summary>Whether this client currently owns the selection (another client taking it clears this).</summary>
    public bool IsOwner => _owned;

    /// <summary>Makes this client the owner, with text produced on demand by <paramref name="provider"/> (on the event thread); null gives it up.</summary>
    public void SetOwner(Func<string>? provider)
    {
        _provider = provider;
        Enqueue(ApplyOwnership);
    }

    /// <summary>Reads the selection's text (our own provider's when we own it): null when empty, refused or not answered within <paramref name="timeout"/>.</summary>
    public async Task<string?> ReadAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (!IsAlive) return null;
        if (_owned && _provider is { } own) return await Task.Run(own, ct).ConfigureAwait(false);
        if (!await WaitReadyAsync(timeout, ct).ConfigureAwait(false)) return null;
        await _readGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var read = new PendingRead();
            Enqueue(() => StartRead(read));
            // The deadline restarts whenever an INCR chunk arrives, so a 10 MB transfer is not cut off while it makes progress.
            var seen = Interlocked.Read(ref _progress);
            while (true)
            {
                var delay = Task.Delay(timeout, ct);
                if (await Task.WhenAny(read.Done.Task, delay).ConfigureAwait(false) == read.Done.Task) return await read.Done.Task.ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                var now = Interlocked.Read(ref _progress);
                if (now == seen || !read.Incr)
                {
                    Enqueue(() => { if (ReferenceEquals(_read, read)) _read = null; });
                    return null;
                }
                seen = now;
            }
        }
        finally
        {
            _readGate.Release();
        }
    }

    private async Task<bool> WaitReadyAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (_ready.Task.IsCompleted) return await _ready.Task.ConfigureAwait(false);
        var delay = Task.Delay(timeout, ct);
        return await Task.WhenAny(_ready.Task, delay).ConfigureAwait(false) == _ready.Task && await _ready.Task.ConfigureAwait(false);
    }

    private void Enqueue(Action action)
    {
        if (_disposed) return;
        _work.Enqueue(action);
        Wake();
    }

    private readonly object _wakeGate = new();

    private void Wake()
    {
        // Under the gate: the descriptors are closed by Dispose, and a write to a closed (possibly reused) descriptor would hit another file.
        lock (_wakeGate)
            if (_wake[1] >= 0) _ = Native.write(_wake[1], [1], 1);
    }

    // ------------------------------------------------------------------ event thread

    private void Run()
    {
        try
        {
            if (!Connect())
            {
                _broken = true;
                _ready.TrySetResult(false);
                return;
            }
            _ready.TrySetResult(true);
            Loop();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            _broken = true;
            _ready.TrySetResult(false);
        }
        finally
        {
            _read?.Done.TrySetResult(null);
            _read = null;
            if (_c != IntPtr.Zero)
            {
                Xcb.xcb_disconnect(_c);
                _c = IntPtr.Zero;
            }
        }
    }

    private bool Connect()
    {
        _c = Xcb.xcb_connect(_display, out _);
        if (_c == IntPtr.Zero || Xcb.xcb_connection_has_error(_c) != 0) return false;
        var screen = Xcb.xcb_setup_roots_iterator(Xcb.xcb_get_setup(_c));
        if (screen.Data == IntPtr.Zero) return false;
        var root = Xcb.ReadU32(screen.Data, 0);
        _window = Xcb.xcb_generate_id(_c);
        Xcb.xcb_create_window(_c, 0, _window, root, -10, -10, 1, 1, 0, Xcb.WindowClassInputOnly, 0, Xcb.CwEventMask, [Xcb.EventMaskPropertyChange]);
        _selection = Atom(_selectionName);
        _utf8 = Atom("UTF8_STRING");
        _string = Xcb.AtomString;
        _text = Atom("TEXT");
        _textPlain = Atom("text/plain");
        _textPlainUtf8 = Atom("text/plain;charset=utf-8");
        _targets = Atom("TARGETS");
        _timestamp = Atom("TIMESTAMP");
        _incr = Atom("INCR");
        _property = Atom("CSCODE_SELECTION");
        _wakeAtom = Atom("CSCODE_WAKE");
        var max = (long)Xcb.xcb_get_maximum_request_length(_c) * 4;
        _maxRequestBytes = (int)Math.Clamp(max, 16 * 1024, int.MaxValue);
        Xcb.xcb_flush(_c);
        return Xcb.xcb_connection_has_error(_c) == 0;
    }

    private uint Atom(string name)
    {
        var bytes = Encoding.ASCII.GetBytes(name);
        var cookie = Xcb.xcb_intern_atom(_c, 0, (ushort)bytes.Length, bytes);
        var reply = Xcb.xcb_intern_atom_reply(_c, cookie, IntPtr.Zero);
        if (reply == IntPtr.Zero) return Xcb.None;
        var atom = Xcb.ReadU32(reply, 8);
        Xcb.Free(reply);
        return atom;
    }

    private void Loop()
    {
        var fds = new Native.PollFd[2];
        var drain = new byte[64];
        while (!_disposed)
        {
            while (_work.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { /* one bad request must not stop the selection */ }
            }
            IntPtr ev;
            while ((ev = Xcb.xcb_poll_for_event(_c)) != IntPtr.Zero)
            {
                var bytes = new byte[32];
                Marshal.Copy(ev, bytes, 0, 32);
                Xcb.Free(ev);
                try { Handle(bytes); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { /* ignore a malformed event */ }
            }
            if (Xcb.xcb_connection_has_error(_c) != 0)
            {
                _broken = true;
                _owned = false;
                return;
            }
            Xcb.xcb_flush(_c);
            if (!_work.IsEmpty) continue;
            ExpireTransfers();
            fds[0] = new Native.PollFd { Fd = Xcb.xcb_get_file_descriptor(_c), Events = Native.PollIn };
            fds[1] = new Native.PollFd { Fd = _wake[0], Events = Native.PollIn };
            // Idle: wait for the X server or a caller. With outgoing INCR transfers a timeout lets abandoned ones expire.
            _ = Native.poll(fds, 2, _outgoing.Count > 0 ? 1000 : -1);
            if ((fds[1].Revents & Native.PollIn) != 0) while (Native.read(_wake[0], drain, drain.Length) > 0) { }
        }
    }

    private void Handle(byte[] e)
    {
        switch ((byte)(e[0] & 0x7f))
        {
            case Xcb.SelectionRequest: OnSelectionRequest(e); break;
            case Xcb.SelectionNotify: OnSelectionNotify(e); break;
            case Xcb.SelectionClear:
                if (Xcb.U32(e, 12) == _selection)
                {
                    _owned = false;
                    _encodedFor = null;
                    _encodedText = null;
                }
                break;
            case Xcb.PropertyNotify: OnPropertyNotify(e); break;
            // 0 = a protocol error (e.g. BadWindow for a requestor that vanished mid-transfer): nothing to do, the transfer expires.
        }
    }

    private void ApplyOwnership()
    {
        _encodedFor = null;
        _encodedText = null;
        if (_provider is null)
        {
            if (_owned) Xcb.xcb_set_selection_owner(_c, Xcb.None, _selection, Xcb.CurrentTime);
            _owned = false;
            return;
        }
        if (_owned) return; // the new provider answers the next request
        Xcb.xcb_set_selection_owner(_c, _window, _selection, Xcb.CurrentTime);
        var reply = Xcb.xcb_get_selection_owner_reply(_c, Xcb.xcb_get_selection_owner(_c, _selection), IntPtr.Zero);
        if (reply == IntPtr.Zero) return;
        _owned = Xcb.ReadU32(reply, 8) == _window;
        Xcb.Free(reply);
    }

    private string CurrentText()
    {
        var provider = _provider;
        if (provider is null) return string.Empty;
        if (ReferenceEquals(provider, _encodedFor) && _encodedText is not null) return _encodedText;
        var text = provider();
        _encodedFor = provider;
        _encodedText = text;
        return text;
    }

    private void OnSelectionRequest(byte[] e)
    {
        var time = Xcb.U32(e, 4);
        var requestor = Xcb.U32(e, 12);
        var selection = Xcb.U32(e, 16);
        var target = Xcb.U32(e, 20);
        var property = Xcb.U32(e, 24);
        if (property == Xcb.None) property = target; // an obsolete client
        var answered = Xcb.None;
        if (selection == _selection && _owned && _provider is not null)
        {
            if (target == _targets)
            {
                var atoms = new[] { _targets, _timestamp, _utf8, _textPlainUtf8, _string, _text, _textPlain };
                var data = new byte[atoms.Length * 4];
                for (var i = 0; i < atoms.Length; i++) Xcb.WriteU32(data, i * 4, atoms[i]);
                Xcb.xcb_change_property(_c, Xcb.PropModeReplace, requestor, property, Xcb.AtomAtom, 32, (uint)atoms.Length, data);
                answered = property;
            }
            else if (target == _timestamp)
            {
                var data = new byte[4];
                Xcb.xcb_change_property(_c, Xcb.PropModeReplace, requestor, property, Xcb.AtomInteger, 32, 1, data);
                answered = property;
            }
            else if (target == _utf8 || target == _text || target == _textPlainUtf8 || target == _textPlain || target == _string)
            {
                var text = CurrentText();
                var bytes = target == _string ? Encoding.Latin1.GetBytes(text) : Encoding.UTF8.GetBytes(text);
                var type = target == _text || target == _textPlain ? _utf8 : target;
                if (bytes.Length > Math.Min(IncrChunk, _maxRequestBytes - 1024)) StartIncr(requestor, property, type, bytes);
                else Xcb.xcb_change_property(_c, Xcb.PropModeReplace, requestor, property, type, 8, (uint)bytes.Length, bytes);
                answered = property;
            }
        }
        var notify = new byte[32];
        notify[0] = Xcb.SelectionNotify;
        Xcb.WriteU32(notify, 4, time);
        Xcb.WriteU32(notify, 8, requestor);
        Xcb.WriteU32(notify, 12, selection);
        Xcb.WriteU32(notify, 16, target);
        Xcb.WriteU32(notify, 20, answered);
        Xcb.xcb_send_event(_c, 0, requestor, 0, notify);
    }

    private void StartIncr(uint requestor, uint property, uint type, byte[] bytes)
    {
        Xcb.xcb_change_window_attributes(_c, requestor, Xcb.CwEventMask, [Xcb.EventMaskPropertyChange]);
        var size = new byte[4];
        Xcb.WriteU32(size, 0, (uint)bytes.Length);
        Xcb.xcb_change_property(_c, Xcb.PropModeReplace, requestor, property, _incr, 32, 1, size);
        _outgoing[(requestor, property)] = new Outgoing(type, bytes, Environment.TickCount64);
    }

    private void OnPropertyNotify(byte[] e)
    {
        var window = Xcb.U32(e, 4);
        var atom = Xcb.U32(e, 8);
        var state = e[16];
        if (state == Xcb.PropertyDelete && _outgoing.TryGetValue((window, atom), out var transfer))
        {
            // The requestor consumed the previous chunk: send the next one, then a zero-length property to end the transfer.
            var remaining = transfer.Data.Length - transfer.Offset;
            var length = Math.Min(IncrChunk, remaining);
            var chunk = transfer.Data.AsSpan(transfer.Offset, length).ToArray();
            Xcb.xcb_change_property(_c, Xcb.PropModeReplace, window, atom, transfer.Type, 8, (uint)length, chunk);
            transfer.Offset += length;
            transfer.LastActivity = Environment.TickCount64;
            if (length == 0) _outgoing.Remove((window, atom));
            return;
        }
        if (window == _window && atom == _property && state == Xcb.PropertyNewValue && _read is { Incr: true } read) ReadIncrChunk(read);
    }

    private void ExpireTransfers()
    {
        if (_outgoing.Count == 0) return;
        var now = Environment.TickCount64;
        foreach (var key in _outgoing.Where(kv => now - kv.Value.LastActivity > TransferTimeout.TotalMilliseconds).Select(kv => kv.Key).ToArray())
            _outgoing.Remove(key);
    }

    // ---- reading another owner's selection

    private void StartRead(PendingRead read)
    {
        _read?.Done.TrySetResult(null);
        _read = read;
        read.Target = _utf8;
        Xcb.xcb_delete_property(_c, _window, _property);
        Xcb.xcb_convert_selection(_c, _window, _selection, _utf8, _property, Xcb.CurrentTime);
    }

    private void OnSelectionNotify(byte[] e)
    {
        if (_read is not { } read || Xcb.U32(e, 8) != _window || Xcb.U32(e, 12) != _selection) return;
        var property = Xcb.U32(e, 20);
        if (property == Xcb.None)
        {
            if (read.Target == _utf8)
            {
                // The owner has no UTF8_STRING (an old client): ask for STRING (Latin-1).
                read.Target = _string;
                Xcb.xcb_convert_selection(_c, _window, _selection, _string, _property, Xcb.CurrentTime);
                return;
            }
            Complete(read, null);
            return;
        }
        var (type, data) = GetProperty(delete: true);
        if (type == _incr)
        {
            // Deleting the INCR property (done above) tells the owner to start sending chunks (PropertyNotify NewValue on our window).
            read.Incr = true;
            Interlocked.Increment(ref _progress);
            return;
        }
        Complete(read, Decode(type, data));
    }

    private void ReadIncrChunk(PendingRead read)
    {
        var (type, data) = GetProperty(delete: true);
        Interlocked.Increment(ref _progress);
        if (data.Length == 0)
        {
            Complete(read, Decode(read.IncrType == Xcb.None ? type : read.IncrType, read.Buffer.ToArray()));
            return;
        }
        if (read.IncrType == Xcb.None) read.IncrType = type;
        read.Buffer.Write(data);
    }

    private (uint Type, byte[] Data) GetProperty(bool delete)
    {
        var cookie = Xcb.xcb_get_property(_c, (byte)(delete ? 1 : 0), _window, _property, Xcb.AnyPropertyType, 0, 0x1FFFFFFF);
        var reply = Xcb.xcb_get_property_reply(_c, cookie, IntPtr.Zero);
        if (reply == IntPtr.Zero) return (Xcb.None, []);
        try
        {
            var type = Xcb.ReadU32(reply, 8);
            var length = Xcb.xcb_get_property_value_length(reply);
            var data = new byte[Math.Max(0, length)];
            if (length > 0) Marshal.Copy(Xcb.xcb_get_property_value(reply), data, 0, length);
            return (type, data);
        }
        finally
        {
            Xcb.Free(reply);
        }
    }

    private string Decode(uint type, byte[] data) => type == _string ? Encoding.Latin1.GetString(data) : Encoding.UTF8.GetString(data);

    private void Complete(PendingRead read, string? text)
    {
        if (ReferenceEquals(_read, read)) _read = null;
        read.Done.TrySetResult(text);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _provider = null;
        Wake();
        var stopped = !_thread.IsAlive || (!ReferenceEquals(Thread.CurrentThread, _thread) && _thread.Join(TimeSpan.FromSeconds(2)));
        if (!stopped) return; // a wedged thread keeps its pipe (closing it under the thread could hand the number to another file)
        lock (_wakeGate)
        {
            Native.close(_wake[0]);
            Native.close(_wake[1]);
            _wake[0] = _wake[1] = -1;
        }
    }

    private sealed class PendingRead
    {
        public TaskCompletionSource<string?> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public uint Target { get; set; }
        public volatile bool Incr;
        public uint IncrType { get; set; }
        public MemoryStream Buffer { get; } = new();
    }

    private sealed class Outgoing(uint type, byte[] data, long started)
    {
        public uint Type { get; } = type;
        public byte[] Data { get; } = data;
        public int Offset { get; set; }
        public long LastActivity { get; set; } = started;
    }

    private static class Native
    {
        public const int OCloexec = 0x80000;
        public const int ONonblock = 0x800;
        public const short PollIn = 1;

        [StructLayout(LayoutKind.Sequential)]
        public struct PollFd
        {
            public int Fd;
            public short Events;
            public short Revents;
        }

        [DllImport("libc", SetLastError = true)] public static extern int pipe2(int[] fds, int flags);
        [DllImport("libc", SetLastError = true)] public static extern int poll([In, Out] PollFd[] fds, nuint count, int timeout);
        [DllImport("libc", SetLastError = true)] public static extern nint read(int fd, byte[] buffer, nint count);
        [DllImport("libc", SetLastError = true)] public static extern nint write(int fd, byte[] buffer, nint count);
        [DllImport("libc", SetLastError = true)] public static extern int close(int fd);
    }
}
