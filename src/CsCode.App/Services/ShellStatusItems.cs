using CsCode.App.Notifications;
using CsCode.Core.Debugging;
using CsCode.Core.Diagnostics;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Ui.Threading;

namespace CsCode.App.Services;

/// <summary>
/// The status-bar items the shell itself owns: Ln/Col, indent, encoding, EOL, language mode, diagnostics counts, memory, debug state,
/// language-server status and the notification bell. Other modules (Vim mode, remote indicator) register their own items through
/// <see cref="IStatusBarService"/>. Sources are optional: an item simply does not appear if its source service is absent.
/// Updates are cheap (record allocation, coalesced by the status bar service); nothing here polls.
/// </summary>
public sealed class ShellStatusItems : IDisposable
{
    private readonly IStatusBarService _bar;
    private readonly IUiDispatcher _ui;
    private readonly NavigationService _nav;
    private readonly IConfigurationService? _config;
    private readonly ILanguageService? _languages;
    private readonly IDiagnosticService? _diagnostics;
    private readonly IMemoryMonitor? _memory;
    private readonly ILanguageServerManager? _servers;
    private readonly NotificationCenter _notifications;
    private readonly DisposableStore _subs = new();
    private readonly Dictionary<string, IDisposable> _items = new();

    public ShellStatusItems(IStatusBarService bar, IUiDispatcher ui, NavigationService nav, NotificationCenter notifications, IConfigurationService? config = null,
        ILanguageService? languages = null, IDiagnosticService? diagnostics = null, IDebugService? debug = null, IMemoryMonitor? memory = null, ILanguageServerManager? servers = null)
    {
        _bar = bar;
        _ui = ui;
        _nav = nav;
        _notifications = notifications;
        _config = config;
        _languages = languages;
        _diagnostics = diagnostics;
        _memory = memory;
        _servers = servers;

        _subs.Add(nav.ActiveCaretChanged.Subscribe(_ => UpdateEditorItems()));
        _subs.Add(nav.ActiveDocumentChanged.Subscribe(_ => UpdateEditorItems()));
        if (languages is not null) _subs.Add(languages.LanguagesChanged.Subscribe(_ => UpdateEditorItems()));
        if (diagnostics is not null)
        {
            _subs.Add(diagnostics.Changed.Subscribe(_ => _ui.PostCoalesced("status.diagnostics", UpdateDiagnostics)));
            UpdateDiagnostics();
        }
        if (debug is not null)
        {
            _subs.Add(debug.StateChanged.Subscribe(s => _ui.Post(() => UpdateDebug(s))));
            UpdateDebug(debug.State);
        }
        if (memory is not null) _subs.Add(memory.Sampled.Subscribe(s => _ui.PostCoalesced("status.memory", () => UpdateMemory(s))));
        if (servers is not null)
        {
            _subs.Add(servers.ServersChanged.Subscribe(_ => _ui.PostCoalesced("status.servers", UpdateServers)));
            UpdateServers();
        }
        notifications.PropertyChanged += OnNotificationsChanged;
        UpdateBell();
        UpdateEditorItems();
    }

    private void Put(StatusBarItemModel item)
    {
        if (_items.TryGetValue(item.Id, out var old)) old.Dispose();
        _items[item.Id] = _bar.Set(item);
    }

    private void Remove(string id)
    {
        if (_items.Remove(id, out var old)) old.Dispose();
    }

    private void UpdateEditorItems()
    {
        if (_nav.ActiveDocument is not { } doc)
        {
            foreach (var id in new[] { "editor.position", "editor.indent", "editor.encoding", "editor.eol", "editor.language" }) Remove(id);
            return;
        }
        var caret = _nav.ActiveCaret ?? TextPosition.Zero;
        Put(new StatusBarItemModel("editor.position", $"Ln {caret.Line + 1}, Col {caret.Character + 1}", "Go to Line/Column", "workbench.action.gotoLine", StatusBarAlignment.Right, 100));
        var tab = _config?.Get(SettingKeys.EditorTabSize, 4) is > 0 and var configured ? configured : 4;
        var spaces = _config?.Get(SettingKeys.EditorInsertSpaces, true) ?? true;
        Put(new StatusBarItemModel("editor.indent", $"{(spaces ? "Spaces" : "Tab Size")}: {tab}", "Select Indentation", null, StatusBarAlignment.Right, 90));
        Put(new StatusBarItemModel("editor.encoding", doc.EncodingName.ToUpperInvariant(), "Select Encoding", null, StatusBarAlignment.Right, 80));
        Put(new StatusBarItemModel("editor.eol", doc.EndOfLine switch { "\r\n" => "CRLF", "\r" => "CR", _ => "LF" }, "Select End of Line Sequence", null, StatusBarAlignment.Right, 70));
        var language = _languages?.Get(doc.LanguageId)?.DisplayName ?? doc.LanguageId;
        Put(new StatusBarItemModel("editor.language", language, "Select Language Mode", Commands.ShellCommands.ChangeLanguageMode, StatusBarAlignment.Right, 60));
    }

    private void UpdateDiagnostics()
    {
        if (_diagnostics is null) return;
        var (e, w, _, _) = _diagnostics.Counts;
        Put(new StatusBarItemModel("diagnostics", $"$(error) {e}  $(warning) {w}", "Problems: click to open the Error List", Commands.ShellCommands.ShowErrorList, StatusBarAlignment.Left, 50));
    }

    private void UpdateDebug(DebugState state)
    {
        if (state == DebugState.Inactive) { Remove("debug.state"); return; }
        Put(new StatusBarItemModel("debug.state", state switch { DebugState.Running => "$(debug-alt) Running", DebugState.Stopped => "$(debug-alt) Paused", _ => "$(debug-alt) Starting..." },
            "Debug session", null, StatusBarAlignment.Left, 40));
    }

    private void UpdateMemory(MemorySnapshot s)
    {
        var mb = s.WorkingSetBytes / (1024 * 1024);
        var stats = _ui.Stats;
        Put(new StatusBarItemModel("memory", $"$(pulse) {mb} MB",
            $"Working set {mb} MB, GC heap {s.GcHeapBytes / (1024 * 1024)} MB\nUI stalls: {stats.Stalls}, longest UI callback: {stats.LongestDispatchMs:F0} ms",
            null, StatusBarAlignment.Right, 10));
    }

    private void UpdateServers()
    {
        if (_servers is null) return;
        var servers = _servers.Servers;
        if (servers.Count == 0) { Remove("servers"); return; }
        var running = servers.Count(s => s.State == LanguageServerState.Running);
        var faulted = servers.Count(s => s.State == LanguageServerState.Faulted);
        var tip = string.Join('\n', servers.Select(s => $"{s.DisplayName}: {s.State}{(s.LastError is { } err ? " - " + err : "")}"));
        Put(new StatusBarItemModel("servers", faulted > 0 ? $"$(warning) {faulted} server error{(faulted == 1 ? "" : "s")}" : $"$(plug) {running}", tip, null, StatusBarAlignment.Left, 30));
    }

    private void OnNotificationsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NotificationCenter.Unread)) UpdateBell();
    }

    private void UpdateBell() =>
        Put(new StatusBarItemModel("notifications", _notifications.Unread > 0 ? $"$(bell-dot) {_notifications.Unread}" : "$(bell)", "Notifications", Menus.ShellMenus.ToggleNotifications, StatusBarAlignment.Right, 5));

    public void Dispose()
    {
        _notifications.PropertyChanged -= OnNotificationsChanged;
        _subs.Dispose();
        foreach (var i in _items.Values) i.Dispose();
        _items.Clear();
    }
}
