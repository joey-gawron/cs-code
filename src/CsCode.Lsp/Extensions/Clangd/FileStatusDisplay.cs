using CsCode.Core.Platform;

namespace CsCode.Lsp.Extensions.Clangd;

/// <summary>
/// clangd's <c>textDocument/clangd.fileStatus</c> in the status bar, as vscode-clangd shows it: "clangd: &lt;state&gt;" for the active editor's
/// file while clangd has reported a state for it ("parsing includes", "building AST", "idle"), and nothing for other files. Remembers the last
/// state of the <see cref="MaxFiles"/> files reported most recently; a server that stops takes its files' states with it.
/// </summary>
internal sealed class FileStatusDisplay : IDisposable
{
    public const string ItemId = "clangd.fileStatus";
    internal const int MaxFiles = 512;

    private readonly ServerExtensionHost _host;
    private readonly object _gate = new();
    private readonly Dictionary<Uri, (ServerExtensionSession Session, string State)> _states = [];
    private readonly Queue<Uri> _order = new();
    private readonly IDisposable? _activeChanged;
    private IDisposable? _item;
    private string? _shownText;
    private bool _disposed;

    public FileStatusDisplay(ServerExtensionHost host)
    {
        _host = host;
        _activeChanged = host.Services.Navigation?.ActiveDocumentChanged.Subscribe(_ => Refresh());
    }

    /// <summary>The text shown now (null: no item).</summary>
    internal string? Shown
    {
        get { lock (_gate) return _shownText; }
    }

    public void Set(ServerExtensionSession session, Uri file, string state)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (!_states.ContainsKey(file)) _order.Enqueue(file);
            _states[file] = (session, state);
            while (_states.Count > MaxFiles && _order.TryDequeue(out var oldest)) _states.Remove(oldest);
        }
        Refresh();
    }

    public void Remove(ServerExtensionSession session)
    {
        lock (_gate)
        {
            foreach (var file in _states.Where(e => ReferenceEquals(e.Value.Session, session)).Select(e => e.Key).ToList()) _states.Remove(file);
            if (_order.Any(f => !_states.ContainsKey(f)))
            {
                var kept = _order.Where(_states.ContainsKey).ToList();
                _order.Clear();
                foreach (var f in kept) _order.Enqueue(f);
            }
        }
        Refresh();
    }

    private void Refresh()
    {
        var active = _host.Services.Navigation?.ActiveDocument?.Uri;
        lock (_gate)
        {
            if (_disposed) return;
            var text = active is not null && _states.TryGetValue(active, out var s) ? "clangd: " + s.State : null;
            if (text == _shownText) return;
            _shownText = text;
            if (text is null || _host.Services.StatusBar is not { } bar)
            {
                _item?.Dispose();
                _item = null;
                return;
            }
            // Set replaces the item of the same id
            _item = bar.Set(new StatusBarItemModel(ItemId, text, "clangd's state for this file", null, StatusBarAlignment.Left, Priority: -10));
        }
    }

    public void Dispose()
    {
        _activeChanged?.Dispose();
        lock (_gate)
        {
            _disposed = true;
            _item?.Dispose();
            _item = null;
            _shownText = null;
            _states.Clear();
            _order.Clear();
        }
    }
}
