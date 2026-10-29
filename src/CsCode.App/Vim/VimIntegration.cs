using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CsCode.App.Docking;
using CsCode.App.Input;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor;
using CsCode.Editor.Documents;
using CsCode.Editor.View;
using CsCode.Ui.Threading;
using CsCode.Vim;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.App.Vim;

/// <summary>
/// The Vim adapter: connects the Vim engine (<c>CsCode.Vim</c>) to the real editor views and the shell. It lives in the shell because only the
/// composition root may reference both the editor and the Vim module (docs/ARCHITECTURE.md §2); the editor offers a Vim-agnostic modal surface
/// (<see cref="EditorModalSurface"/>), the engine a host abstraction (<see cref="IVimHost"/>), and this class joins them.
/// <list type="bullet">
/// <item><b>Lifecycle.</b> One engine per editor view, created when the view is created (<see cref="EditorViewFactory.ViewOpened"/>) and disposed
/// when the view is destroyed (<see cref="EditorViewFactory.ViewClosing"/>, i.e. <c>DisposeView</c>); one <see cref="EditorVimBuffer"/> per open
/// document, shared by its views, released when the document closes. <c>vim.enable</c> / Toggle Vim Mode switch everything off (plain editor
/// caret, keys and status bar at once) and on again (engines for the open views).</item>
/// <item><b>Keys.</b> The main window's tunnelling handlers offer every key and text input to <see cref="OnKeyDown"/>/<see cref="OnTextInput"/>
/// before the shell's key bindings (<see cref="VimKeyPolicy"/> decides key presses; characters and IME commits are text input).</item>
/// <item><b>Threading.</b> Everything runs on the UI thread inside the key event; the engine's sliced work continues on Background-priority
/// dispatcher callbacks of at most <see cref="SliceBudget"/>. Nothing here waits: shell actions and app commands are started and left running.</item>
/// </list>
/// </summary>
public sealed class VimIntegration : IShellKeyInterceptor, IDisposable
{
    public const string ModeItemId = "vim.mode";
    public const string CommandLineItemId = "vim.cmdline";
    public const string ShowCommandItemId = "vim.showcmd";
    public const string ToggleItemId = "vim.toggle";
    public const string OutputChannel = "Vim";
    /// <summary>Origin of the key bindings the integration adds (gD gi ]d [d g.); a vimrc or settings binding of the same keys replaces them.</summary>
    public const string IntegrationOrigin = "cscode.integration";

    public static readonly TimeSpan SliceBudget = TimeSpan.FromMilliseconds(3);
    internal static readonly TimeSpan YankFlashDuration = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ClipboardWait = TimeSpan.FromMilliseconds(500);

    private readonly IVimService _vim;
    private readonly EditorViewFactory _editors;
    private readonly DocumentWell _well;
    private readonly ICommandService _commands;
    private readonly IStatusBarService _status;
    private readonly IConfigurationService? _config;
    private readonly IUiDispatcher _ui;
    private readonly IOutputService? _output;
    private readonly IDocumentManager? _documents;
    private readonly WindowHolder _windowHolder;
    private readonly DisposableStore _subscriptions = new();
    private readonly Dictionary<CodeEditorControl, VimView> _views = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<EditorDocument, EditorVimBuffer> _buffers = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, IDisposable> _statusItems = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _statusTexts = new(StringComparer.Ordinal);
    private readonly Queue<(VimView View, KeyStroke? Key, string? Text)> _held = new();
    private IReadOnlyDictionary<KeyStroke, bool> _handleKeys = new Dictionary<KeyStroke, bool>();
    private Func<bool>? _chordPending;
    private VimView? _current;
    private (VimView? View, int TabSize, bool Spaces) _synced;
    private VimSearchHighlight? _search;
    private System.Text.RegularExpressions.Regex? _searchRegex;
    private bool _started, _enabled, _disposed, _lastKeyDownToVim;
    private int _holdGeneration;

    private VimIntegration(IServiceProvider sp, IVimService vim, EditorViewFactory editors)
    {
        _vim = vim;
        _editors = editors;
        _well = sp.GetRequiredService<DocumentWell>();
        _commands = sp.GetRequiredService<ICommandService>();
        _status = sp.GetRequiredService<IStatusBarService>();
        _config = sp.GetService<IConfigurationService>();
        _ui = sp.GetRequiredService<IUiDispatcher>();
        _output = sp.GetService<IOutputService>();
        _documents = sp.GetService<IDocumentManager>();
        _windowHolder = sp.GetRequiredService<WindowHolder>();
        Clipboard = new VimClipboard(() => _windowHolder.Window);
        Actions = new VimShellActions(this, _well, sp.GetRequiredService<ShellDockFactory>(), sp.GetService<IWindowService>(), sp.GetService<IWorkspaceService>());
    }

    /// <summary>The adapter for a composition with the Vim module and the real editor; null otherwise (fakes in the dev host, tests without Vim).</summary>
    public static VimIntegration? TryCreate(IServiceProvider sp) =>
        sp.GetService<IVimService>() is { } vim && sp.GetService<IEditorViewFactory>() is EditorViewFactory editors ? new VimIntegration(sp, vim, editors) : null;

    public IVimService Service => _vim;
    public bool Enabled => _enabled;
    internal VimClipboard Clipboard { get; }
    internal VimShellActions Actions { get; }
    internal TopLevel? Window => _windowHolder.Window;

    /// <summary>The engine of a view (null when Vim is off or the control is not an editor view).</summary>
    public VimEngine? EngineOf(Control view) => view is CodeEditorControl c && _views.TryGetValue(c, out var v) ? v.Engine : null;

    /// <summary>Number of live engines (one per editor view while Vim is on).</summary>
    public int EngineCount => _views.Count;

    /// <summary>Number of document buffers held (one per open document that had a view while Vim was on).</summary>
    public int BufferCount => _buffers.Count;

    internal EditorVimBuffer? BufferOf(EditorDocument document) => _buffers.GetValueOrDefault(document);

    /// <summary>Keys waiting for a clipboard read (tests).</summary>
    internal int HeldKeyCount => _held.Count;

    /// <summary>Wires the adapter (UI thread, once): the window's key interceptor calls arrive after this. <paramref name="chordPending"/> tells
    /// whether the shell is in the middle of a key chord (its second key must not go to Vim).</summary>
    public void Start(Func<bool> chordPending)
    {
        if (_started) return;
        _started = true;
        _chordPending = chordPending;
        _handleKeys = VimKeyPolicy.ParseHandleKeys(_config?.GetRaw(VimSettings.HandleKeys));
        if (_config is not null)
            _subscriptions.Add(_config.Changed.Subscribe(e =>
            {
                if (e.Affects(VimSettings.HandleKeys)) _ui.Post(() => _handleKeys = VimKeyPolicy.ParseHandleKeys(_config.GetRaw(VimSettings.HandleKeys)));
            }));
        _subscriptions.Add(_vim.EnabledChanged.Subscribe(on => _ui.Post(() => SetEnabled(on))));
        _subscriptions.Add(_editors.ViewOpened.Subscribe(view => { if (_enabled) Attach(view); }));
        _subscriptions.Add(_editors.ViewClosing.Subscribe(Detach));
        if (_documents is not null)
            _subscriptions.Add(_documents.Closed.Subscribe(e => _ui.Post(() => ReleaseBuffer(e.Document))));
        if (_windowHolder.Window is WindowBase window)
        {
            // Text copied in another application reaches "+p / "*p: the clipboard is read when the window becomes active (and on editor focus).
            void OnActivated(object? s, EventArgs e) => _ = Clipboard.RefreshAsync();
            window.Activated += OnActivated;
            _subscriptions.Add(Disposable.Create(() => window.Activated -= OnActivated));
        }
        InstallIntegrationBindings();
        SetEnabled(_vim.Enabled);
    }

    // ------------------------------------------------------------------------------------------------------------ on / off

    private void SetEnabled(bool on)
    {
        if (_disposed) return;
        if (_enabled == on && (on || _views.Count == 0))
        {
            UpdateToggleItem();
            return;
        }
        _enabled = on;
        if (on)
        {
            foreach (var view in _editors.Views) Attach(view);
        }
        else
        {
            foreach (var view in _views.Keys.ToArray()) Detach(view);
            foreach (var buffer in _buffers.Values) buffer.Dispose();
            _buffers.Clear();
            _held.Clear();
            _search = null;
            _searchRegex = null;
            _current = null;
        }
        UpdateStatus();
        UpdateToggleItem();
    }

    private void Attach(CodeEditorControl view)
    {
        if (_disposed || _views.ContainsKey(view)) return;
        var surface = view.Modal;
        var buffer = BufferFor(view.Document);
        VimView? created = null;
        var timer = new ThreadingVimTimer(action => _ui.Post(() =>
        {
            if (created is { IsDisposed: false } v) Run(v, () => { action(); return VimKeyResult.Handled; });
        }));
        var host = new EditorVimHost(this, surface, buffer, timer);
        var vimView = created = new VimView(view, surface, buffer, host);
        host.View = vimView;
        surface.Attach(redo => UndoRedo(vimView, redo));
        buffer.SetActiveEngine(null);
        var engine = _vim.CreateEngine(host);
        vimView.Engine = engine;
        _views[view] = vimView;
        vimView.Subscriptions.Add(engine.PendingWorkChanged.Subscribe(pending => { if (pending) SchedulePump(vimView); }));
        void OnFocus(object? s, RoutedEventArgs e) => OnViewFocused(vimView);
        view.GotFocus += OnFocus;
        vimView.Subscriptions.Add(Disposable.Create(() => view.GotFocus -= OnFocus));
        if (_searchRegex is not null) surface.SetPatternHighlight(_searchRegex);
        // The engine reports its mode only when it changes: publish the starting state (Normal: caret clamped onto a character, block caret).
        buffer.SetActiveEngine(engine);
        if (engine.Mode == VimMode.Normal) engine.Reset();
        else ApplyCaretShape(vimView, engine.Mode);
        if (surface.HasKeyboardFocus || _current is null) OnViewFocused(vimView);
    }

    private void Detach(CodeEditorControl view)
    {
        if (!_views.Remove(view, out var v)) return;
        v.IsDisposed = true;
        v.Subscriptions.Dispose();
        if (ReferenceEquals(v.Buffer.ActiveEngine, v.Engine)) v.Buffer.SetActiveEngine(null);
        v.Engine.Dispose();
        v.Host.View = null;
        v.Surface.Detach();
        if (ReferenceEquals(_current, v))
        {
            _current = null;
            UpdateStatus();
        }
        if (ReferenceEquals(_synced.View, v)) _synced = default;
    }

    private EditorVimBuffer BufferFor(EditorDocument document)
    {
        if (_buffers.TryGetValue(document, out var buffer)) return buffer;
        buffer = new EditorVimBuffer(document);
        _buffers[document] = buffer;
        return buffer;
    }

    private void ReleaseBuffer(ITextDocument document)
    {
        if (document is not EditorDocument doc || !_buffers.TryGetValue(doc, out var buffer)) return;
        if (_views.Values.Any(v => ReferenceEquals(v.Buffer, buffer))) return; // a view still shows it (closing races the document event)
        _buffers.Remove(doc);
        buffer.Dispose();
    }

    private void ApplyCaretShape(VimView v, VimMode mode)
    {
        var shape = _vim.Session.CursorStyles.GetValueOrDefault(mode, mode is VimMode.Insert ? VimCursorShape.Line : VimCursorShape.Block);
        v.Host.OnModeChanged(mode, new VimModeInfo(shape, mode.ToString().ToUpperInvariant()));
    }

    // ------------------------------------------------------------------------------------------------------------ keys

    public bool OnKeyDown(KeyEventArgs e)
    {
        if (!_enabled || _disposed || Find(e.Source) is not { } v) return false;
        if (_chordPending?.Invoke() == true) return false; // the second key of a shell chord (Ctrl+K Ctrl+C) is the shell's
        if (!AvaloniaKeyMapper.TryMap(e.Key, e.KeyModifiers, out var stroke)) return false;
        if (VimKeyPolicy.IsTextKey(stroke))
        {
            _lastKeyDownToVim = false;
            return false; // characters arrive as text input (keyboard layout and IME applied)
        }
        var mode = v.Engine.Mode;
        var popups = Popups(v);
        if (VimKeyPolicy.Decide(stroke, mode, popups, _handleKeys) == VimKeyOwner.App)
        {
            _lastKeyDownToVim = false;
            return false;
        }
        var insert = mode is VimMode.Insert or VimMode.Replace;
        if (insert && stroke is { Ctrl: true, Key: "n" or "p" } && (popups & EditorPopups.Completion) != 0)
        {
            v.Surface.MoveCompletionSelection(stroke.Key == "n" ? 1 : -1);
            _lastKeyDownToVim = true;
            return true;
        }
        if (stroke is { Key: "escape", Ctrl: false, Alt: false, Shift: false })
        {
            // a snippet session ends with Insert mode; hover and parameter hints close as well
            if (insert && (popups & EditorPopups.Snippet) != 0) v.Surface.EndSnippet();
            v.Surface.CloseInfoPopups();
        }
        var handled = Feed(v, stroke, null);
        _lastKeyDownToVim = handled;
        return handled;
    }

    public bool OnTextInput(TextInputEventArgs e)
    {
        if (!_enabled || _disposed || Find(e.Source) is not { } v) return false;
        var text = e.Text;
        if (string.IsNullOrEmpty(text)) return false;
        // some platforms follow Enter/Tab/Backspace with a control character as text: swallow it when Vim took the key press
        if (IsControlText(text)) return _lastKeyDownToVim;
        return Feed(v, null, text);
    }

    private static bool IsControlText(string text)
    {
        foreach (var c in text) if (c >= ' ' && c != '\u007f') return false;
        return true;
    }

    private VimView? Find(object? source)
    {
        if (source is null) return null;
        if (_current is { } c && c.Surface.IsInputElement(source)) return c;
        foreach (var v in _views.Values) if (v.Surface.IsInputElement(source)) return v;
        return null;
    }

    private static EditorPopups Popups(VimView v)
    {
        var p = EditorPopups.None;
        if (v.Surface.IsCompletionOpen) p |= EditorPopups.Completion;
        if (v.Surface.IsSignatureHelpOpen) p |= EditorPopups.SignatureHelp;
        if (v.Surface.IsSnippetActive) p |= EditorPopups.Snippet;
        return p;
    }

    /// <summary>One key (or typed text) into the engine. While the clipboard is being read for a <c>"+</c>/<c>"*</c> command, keys wait in order.</summary>
    private bool Feed(VimView v, KeyStroke? key, string? text)
    {
        if (_held.Count > 0)
        {
            _held.Enqueue((v, key, text));
            return true;
        }
        if (!ReferenceEquals(_current, v)) OnViewFocused(v);
        ClearMessage(v);
        var result = Run(v, () => key is { } k ? v.Engine.HandleKey(k) : v.Engine.HandleText(text!));
        if (result == VimKeyResult.Handled) HoldForClipboard(v);
        return result == VimKeyResult.Handled;
    }

    /// <summary>Runs an engine call for <paramref name="v"/>: its buffer attributes changes to it, its options follow the editor, sliced work continues.</summary>
    private VimKeyResult Run(VimView v, Func<VimKeyResult> call)
    {
        if (v.IsDisposed) return VimKeyResult.PassThrough;
        SyncOptions(v);
        v.Buffer.SetActiveEngine(v.Engine);
        var result = call();
        if (!v.IsDisposed && v.Engine.HasPendingWork) SchedulePump(v);
        return result;
    }

    /// <summary>The editor's tab settings are the source of truth (as in VSCodeVim); <c>:set ts</c> lasts until another view takes the keys.</summary>
    private void SyncOptions(VimView v)
    {
        var tab = Math.Max(1, v.Surface.TabSize);
        var spaces = v.Surface.InsertSpaces;
        if (ReferenceEquals(_synced.View, v) && _synced.TabSize == tab && _synced.Spaces == spaces) return;
        _synced = (v, tab, spaces);
        var o = _vim.Session.Options;
        o.TabStop = tab;
        o.ShiftWidth = tab;
        o.ExpandTab = spaces;
    }

    private void SchedulePump(VimView v)
    {
        if (v.PumpScheduled || v.IsDisposed) return;
        v.PumpScheduled = true;
        _ui.Post(() =>
        {
            v.PumpScheduled = false;
            if (v.IsDisposed || !v.Engine.HasPendingWork) return;
            Run(v, () =>
            {
                v.Engine.RunPendingWork(SliceBudget);
                return VimKeyResult.Handled;
            });
        }, UiPriority.Background);
    }

    /// <summary>
    /// After <c>"+</c> or <c>"*</c> the next key reads the system clipboard, which Avalonia only reads asynchronously: the keys that follow wait
    /// (in order, at most <see cref="ClipboardWait"/>) until the read completes, instead of the UI thread ever blocking on it.
    /// </summary>
    private void HoldForClipboard(VimView v)
    {
        var pending = v.Engine.PendingKeys;
        if (!(pending.EndsWith("\"+", StringComparison.Ordinal) || pending.EndsWith("\"*", StringComparison.Ordinal))) return;
        var generation = ++_holdGeneration;
        _held.Enqueue((v, null, null)); // sentinel: keys queue behind it
        async Task ReleaseAsync()
        {
            try { await Clipboard.RefreshAsync().WaitAsync(ClipboardWait).ConfigureAwait(true); }
            catch (TimeoutException) { }
            catch (Exception) { /* an unreadable clipboard reads as the last known text */ }
            if (generation != _holdGeneration) return;
            ReleaseHeld();
        }
        _ = ReleaseAsync();
    }

    private void ReleaseHeld()
    {
        var keys = _held.ToArray();
        _held.Clear();
        for (var i = 0; i < keys.Length; i++)
        {
            var (view, key, text) = keys[i];
            if (view.IsDisposed || (key is null && text is null)) continue; // the sentinel, or a view closed meanwhile
            Feed(view, key, text);
            if (_held.Count == 0) continue;
            // a released key started another clipboard read: the rest waits for that one
            for (var j = i + 1; j < keys.Length; j++) _held.Enqueue(keys[j]);
            return;
        }
    }

    /// <summary>The editor's Undo/Redo (menu, toolbar, key binding) run Vim's <c>u</c>/<c>Ctrl-R</c>, so the two histories never disagree.</summary>
    private bool UndoRedo(VimView v, bool redo)
    {
        if (!_enabled || v.IsDisposed) return false;
        var engine = v.Engine;
        // A change from before Vim was switched on is not in Vim's history: the editor undoes it itself.
        if (redo ? !engine.CanRedo : !engine.CanUndo) return false;
        Run(v, () =>
        {
            var keys = engine.Mode switch
            {
                VimMode.Insert or VimMode.Replace => redo ? "<C-o><C-r>" : "<C-o>u",
                VimMode.Normal => redo ? "<C-r>" : "u",
                _ => redo ? "<Esc><C-r>" : "<Esc>u",
            };
            engine.TypeKeys(keys);
            return VimKeyResult.Handled;
        });
        return true;
    }

    // ------------------------------------------------------------------------------------------------------------ feedback from the engines

    private void OnViewFocused(VimView v)
    {
        if (v.IsDisposed || ReferenceEquals(_current, v)) return;
        _current = v;
        _ = Clipboard.RefreshAsync();
        UpdateStatus();
    }

    internal void OnModeChanged(VimView v, VimMode mode, in VimModeInfo info)
    {
        if (v.Mode is VimMode.Insert or VimMode.Replace && mode is not (VimMode.Insert or VimMode.Replace or VimMode.InsertNormal))
        {
            // leaving Insert mode: IntelliSense belongs to typing (a list that opened late must not linger over Normal mode)
            v.Surface.CloseCompletion();
            v.Surface.CloseInfoPopups();
        }
        v.Mode = mode;
        v.ModeInfo = info;
        if (ReferenceEquals(v, _current)) UpdateStatus();
    }

    internal void OnMessage(VimView v, in VimMessage message)
    {
        v.Message = message;
        if (message.Text.Contains('\n') && _output is not null)
        {
            // :registers, :marks, :jumps... print several lines: the whole text goes to the Vim output channel, the status bar shows the first line
            var channel = _output.GetOrCreate(OutputChannel);
            channel.AppendLine(message.Text);
        }
        if (ReferenceEquals(v, _current)) UpdateStatus();
    }

    internal void OnShowCommand(VimView v, string pending)
    {
        v.ShowCommand = pending;
        if (ReferenceEquals(v, _current)) UpdateStatus();
    }

    internal void OnRecordingChanged(VimView v, char? register)
    {
        v.Recording = register;
        if (ReferenceEquals(v, _current)) UpdateStatus();
    }

    internal void OnCommandLineChanged(VimView v, in VimCommandLine commandLine)
    {
        v.CommandLine = commandLine;
        if (ReferenceEquals(v, _current)) UpdateStatus();
    }

    /// <summary>'hlsearch' / 'incsearch' are session-wide in Vim: every view shows the same matches.</summary>
    internal void OnSearchHighlight(VimSearchHighlight? highlight)
    {
        if (highlight == _search) return;
        _search = highlight;
        _searchRegex = highlight is { } h ? EditorModalSurface.CompilePattern(h.DotNetPattern, h.IgnoreCase) : null;
        foreach (var v in _views.Values) v.Surface.SetPatternHighlight(_searchRegex);
    }

    /// <summary>The current search highlight pattern (tests).</summary>
    public System.Text.RegularExpressions.Regex? SearchHighlight => _searchRegex;

    internal void ShowError(VimView? v, string text)
    {
        v ??= _current;
        if (v is null) return;
        OnMessage(v, new VimMessage(VimMessageKind.Error, text));
    }

    private void ClearMessage(VimView v)
    {
        if (v.Message is null) return;
        v.Message = null;
        if (ReferenceEquals(v, _current)) UpdateStatus();
    }

    internal void ExecuteCommand(string id, IReadOnlyList<object?>? args)
    {
        var view = _current;
        async Task RunAsync()
        {
            try { await _commands.ExecuteAsync(id, args is null ? [] : args.ToArray()).ConfigureAwait(true); }
            catch (CommandNotFoundException) { ShowError(view, $"E492: Not an editor command: {id}"); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { ShowError(view, $"{id}: {ex.Message}"); }
        }
        _ = RunAsync();
    }

    // ------------------------------------------------------------------------------------------------------------ status bar

    /// <summary>The status bar text of a Vim item, or null when it is not shown (tests).</summary>
    public string? StatusText(string itemId) => _statusTexts.GetValueOrDefault(itemId);

    private void UpdateStatus()
    {
        var v = _enabled ? _current : null;
        if (v is null)
        {
            SetItem(ModeItemId, null);
            SetItem(CommandLineItemId, null);
            SetItem(ShowCommandItemId, null);
            return;
        }
        var cmd = v.CommandLine;
        SetItem(ModeItemId, cmd.Active ? null : ModeText(v), "Vim mode");
        if (cmd.Active) SetItem(CommandLineItemId, CommandLineText(cmd), "Vim command line");
        else if (v.Message is { } m) SetItem(CommandLineItemId, MessageText(m), m.Kind == VimMessageKind.Error ? "Vim error" : "Vim message");
        else SetItem(CommandLineItemId, null);
        SetItem(ShowCommandItemId, v.ShowCommand.Length == 0 ? null : v.ShowCommand, "Pending keys");
    }

    internal static string ModeText(VimView v)
    {
        var label = v.ModeInfo.Label is { Length: > 0 } l ? $"-- {l} --" : "";
        return v.Recording is { } r ? (label.Length > 0 ? label + " " : "") + $"recording @{r}" : label;
    }

    /// <summary>The prompt and text with a cursor mark at the cursor ('▏' inside the text, '█' at the end), as VSCodeVim draws it in its status bar.</summary>
    internal static string CommandLineText(in VimCommandLine c)
    {
        var text = c.Text;
        var at = Math.Clamp(c.CursorIndex, 0, text.Length);
        return c.Prompt + (at == text.Length ? text + "█" : text[..at] + "▏" + text[at..]);
    }

    private static string MessageText(in VimMessage m)
    {
        var text = m.Text;
        var nl = text.IndexOf('\n');
        return nl < 0 ? text : text[..nl] + $"  (+{text.Count(c => c == '\n')} lines in Output › {OutputChannel})";
    }

    private void UpdateToggleItem() =>
        SetItem(ToggleItemId, _enabled ? "Vim" : "Vim: Off", _enabled ? "Vim emulation is on: click to turn it off (Toggle Vim Mode)" : "Vim emulation is off: click to turn it on (Toggle Vim Mode)",
            VimService.ToggleCommand, StatusBarAlignment.Right, priority: 5);

    private void SetItem(string id, string? text, string? tooltip = null, string? command = null, StatusBarAlignment alignment = StatusBarAlignment.Left, int priority = 0)
    {
        if (text is null)
        {
            _statusTexts.Remove(id);
            if (_statusItems.Remove(id, out var gone)) gone.Dispose();
            return;
        }
        if (_statusTexts.TryGetValue(id, out var shown) && shown == text) return;
        _statusTexts[id] = text;
        // The status bar draws "$(name)" as an icon: a zero-width joiner keeps typed text literal. Leading/trailing spaces are kept with no-break spaces.
        var safe = text.Replace("$(", "$‍(", StringComparison.Ordinal).Replace(' ', ' ');
        var order = id switch { ModeItemId => 1_000_002, CommandLineItemId => 1_000_001, ShowCommandItemId => 1_000_000, _ => priority };
        var item = new StatusBarItemModel(id, safe, tooltip, command, alignment, order);
        var old = _statusItems.GetValueOrDefault(id);
        _statusItems[id] = _status.Set(item);
        _ = old; // replaced by id: the newer Set owns the slot, disposing the old token is a no-op
    }

    // ------------------------------------------------------------------------------------------------------------ bindings and options

    /// <summary>
    /// The language-server keys nvim-lspconfig made common, on top of the engine's own gd gr gh K: gD declaration, gi implementation, ]d [d
    /// next/previous problem, g. quick fix. Added only where neither the vimrc nor the settings map those keys already.
    /// </summary>
    private void InstallIntegrationBindings()
    {
        var map = _vim.Session.Mappings;
        void Add(string lhs, string command)
        {
            var keys = lhs.Select(c => (int)c).ToArray();
            if (map.Lookup(MapModes.Normal, keys).Exact is not null) return;
            map.Add(new Mapping { Lhs = keys, Modes = MapModes.Normal, NoRemap = true, LhsText = lhs, Commands = [new VimBindingCommand(command, null)], Origin = IntegrationOrigin });
        }
        Add("gD", "editor.action.goToDeclaration");
        Add("gi", "editor.action.goToImplementation");
        Add("]d", "editor.action.marker.next");
        Add("[d", "editor.action.marker.prev");
        Add("g.", KnownCommands.QuickFix);
        // Enter in Insert mode goes through the editor (language-aware indentation); o and O should keep the indentation too.
        _vim.Session.Options.AutoIndent = true;
        if (_vim.VimrcLoaded.IsCompleted) _ = _vim.ReloadVimrcAsync(); // the vimrc's own settings win over these defaults
    }

    public void Dispose()
    {
        if (_disposed) return;
        foreach (var view in _views.Keys.ToArray()) Detach(view);
        foreach (var buffer in _buffers.Values) buffer.Dispose();
        _buffers.Clear();
        _disposed = true;
        _subscriptions.Dispose();
        foreach (var item in _statusItems.Values) item.Dispose();
        _statusItems.Clear();
        _statusTexts.Clear();
        _held.Clear();
        _vim.Session.Mappings.RemoveOrigin(IntegrationOrigin);
    }
}

/// <summary>The Vim state of one editor view: its engine and host, and what the engine last reported (for the status bar).</summary>
internal sealed class VimView(CodeEditorControl view, EditorModalSurface surface, EditorVimBuffer buffer, EditorVimHost host)
{
    public CodeEditorControl View { get; } = view;
    public EditorModalSurface Surface { get; } = surface;
    public EditorVimBuffer Buffer { get; } = buffer;
    public EditorVimHost Host { get; } = host;
    public VimEngine Engine { get; set; } = null!;
    public DisposableStore Subscriptions { get; } = new();
    public bool IsDisposed { get; set; }
    public bool PumpScheduled { get; set; }
    public bool InVisual { get; set; }
    public VimMode Mode { get; set; }
    public VimModeInfo ModeInfo { get; set; }
    public string ShowCommand { get; set; } = "";
    public char? Recording { get; set; }
    public VimCommandLine CommandLine { get; set; }
    public VimMessage? Message { get; set; }
}
