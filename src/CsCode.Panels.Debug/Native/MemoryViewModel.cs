using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CsCode.Core.Debugging;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Panels.Debug.Infrastructure;

namespace CsCode.Panels.Debug.Native;

/// <summary>Sixteen bytes of the Memory window: address, hex and ASCII (<c>??</c> where unread or unreadable).</summary>
public sealed class MemoryRow(int index, long offset) : ObservableObject
{
    internal const string Unread = "?? ?? ?? ?? ?? ?? ?? ??  ?? ?? ?? ?? ?? ?? ?? ??";
    private string _address = "", _hex = Unread, _ascii = "";

    public int Index { get; } = index;
    /// <summary>Bytes from the window's memory reference to this row's first byte (negative above it).</summary>
    public long Offset { get; } = offset;
    public string AddressText { get => _address; set => SetProperty(ref _address, value); }
    public string Hex { get => _hex; set => SetProperty(ref _hex, value); }
    public string Ascii { get => _ascii; set => SetProperty(ref _ascii, value); }
    /// <summary>The row of the reference itself (offset 0): marked in the view.</summary>
    public bool IsTarget => Offset == 0;
}

/// <summary>
/// Memory window (VS Code's hex editor for <c>readMemory</c>): <see cref="RowCount"/> rows of <see cref="BytesPerRow"/> bytes around a memory
/// reference — a variable's ("View Binary Data" on a Locals / Watch row), or what the address box gives (an address, or an expression with a
/// memory reference). Only the rows the view shows are read, a <see cref="PageBytes"/> page per request, one request per page in flight; at most
/// <see cref="MaxPages"/> pages are kept (the least recent are unloaded and read again when shown). Every stop marks the pages stale and reads
/// the visible ones again. Editing a row writes it with <c>writeMemory</c> when the adapter supports it.
/// </summary>
public sealed class MemoryViewModel : ObservableObject, IDisposable
{
    public const int BytesPerRow = 16;
    public const int RowCount = 4_096; // 64 KiB: 32 KiB each side of the reference
    public const int PageBytes = 4_096;
    public const int MaxPages = 16;
    private const int RowsPerPage = PageBytes / BytesPerRow;

    private readonly DebugPanelServices _s;
    private readonly DebugFocusTracker _tracker;
    private readonly DisposableStore _subs = new();
    private readonly LinkedList<long> _loadedPages = new(); // most recent first
    private readonly HashSet<long> _inFlight = new();
    private CancellationTokenSource _cts = new();
    private IDebugSession? _session;
    private DebugFocus _focus;
    private NativeTarget? _target;
    private ulong? _baseAddress;
    private long _generation, _loadedEpoch = -1;
    private int _firstVisible, _lastVisible = 32;
    private string? _status = "Not debugging", _addressInput, _title;
    private MemoryRow? _selected;
    private int _disposed;

    public MemoryViewModel(DebugPanelServices services)
    {
        _s = services;
        var rows = new MemoryRow[RowCount];
        for (var i = 0; i < RowCount; i++) rows[i] = new MemoryRow(i, (long)(i - RowCount / 2) * BytesPerRow);
        Rows = rows;
        GoCommand = new UiCommand(() => GoToAsync(AddressInput));
        RefreshCommand = new UiCommand(() => { Invalidate(); return LoadVisibleAsync(); });
        EditCommand = new UiCommand(() => SelectedRow is { } r ? EditAsync(r) : Task.CompletedTask);
        _subs.Add(_s.NativeViews.MemoryRequested.Subscribe(t => _s.Ui.Post(() => Navigate(t))));
        _target = _s.NativeViews.TakeMemory();
        _tracker = new DebugFocusTracker(services, OnFocus);
        _tracker.Start();
    }

    public IReadOnlyList<MemoryRow> Rows { get; }
    public MemoryRow? SelectedRow { get => _selected; set => SetProperty(ref _selected, value); }
    public string? StatusText { get => _status; private set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(ShowStatus)); } }
    public bool ShowStatus => !string.IsNullOrEmpty(_status);
    /// <summary>What is shown: the variable or expression and its address.</summary>
    public string? Title { get => _title; private set => SetProperty(ref _title, value); }
    public string? AddressInput { get => _addressInput; set => SetProperty(ref _addressInput, value); }
    public UiCommand GoCommand { get; }
    public UiCommand RefreshCommand { get; }
    public UiCommand EditCommand { get; }
    public bool CanWrite => _session is IDebugSessionNative { SupportsWriteMemory: true };
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    /// <summary>The row of the reference (the view scrolls it to the top after a navigation).</summary>
    public MemoryRow TargetRow => Rows[RowCount / 2];
    public event Action<MemoryRow>? RevealRequested;
    internal int RequestsSent;

    // ------------------------------------------------------------------ following the debugger

    private void OnFocus(DebugFocus focus)
    {
        if (IsDisposed) return;
        _focus = focus;
        if (focus.Session is null || focus.State == DebugSessionState.Terminated)
        {
            Reset("Not debugging");
            _session = null;
            return;
        }
        if (!ReferenceEquals(focus.Session, _session))
        {
            Reset(null);
            _session = focus.Session;
            OnPropertyChanged(nameof(CanWrite));
        }
        if (!focus.Session.Capabilities.SupportsReadMemory || focus.Session is not IDebugSessionNative)
        {
            Reset("The debug adapter does not support reading memory.");
            return;
        }
        if (!focus.IsStopped || focus.Epoch == _loadedEpoch) return;
        _loadedEpoch = focus.Epoch;
        if (_target is null) { StatusText = "Enter an address or expression, or use View Binary Data on a variable."; return; }
        Invalidate();
        _ = LoadVisibleAsync();
    }

    private void Navigate(NativeTarget target)
    {
        if (IsDisposed) return;
        _s.NativeViews.Handled(target);
        SetTarget(target);
    }

    private void SetTarget(NativeTarget target)
    {
        Reset(null);
        _target = target;
        _baseAddress = DebugAddress.TryParse(target.MemoryReference, out var a) ? a : null;
        UpdateTitle();
        foreach (var r in Rows) r.AddressText = AddressOf(r);
        RevealRequested?.Invoke(TargetRow);
        if (_focus.IsStopped) _ = LoadVisibleAsync();
    }

    /// <summary>The address box: an address goes as it is; an expression is evaluated in the focused frame and its memory reference used.</summary>
    public async Task GoToAsync(string? input)
    {
        if (string.IsNullOrWhiteSpace(input) || _session is not { } session) return;
        var text = input.Trim();
        if (DebugAddress.TryParse(text, out _)) { SetTarget(new NativeTarget(text, text)); return; }
        try
        {
            var r = await _s.Ui.OnUi(session.EvaluateAsync(text, _focus.FrameId, "watch", _cts.Token), _cts.Token);
            var reference = AddressOfResult(r);
            if (reference is null) { StatusText = $"'{text}' has no memory reference (try &{text})."; return; }
            SetTarget(new NativeTarget(reference, text));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { StatusText = $"Cannot evaluate '{text}': {ex.Message}"; }
    }

    /// <summary>
    /// Where an evaluated expression's memory is: a pointer's value when the result is an address (<c>0x…</c>: lldb-dap's memory reference of a
    /// pointer is the pointer's own storage, not where it points), else the adapter's memory reference (the value's own location).
    /// </summary>
    internal static string? AddressOfResult(DebugEvaluateResult r)
    {
        var head = r.Result.TrimStart().Split(' ', 2)[0];
        return head.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && DebugAddress.TryParse(head, out _) ? head : r.MemoryReference;
    }

    // ------------------------------------------------------------------ paging

    /// <summary>The view's visible rows changed (scrolling, resizing): the pages they need are read.</summary>
    public void SetVisibleRows(int first, int last)
    {
        _firstVisible = Math.Clamp(first, 0, RowCount - 1);
        _lastVisible = Math.Clamp(last, _firstVisible, RowCount - 1);
        if (_focus.IsStopped) _ = LoadVisibleAsync();
    }

    private void Reset(string? status)
    {
        Interlocked.Increment(ref _generation);
        var old = _cts;
        _cts = new CancellationTokenSource();
        try { old.Cancel(); } catch (AggregateException) { }
        old.Dispose();
        _inFlight.Clear();
        foreach (var page in _loadedPages) Unload(page);
        _loadedPages.Clear();
        _loadedEpoch = -1;
        StatusText = status;
    }

    /// <summary>Every loaded page is stale (a new stop, Refresh): the text stays until the new read replaces it.</summary>
    private void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        _inFlight.Clear();
        _loadedPages.Clear();
    }

    internal async Task LoadVisibleAsync()
    {
        if (_target is not { } target || _session is not IDebugSessionNative native) return;
        var firstPage = PageOf(_firstVisible);
        var lastPage = PageOf(_lastVisible);
        var tasks = new List<Task>();
        for (var page = firstPage; page <= lastPage; page += PageBytes)
            if (!_loadedPages.Contains(page) && _inFlight.Add(page)) tasks.Add(LoadPageAsync(native, target, page));
        await Task.WhenAll(tasks);
    }

    private static long PageOf(int row) => (long)Math.Floor((row - RowCount / 2) / (double)RowsPerPage) * PageBytes;

    private async Task LoadPageAsync(IDebugSessionNative native, NativeTarget target, long page)
    {
        var generation = Volatile.Read(ref _generation);
        var ct = _cts.Token;
        Interlocked.Increment(ref RequestsSent);
        try
        {
            var block = await _s.Ui.OnUi(native.ReadMemoryAsync(target.MemoryReference, page, PageBytes, ct), ct);
            if (IsDisposed || generation != Volatile.Read(ref _generation) || !ReferenceEquals(target, _target)) return;
            if (_baseAddress is null && block.Data.Length + block.UnreadableBytes > 0)
            {
                _baseAddress = unchecked(block.Address - (ulong)page);
                UpdateTitle();
                foreach (var r in Rows) r.AddressText = AddressOf(r);
            }
            Show(page, block.Data.Span);
            _loadedPages.AddFirst(page);
            while (_loadedPages.Count > MaxPages)
            {
                Unload(_loadedPages.Last!.Value);
                _loadedPages.RemoveLast();
            }
            if (StatusText is not null && !StatusText.StartsWith("Cannot", StringComparison.Ordinal)) StatusText = null;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (generation == Volatile.Read(ref _generation)) StatusText = "Cannot read memory: " + ex.Message;
        }
        finally
        {
            if (generation == Volatile.Read(ref _generation)) _inFlight.Remove(page);
        }
    }

    private void Show(long page, ReadOnlySpan<byte> data)
    {
        var firstRow = RowCount / 2 + (int)(page / BytesPerRow);
        for (var i = 0; i < RowsPerPage; i++)
        {
            var index = firstRow + i;
            if (index < 0 || index >= RowCount) continue;
            var start = i * BytesPerRow;
            var row = Rows[index];
            row.Hex = FormatHex(data, start);
            row.Ascii = FormatAscii(data, start);
        }
    }

    private void Unload(long page)
    {
        var firstRow = RowCount / 2 + (int)(page / BytesPerRow);
        for (var i = 0; i < RowsPerPage; i++)
        {
            var index = firstRow + i;
            if (index < 0 || index >= RowCount) continue;
            Rows[index].Hex = MemoryRow.Unread;
            Rows[index].Ascii = "";
        }
    }

    internal static string FormatHex(ReadOnlySpan<byte> data, int start)
    {
        var sb = new StringBuilder(BytesPerRow * 3 + 1);
        for (var i = 0; i < BytesPerRow; i++)
        {
            if (i == 8) sb.Append(' ');
            var at = start + i;
            sb.Append(at < data.Length ? data[at].ToString("x2", CultureInfo.InvariantCulture) : "??");
            if (i < BytesPerRow - 1) sb.Append(' ');
        }
        return sb.ToString();
    }

    internal static string FormatAscii(ReadOnlySpan<byte> data, int start)
    {
        var chars = new char[BytesPerRow];
        for (var i = 0; i < BytesPerRow; i++)
        {
            var at = start + i;
            chars[i] = at >= data.Length ? ' ' : data[at] is >= 0x20 and < 0x7f ? (char)data[at] : '.';
        }
        return new string(chars);
    }

    private string AddressOf(MemoryRow row) =>
        _baseAddress is { } b ? DebugAddress.Format(unchecked(b + (ulong)row.Offset)) : (row.Offset >= 0 ? "+" : "-") + Math.Abs(row.Offset).ToString("x", CultureInfo.InvariantCulture);

    private void UpdateTitle() =>
        Title = _target is null ? null
            : _baseAddress is { } b ? $"{_target.Label ?? _target.MemoryReference} at {DebugAddress.Format(b)}" : _target.Label ?? _target.MemoryReference;

    // ------------------------------------------------------------------ editing

    /// <summary>Edits a row's sixteen bytes (hex, space separated; fewer bytes write fewer) with <c>writeMemory</c>, then reads its page again.</summary>
    public async Task EditAsync(MemoryRow row)
    {
        if (_target is not { } target || _session is not IDebugSessionNative native || _s.Window is not { } window) return;
        if (!native.SupportsWriteMemory) { StatusText = "The debug adapter does not support writing memory."; return; }
        var input = await window.ShowInputBoxAsync(new InputBoxOptions(Title: $"Write memory at {row.AddressText}", Prompt: "Bytes in hex, separated by spaces",
            Value: row.Hex.Replace("  ", " ", StringComparison.Ordinal), Validate: text => ParseBytes(text) is null ? "Enter up to 16 bytes in hex, such as 48 65 6c 6c 6f" : null));
        if (input is null || ParseBytes(input) is not { } bytes) return;
        try
        {
            var written = await _s.Ui.OnUi(native.WriteMemoryAsync(target.MemoryReference, row.Offset, bytes, _cts.Token), _cts.Token);
            if (written is { } n && n < bytes.Length) StatusText = $"Only {n} of {bytes.Length} bytes were written.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { StatusText = "Cannot write memory: " + ex.Message; return; }
        var page = PageOf(row.Index);
        _loadedPages.Remove(page);
        if (_inFlight.Add(page)) await LoadPageAsync(native, target, page);
    }

    public Task CopyAsync(string text) => _s.Window?.CopyToClipboardAsync(text).AsTask() ?? Task.CompletedTask;

    internal static byte[]? ParseBytes(string text)
    {
        var parts = text.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > BytesPerRow) return null;
        var bytes = new byte[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!byte.TryParse(parts[i].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? parts[i][2..] : parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i])) return null;
        return bytes;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _tracker.Dispose();
        _subs.Dispose();
        try { _cts.Cancel(); } catch (AggregateException) { }
        _cts.Dispose();
        _loadedPages.Clear();
        _inFlight.Clear();
        _session = null;
        _target = null;
        _focus = default;
        _selected = null;
        RevealRequested = null;
    }
}
