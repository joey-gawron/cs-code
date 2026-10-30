using Avalonia.Input;
using Avalonia.Input.Platform;
using CsCode.App.Services.X11;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Ui.Threading;

namespace CsCode.App.Services;

/// <summary>The PRIMARY selection behind <see cref="ClipboardService"/> (X11 through <see cref="X11SelectionClient"/>; an in-memory one in tests).</summary>
public interface IPrimarySelectionBackend : IDisposable
{
    bool IsAvailable { get; }
    void SetOwner(Func<string>? provider);
    Task<string?> ReadAsync(TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// The platform clipboard as <see cref="ClipboardService"/> uses it, called on the UI thread. Avalonia's <see cref="IClipboard"/> is the real one
/// (<see cref="AvaloniaPlatformClipboard"/>); it cannot be implemented outside Avalonia, so tests substitute this (a clipboard owner that never answers).
/// </summary>
public interface IPlatformClipboard
{
    Task<string?> TryGetTextAsync();
    Task<bool> HasTextAsync();
    /// <param name="html">Also offered as <c>text/html</c> where the platform takes it (X11/Wayland), for pasting with formatting.</param>
    Task SetAsync(string text, string? html);
}

/// <summary><see cref="IPlatformClipboard"/> over a window's Avalonia clipboard (X11 CLIPBOARD under XWayland on Linux; in-memory when headless).</summary>
public sealed class AvaloniaPlatformClipboard(IClipboard clipboard) : IPlatformClipboard
{
    // `text/html` is the MIME target X11 (and Wayland) applications ask for; Windows' "HTML Format" needs a CF_HTML header and is not written.
    private static readonly DataFormat<string> HtmlFormat = DataFormat.CreateStringPlatformFormat("text/html");

    public Task<string?> TryGetTextAsync() => clipboard.TryGetTextAsync();

    public async Task<bool> HasTextAsync() => (await clipboard.GetDataFormatsAsync().ConfigureAwait(true)).Contains(DataFormat.Text);

    public Task SetAsync(string text, string? html)
    {
        var item = new DataTransferItem();
        item.SetText(text);
        if (html is not null && OperatingSystem.IsLinux()) item.Set(HtmlFormat, html);
        var transfer = new DataTransfer();
        transfer.Add(item);
        return clipboard.SetDataAsync(transfer);
    }

    /// <summary>A factory over a window's clipboard (re-created when the window or its clipboard changes; null without a window).</summary>
    public static Func<IPlatformClipboard?> Of(Func<Avalonia.Controls.TopLevel?> window)
    {
        AvaloniaPlatformClipboard? cached = null;
        return () =>
        {
            if (window()?.Clipboard is not { } c) return null;
            if (cached is null || !ReferenceEquals(cached.Inner, c)) cached = new AvaloniaPlatformClipboard(c);
            return cached;
        };
    }

    internal IClipboard Inner => clipboard;
}

/// <summary>PRIMARY over X11 (also under XWayland): <see cref="X11SelectionClient"/> on <c>$DISPLAY</c>.</summary>
public sealed class X11PrimarySelection : IPrimarySelectionBackend
{
    private readonly X11SelectionClient _client;

    private X11PrimarySelection(X11SelectionClient client) => _client = client;

    /// <summary>Null when there is no X display (headless, Windows, macOS) or libxcb is missing.</summary>
    public static X11PrimarySelection? TryCreate(string? display = null) => X11SelectionClient.TryStart("PRIMARY", display) is { } c ? new X11PrimarySelection(c) : null;

    public bool IsAvailable => _client.IsAlive;
    public void SetOwner(Func<string>? provider) => _client.SetOwner(provider);
    public Task<string?> ReadAsync(TimeSpan timeout, CancellationToken ct) => _client.ReadAsync(timeout, ct);
    public void Dispose() => _client.Dispose();
}

/// <summary>
/// <see cref="IClipboardService"/> over the main window's platform clipboard (Avalonia: X11 CLIPBOARD under XWayland on the user's desktop,
/// the in-memory clipboard of the headless platform in tests) plus the PRIMARY selection and the clipboard ring.
/// <para>
/// Every call hops to the UI thread only to start the platform operation and never waits there: the platform clipboard's own reads are
/// asynchronous (the X11 owner answers through the event loop), and each read is bounded by <see cref="ReadTimeout"/>, after which it counts
/// as empty, so an owner that never answers costs nothing but an empty paste. What CsCode wrote last is remembered with its shape (whole lines,
/// block, one piece per caret) and handed back while the clipboard still holds that exact text; other applications' text reads as plain text.
/// The ring (Ctrl+Shift+V) is in memory only.
/// </para>
/// </summary>
public sealed class ClipboardService : IClipboardService, IDisposable
{
    /// <summary>How long a read waits for the clipboard owner (another process) before giving up.</summary>
    public static TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(2);

    private readonly IUiDispatcher _ui;
    private readonly Func<IPlatformClipboard?> _platform;
    private readonly Lazy<IPrimarySelectionBackend?> _primary;
    private readonly Emitter<ClipboardText> _ringChanged = new();
    private readonly Emitter<ClipboardText> _written = new();
    private readonly object _gate = new();
    private readonly LinkedList<ClipboardText> _ring = new();
    private ClipboardText? _last;
    private Func<string>? _pendingPrimary;
    private bool _disposed;

    /// <param name="primary">Creates the PRIMARY backend on first use (null: none). Default: X11 when <c>$DISPLAY</c> is set.</param>
    public ClipboardService(IUiDispatcher ui, Func<IPlatformClipboard?> platform, Func<IPrimarySelectionBackend?>? primary = null)
    {
        _ui = ui;
        _platform = platform;
        _primary = new Lazy<IPrimarySelectionBackend?>(primary ?? DefaultPrimary, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private static IPrimarySelectionBackend? DefaultPrimary() => OperatingSystem.IsLinux() ? X11PrimarySelection.TryCreate() : null;

    // ------------------------------------------------------------------ CLIPBOARD

    public async ValueTask<ClipboardText?> ReadAsync(CancellationToken ct = default)
    {
        var text = await WithTimeout(ui => ui.TryGetTextAsync(), ct).ConfigureAwait(false);
        if (text is null) return null;
        var last = Volatile.Read(ref _last);
        return last is not null && SameText(last.Text, text) ? last with { Text = text } : ClipboardText.Plain(text);
    }

    public async ValueTask<bool> HasTextAsync(CancellationToken ct = default)
    {
        var has = await WithTimeout(async ui => (object?)await ui.HasTextAsync().ConfigureAwait(true), ct, TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        return has is true;
    }

    public async ValueTask WriteAsync(ClipboardText content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        Volatile.Write(ref _last, content);
        AddToRing(content);
        _written.Fire(content);
        if (_disposed) return;
        // Setting the clipboard makes CsCode its owner; no other process is involved until someone pastes, so this does not wait on anyone.
        var set = await _ui.InvokeAsync(() => SetOnUi(content)).ConfigureAwait(false);
        try { await set.ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* no clipboard (a closing window): the ring still has the copy */ }
    }

    private Task SetOnUi(ClipboardText content) => _platform() is { } clipboard ? clipboard.SetAsync(content.Text, content.Html) : Task.CompletedTask;

    /// <summary>Starts a platform clipboard call on the UI thread and waits for it off the UI thread, at most <paramref name="timeout"/>.</summary>
    private async Task<T?> WithTimeout<T>(Func<IPlatformClipboard, Task<T?>> call, CancellationToken ct, TimeSpan? timeout = null) where T : class
    {
        if (_disposed) return null;
        var started = await _ui.InvokeAsync(() => _platform() is { } clipboard ? call(clipboard) : Task.FromResult<T?>(null)).ConfigureAwait(false);
        var limit = Task.Delay(timeout ?? ReadTimeout, ct);
        if (await Task.WhenAny(started, limit).ConfigureAwait(false) != started)
        {
            ct.ThrowIfCancellationRequested();
            _ = started.ContinueWith(t => _ = t.Exception, TaskScheduler.Default); // observe a late failure
            return null;
        }
        try { return await started.ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException) { return null; } // owner died mid-transfer, no clipboard
    }

    private static bool SameText(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal)) return true;
        // A platform may translate line endings on the way through (Windows: LF to CRLF); the shape still belongs to this text.
        return Math.Abs(a.Length - b.Length) <= Math.Max(a.Length, b.Length) / 2
            && string.Equals(ClipboardTextExtensions.NormalizeLineEndings(a, "\n"), ClipboardTextExtensions.NormalizeLineEndings(b, "\n"), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ ring

    public IReadOnlyList<ClipboardText> Ring
    {
        get { lock (_gate) return _ring.ToArray(); }
    }

    public IEventSource<ClipboardText> RingChanged => _ringChanged;

    /// <summary>
    /// Fires for every write, on the writer's thread, before the platform clipboard is set: also for the copies the ring leaves out (empty, over
    /// 1 MB). For caches of what the clipboard holds (Vim's <c>+</c> register), which must follow every copy CsCode makes.
    /// </summary>
    public IEventSource<ClipboardText> Written => _written;

    private void AddToRing(ClipboardText content)
    {
        if ((long)content.Text.Length * sizeof(char) > ClipboardLimits.RingEntryMaxBytes || content.Text.Length == 0) return;
        lock (_gate)
        {
            // Copying the same text again moves it to the top rather than filling the ring with duplicates.
            for (var node = _ring.First; node is not null; node = node.Next)
                if (string.Equals(node.Value.Text, content.Text, StringComparison.Ordinal)) { _ring.Remove(node); break; }
            _ring.AddFirst(content with { Html = null }); // the HTML rendition is for other applications; the ring pastes text
            while (_ring.Count > ClipboardLimits.RingCapacity) _ring.RemoveLast();
        }
        _ringChanged.Fire(content);
    }

    // ------------------------------------------------------------------ PRIMARY

    public bool IsPrimarySelectionSupported => _primary.Value?.IsAvailable == true;

    public void SetPrimarySelection(Func<string>? provider)
    {
        if (_disposed) return;
        // Creating the X11 backend connects on its own thread; this call only records the provider and enqueues.
        Volatile.Write(ref _pendingPrimary, provider);
        _primary.Value?.SetOwner(provider);
    }

    public async ValueTask<string?> ReadPrimarySelectionAsync(CancellationToken ct = default)
    {
        if (_disposed || _primary.Value is not { IsAvailable: true } primary) return null;
        try { return await primary.ReadAsync(ReadTimeout, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException) { return null; }
    }

    /// <summary>The provider CsCode last offered as PRIMARY (tests).</summary>
    internal Func<string>? PrimaryProvider => Volatile.Read(ref _pendingPrimary);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_primary.IsValueCreated) _primary.Value?.Dispose();
        lock (_gate) _ring.Clear();
        _ringChanged.Clear();
        _written.Clear();
    }
}

/// <summary>An in-process PRIMARY selection (the headless platform has none): what the editor offers is what a middle-click reads.</summary>
public sealed class InMemoryPrimarySelection : IPrimarySelectionBackend
{
    private volatile Func<string>? _provider;
    private string? _external;

    public bool IsAvailable => true;
    public void SetOwner(Func<string>? provider) { _provider = provider; _external = null; }

    /// <summary>Simulates another application taking PRIMARY with <paramref name="text"/>.</summary>
    public void SetExternal(string? text) { _provider = null; _external = text; }

    public Func<string>? Provider => _provider;

    public Task<string?> ReadAsync(TimeSpan timeout, CancellationToken ct) =>
        _provider is { } p ? Task.Run<string?>(() => p(), ct) : Task.FromResult(_external);

    public void Dispose() => _provider = null;
}
