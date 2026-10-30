using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CsCode.Core.Contributions;
using CsCode.Core.Debugging;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Terminal;
using CsCode.Core.Text;
using CsCode.Core.Theming;
using CsCode.Panels.Terminal.Input;
using CsCode.Panels.Terminal.Rendering;
using CsCode.Panels.Terminal.Views;
using CsCode.Terminal.Emulator;
using CsCode.Terminal.Sessions;
using CsCode.Ui.Menus;
using CsCode.Ui.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Panels.Terminal;

/// <summary>
/// App-lifetime half of the Terminal tool window (the window is a singleton, its view comes and goes): the terminal commands with VS Code's
/// ids and default keys, the keybinding precedence of a focused terminal, the <c>terminalFocus</c> context key, clipboard and link handling,
/// and the appearance (font, colours) the views draw with. Services are resolved lazily: this is built while the shell enumerates the tool
/// windows, before most of the shell exists.
/// </summary>
public sealed class TerminalPanelRuntime : IDisposable
{
    public const string ViewId = TerminalService.ViewId;
    public const string FocusContextKey = "terminalFocus";
    public const string AltBufferContextKey = "terminalAltBufferActive";
    public const string HasSelectionContextKey = "terminalTextSelected";
    public const string ProcessSupportedContextKey = "terminalProcessSupported";

    /// <summary>VS Code's default <c>terminal.integrated.commandsToSkipShell</c> (the subset CsCode has), plus every terminal command.</summary>
    public static readonly string[] DefaultCommandsToSkipShell =
    [
        "workbench.action.quickOpen", "workbench.action.showCommands", "workbench.action.quickOpenView", "workbench.action.togglePanel",
        "workbench.action.toggleMaximizedPanel", "workbench.action.closeQuickOpen", "workbench.action.toggleFullScreen",
        "workbench.action.focusActiveEditorGroup", "workbench.action.focusFirstEditorGroup", "workbench.action.focusLastEditorGroup",
        "workbench.action.nextEditor", "workbench.action.previousEditor", "workbench.action.nextEditorInGroup", "workbench.action.previousEditorInGroup",
        "workbench.action.openNextRecentlyUsedEditor", "workbench.action.openPreviousRecentlyUsedEditor",
        "workbench.action.navigateUp", "workbench.action.navigateDown", "workbench.action.navigateLeft", "workbench.action.navigateRight",
        "workbench.action.debug.start", "workbench.action.debug.run", "workbench.action.debug.stop", "workbench.action.debug.restart",
        "workbench.action.debug.continue", "workbench.action.debug.pause", "workbench.action.debug.stepInto", "workbench.action.debug.stepOut",
        "workbench.action.debug.stepOver", "workbench.action.debug.disconnect", "editor.debug.action.toggleBreakpoint",
        "workbench.action.tasks.build", "workbench.action.tasks.runTask", "workbench.action.tasks.test", "workbench.action.tasks.terminate",
        "editor.action.toggleTabFocusMode", "notifications.hideToasts", "workbench.action.files.save", "workbench.action.files.saveAll",
        "workbench.action.quit",
    ];

    private readonly IServiceProvider _sp;
    private readonly DisposableStore _subs = new();
    private readonly Emitter<object?> _appearance = new();
    private IDisposable? _keyHandler;
    private WeakReference<TerminalView>? _view;
    private WeakReference<TerminalControl>? _focused;
    private bool _focusPending;

    public TerminalPanelRuntime(IServiceProvider services)
    {
        _sp = services;
        Service = services.GetRequiredService<TerminalService>();
        Ui = services.GetService<IUiDispatcher>() ?? new AvaloniaFallbackDispatcher();
        if (Commands is { } commands) _subs.Add(TerminalCommandSet.Register(commands, this));
        if (services.GetService<IContributionRegistry>() is { } contributions)
            _subs.Add(contributions.Contribute(ContributionSources.Builtin, AppContext.BaseDirectory, TerminalCommandSet.Keybindings));
        _subs.Add(Service.PresentationRequested.Subscribe(p => Dispatcher.UIThread.Post(() => OnPresentation(p))));
        if (Config is { } config)
            _subs.Add(config.Changed.Subscribe(e =>
            {
                if (e.Affects("terminal.integrated") || e.Affects("editor.fontFamily") || e.Affects("editor.fontSize")) _appearance.Fire(null);
            }));
        if (services.GetService<IColorThemeService>() is { } themes)
            _subs.Add(themes.Changed.Subscribe(_ =>
            {
                // Changed may fire on any thread; the palette reads Avalonia brushes, which belong to the UI thread (after the applier's update).
                Dispatcher.UIThread.Post(() => Service.Palette = new ThemePalette(), DispatcherPriority.Background);
                _appearance.Fire(null);
            }));
        Service.Palette = new ThemePalette();
        Keys?.Set(ProcessSupportedContextKey, true);
        _keyHandler = InputElement.KeyDownEvent.AddClassHandler<TopLevel>(OnTopLevelKeyDown, RoutingStrategies.Tunnel);
    }

    public TerminalService Service { get; }
    public IUiDispatcher Ui { get; }
    internal IServiceProvider ServiceProvider => _sp;
    public ICommandService? Commands => Lazy(ref _commands);
    public IContextKeyService? Keys => Lazy(ref _keys);
    public IKeybindingService? Bindings => Lazy(ref _bindings);
    public IConfigurationService? Config => Lazy(ref _config);
    public IWindowService? Window => Lazy(ref _window);
    public IClipboardService? Clipboard => Lazy(ref _clipboard);
    public INavigationService? Navigation => Lazy(ref _navigation);
    public IWorkspaceService? Workspace => Lazy(ref _workspace);
    public IViewActivator? Views => Lazy(ref _views);
    public IContributionRegistry? Contributions => Lazy(ref _contributions);

    private ICommandService? _commands;
    private IContextKeyService? _keys;
    private IKeybindingService? _bindings;
    private IConfigurationService? _config;
    private IWindowService? _window;
    private IClipboardService? _clipboard;
    private INavigationService? _navigation;
    private IWorkspaceService? _workspace;
    private IViewActivator? _views;
    private IContributionRegistry? _contributions;

    /// <summary>
    /// Resolved on first use (this object is built while the shell enumerates the tool windows, before most services can be built) and kept,
    /// so views disposed while the container shuts down never ask a disposed provider.
    /// </summary>
    private T? Lazy<T>(ref T? field) where T : class
    {
        if (field is not null) return field;
        try { return field = _sp.GetService<T>(); }
        catch (ObjectDisposedException) { return null; }
    }

    /// <summary>Font, colours or cursor settings changed: views re-read them (fires on any thread).</summary>
    public IEventSource<object?> AppearanceChanged => _appearance;

    // ------------------------------------------------------------------ settings

    internal TerminalFont CreateFont()
    {
        var family = Config?.Get<string>(TerminalSettingKeys.FontFamily) is { Length: > 0 } f ? f
            : Config?.Get<string>(SettingKeys.EditorFontFamily) is { Length: > 0 } ef ? ef
            : "Cascadia Code, Cascadia Mono, Consolas, DejaVu Sans Mono, Liberation Mono, Noto Sans Mono, monospace";
        var size = Config?.Get(TerminalSettingKeys.FontSize, 0.0) is > 0 and var s ? s : Config?.Get(SettingKeys.EditorFontSize, 14.0) ?? 14.0;
        return TerminalFont.Create(family, size, Config?.Get(TerminalSettingKeys.LineHeight, 1.0) ?? 1.0, Config?.Get(TerminalSettingKeys.LetterSpacing, 0.0) ?? 0.0);
    }

    internal TerminalColors Colors() =>
        TerminalColors.FromTheme(Config?.Get(TerminalSettingKeys.DrawBoldTextInBrightColors, true) ?? true, Config?.Get(TerminalSettingKeys.MinimumContrastRatio, 4.5) ?? 4.5);

    public CursorShape CursorStyle => (Config?.Get<string>(TerminalSettingKeys.CursorStyle) ?? "block") switch
    {
        "line" => CursorShape.Bar,
        "underline" => CursorShape.Underline,
        _ => CursorShape.Block,
    };

    public bool CursorBlinking(TerminalSnapshot? snapshot) => snapshot?.CursorBlink ?? Config?.Get(TerminalSettingKeys.CursorBlinking, false) ?? false;

    public string WordSeparators => Config?.Get<string>(TerminalSettingKeys.WordSeparators) ?? TerminalSettingKeys.DefaultWordSeparators;
    public bool CopyOnSelection => Config?.Get(TerminalSettingKeys.CopyOnSelection, false) ?? false;

    /// <summary>The effective commandsToSkipShell: the defaults and every terminal command, plus the user's entries, minus the '-id' ones.</summary>
    public HashSet<string> CommandsToSkipShell()
    {
        var set = new HashSet<string>(DefaultCommandsToSkipShell, StringComparer.Ordinal);
        foreach (var id in TerminalCommandSet.Ids) set.Add(id);
        foreach (var entry in Config?.Get<string[]>(TerminalSettingKeys.CommandsToSkipShell) ?? [])
        {
            if (entry.StartsWith('-')) set.Remove(entry[1..]);
            else set.Add(entry);
        }
        return set;
    }

    // ------------------------------------------------------------------ views and focus

    internal void Attach(TerminalView view) => _view = new WeakReference<TerminalView>(view);

    internal void Detach(TerminalView view)
    {
        if (_view is not null && _view.TryGetTarget(out var current) && ReferenceEquals(current, view)) _view = null;
    }

    /// <summary>The live Terminal view, if the tool window is shown.</summary>
    public TerminalView? View => _view is not null && _view.TryGetTarget(out var v) && !v.IsDisposed ? v : null;

    /// <summary>The terminal control that has keyboard focus, if any.</summary>
    public TerminalControl? FocusedControl => _focused is not null && _focused.TryGetTarget(out var c) && c.IsKeyboardFocusWithin ? c : null;

    /// <summary>
    /// The control marks the <c>terminalFocus</c> context key itself (the shell classifies tool windows as <c>panelFocus</c>); the shell's own
    /// tracker clears it when focus moves to another area.
    /// </summary>
    internal void SetTerminalFocus(bool focused, TerminalControl control)
    {
        if (focused)
        {
            _focused = new WeakReference<TerminalControl>(control);
            Keys?.Set(FocusContextKey, true);
            Keys?.Set("panelFocus", true);
            // The main window classifies focus in its own GotFocus handler, which runs after the control's and resets the key for a
            // "panel": set it again once the event is done (Normal priority runs before any pending input).
            Dispatcher.UIThread.Post(() =>
            {
                if (_focused is not null && _focused.TryGetTarget(out var still) && ReferenceEquals(still, control) && control.IsKeyboardFocusWithin)
                    Keys?.Set(FocusContextKey, true);
            });
            return;
        }
        // Only the control that holds the key clears it (a view disposed without ever having focus must not).
        if (_focused is null || !_focused.TryGetTarget(out var c) || !ReferenceEquals(c, control)) return;
        _focused = null;
        Keys?.Set(FocusContextKey, false);
    }

    internal void SetAltBuffer(bool active) => Keys?.Set(AltBufferContextKey, active);

    /// <summary>A view asks whether it should take the keyboard when it appears (a command asked for the Terminal window).</summary>
    internal bool TakeFocusRequest()
    {
        var pending = _focusPending;
        _focusPending = false;
        return pending;
    }

    private void OnPresentation(TerminalPresentation p)
    {
        if (!p.PreserveFocus) _focusPending = true;
        if (View is { } view)
        {
            if (p.Terminal is not null) view.Show(p.Terminal);
            if (!p.PreserveFocus) view.FocusTerminal();
        }
    }

    /// <summary>Shows the Terminal window, creating a terminal when there is none; focuses it unless asked not to.</summary>
    public async Task ShowAsync(bool focus)
    {
        if (focus) _focusPending = true;
        if (Service.Terminals.Count == 0) await Service.CreateAsync(new TerminalCreateOptions { Show = false }).ConfigureAwait(true);
        await Service.ShowAsync(null, preserveFocus: !focus).ConfigureAwait(true);
        if (focus) View?.FocusTerminal();
    }

    // ------------------------------------------------------------------ keybinding precedence

    /// <summary>
    /// VS Code's precedence while a terminal has focus, ahead of the shell's keyboard pipeline (a tunnelling class handler on every top-level
    /// runs before the window's own handler): a key that starts a chord (<c>terminal.integrated.allowChords</c>) or runs a command of
    /// <c>commandsToSkipShell</c> goes to CsCode; every other key — including keys bound to other commands — goes to the shell, unless
    /// <c>sendKeybindingsToShell</c> sends even those. Ctrl+Tab stays with the shell's editor switcher.
    /// </summary>
    private void OnTopLevelKeyDown(TopLevel sender, KeyEventArgs e)
    {
        if (e.Handled || e.Source is not TerminalControl control || control.Runtime != this) return;
        if (e.Key == Key.Tab && (e.KeyModifiers & KeyModifiers.Control) != 0) return;
        if (Bindings is { } bindings && bindings.PendingChord.Count > 0) return; // the second key of a chord belongs to the chord
        if (StrokeOf(e) is { } stroke && GoesToWorkbench(stroke)) return;
        if (control.HandleKey(e)) e.Handled = true;
    }

    internal static KeyStroke? StrokeOf(KeyEventArgs e)
    {
        var name = KeyNames.NameOf(e.Key);
        if (name is null) return null;
        var m = e.KeyModifiers;
        return new KeyStroke(name, (m & KeyModifiers.Control) != 0, (m & KeyModifiers.Shift) != 0, (m & KeyModifiers.Alt) != 0, (m & KeyModifiers.Meta) != 0);
    }

    /// <summary>Whether CsCode (not the shell) gets this stroke while the terminal has focus. Only consulted on key presses in a terminal.</summary>
    public bool GoesToWorkbench(KeyStroke stroke)
    {
        if (Bindings is not { } bindings || Commands is not { } commands) return false;
        var keys = Keys;
        var allowChords = Config?.Get(TerminalSettingKeys.AllowChords, true) ?? true;
        var sendToShell = Config?.Get(TerminalSettingKeys.SendKeybindingsToShell, false) ?? false;
        var all = bindings.All; // ascending precedence
        var removed = new HashSet<(string, string)>();
        string? exact = null;
        var chord = false;
        for (var i = all.Count - 1; i >= 0; i--)
        {
            var b = all[i];
            if (b.Chord.Count == 0 || b.Chord[0] != stroke) continue;
            var chordKey = string.Join(' ', b.Chord);
            if (b.IsNegative) { removed.Add((b.Command.TrimStart('-'), chordKey)); continue; }
            if (removed.Contains((b.Command, chordKey)) || !commands.Has(b.Command) || !(keys?.Evaluate(b.When) ?? b.When is null)) continue;
            if (b.Chord.Count > 1) chord = true;
            else exact ??= b.Command;
        }
        if (chord && allowChords && stroke.Key != "escape") return true;
        if (exact is null) return false;
        return !sendToShell && CommandsToSkipShell().Contains(exact);
    }

    // ------------------------------------------------------------------ clipboard

    public async Task CopyAsync(string text)
    {
        if (Clipboard is { } clipboard) await clipboard.WriteAsync(ClipboardText.Plain(text)).ConfigureAwait(true);
        else if (Window is { } window) await window.CopyToClipboardAsync(text).ConfigureAwait(true);
    }

    public void SetPrimarySelection(Func<string> provider)
    {
        if (Clipboard is { IsPrimarySelectionSupported: true } clipboard) clipboard.SetPrimarySelection(provider);
    }

    public async Task PasteAsync(TerminalControl control)
    {
        if (Clipboard is not { } clipboard) return;
        var content = await clipboard.ReadAsync().ConfigureAwait(true);
        if (content?.Text is { Length: > 0 } text) await PasteTextAsync(control, text).ConfigureAwait(true);
    }

    public async Task PastePrimaryAsync(TerminalControl control)
    {
        if (Clipboard is not { IsPrimarySelectionSupported: true } clipboard) return;
        if (await clipboard.ReadPrimarySelectionAsync().ConfigureAwait(true) is { Length: > 0 } text) await PasteTextAsync(control, text).ConfigureAwait(true);
    }

    /// <summary>A paste, after the multi-line warning (<c>terminal.integrated.enableMultiLinePasteWarning</c>: auto warns only without bracketed paste).</summary>
    internal async Task PasteTextAsync(TerminalControl control, string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n').Length;
        var mode = Config?.Get<string>(TerminalSettingKeys.EnableMultiLinePasteWarning) ?? "auto";
        var bracketed = control.Session?.Modes.BracketedPaste ?? false;
        var warn = lines > 1 && mode switch { "never" => false, "always" => true, _ => !bracketed };
        if (warn && Window is { } window)
        {
            var answer = await window.ShowMessageAsync(MessageSeverity.Warning, $"Are you sure you want to paste {lines} lines of text into the terminal?",
                [new MessageAction("Paste"), new MessageAction("Paste as one line"), new MessageAction("Cancel", IsCloseAffordance: true)]).ConfigureAwait(true);
            if (answer == "Paste as one line") text = string.Join(' ', text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
            else if (answer != "Paste") return;
        }
        control.SendPaste(text);
    }

    // ------------------------------------------------------------------ links

    /// <summary>
    /// Ctrl+click: web links open in the browser, other schemes after a confirmation (IWindowService), OSC 8 targets alike, and file paths
    /// with an optional <c>:line:col</c> in the editor — resolved against the terminal's cwd (OSC 7) and the workspace folders, off the UI thread.
    /// </summary>
    public async Task OpenLinkAsync(TerminalLink link, TerminalSession session)
    {
        try
        {
            if (link.Kind is LinkKind.Url or LinkKind.Hyperlink && Uri.TryCreate(link.Target, UriKind.Absolute, out var uri) && !uri.IsFile)
            {
                if (Window is not { } window) return;
                if (uri.Scheme is not ("http" or "https"))
                {
                    var answer = await window.ShowMessageAsync(MessageSeverity.Info, $"Open '{uri}' with the program your system uses for {uri.Scheme} links?",
                        [new MessageAction("Open"), new MessageAction("Cancel", IsCloseAffordance: true)]).ConfigureAwait(true);
                    if (answer != "Open") return;
                }
                await window.OpenExternalAsync(uri).ConfigureAwait(true);
                return;
            }
            var path = link.Kind == LinkKind.File ? link.Target : Uri.TryCreate(link.Target, UriKind.Absolute, out var fileUri) && fileUri.IsFile ? fileUri.LocalPath : link.Target;
            var cwd = session.CurrentDirectory;
            var folders = Workspace?.Folders.Where(f => f.IsFile).Select(f => f.LocalPath).ToArray() ?? [];
            var resolved = await Task.Run(() => TerminalPaths.ResolveFile(path, cwd, folders)).ConfigureAwait(true);
            if (resolved is null)
            {
                Window?.SetStatusBarMessage($"No file '{path}' was found.", TimeSpan.FromSeconds(4));
                return;
            }
            TextRange? range = link.Line is { } line ? TextRange.At(Math.Max(0, line - 1), Math.Max(0, (link.Column ?? 1) - 1)) : null;
            if (Navigation is { } nav) await nav.OpenAsync(new Uri(resolved), range).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or UriFormatException or ArgumentException)
        {
            Window?.SetStatusBarMessage("The link could not be opened: " + ex.Message, TimeSpan.FromSeconds(4));
        }
    }

    // ------------------------------------------------------------------ context menu

    internal IReadOnlyList<MenuEntry> ContextMenuEntries(TerminalControl control)
    {
        var copy = ClipboardKeys.Gesture("Shift+C");
        return MenuSections.Join(
            [
                new MenuEntry("_Copy", control.CopyAsync) { Gesture = Bindings?.GetLabel(TerminalCommandSet.CopySelection) ?? copy, Icon = "copy", IsEnabled = control.CanCopy, CommandId = TerminalCommandSet.CopySelection },
                new MenuEntry("_Paste", control.PasteAsync) { Gesture = Bindings?.GetLabel(TerminalCommandSet.Paste), Icon = "clippy", IsEnabled = control.CanPaste, CommandId = TerminalCommandSet.Paste },
                new MenuEntry("Select _All", () => { control.SelectAll(); return Task.CompletedTask; }) { Gesture = Bindings?.GetLabel(TerminalCommandSet.SelectAll), CommandId = TerminalCommandSet.SelectAll },
            ],
            [
                new MenuEntry("C_lear Terminal", () => Run(TerminalCommandSet.Clear)) { Icon = "clear-all", CommandId = TerminalCommandSet.Clear },
                new MenuEntry("_Rename...", () => Run(TerminalCommandSet.Rename)) { CommandId = TerminalCommandSet.Rename },
                new MenuEntry("_Kill Terminal", () => Run(TerminalCommandSet.Kill)) { Icon = "trash", CommandId = TerminalCommandSet.Kill },
            ]);
    }

    private async Task Run(string command)
    {
        if (Commands is { } c) await c.ExecuteAsync(command).ConfigureAwait(true);
    }

    public void Dispose()
    {
        _keyHandler?.Dispose();
        _keyHandler = null;
        _subs.Dispose();
        _appearance.Clear();
    }

    /// <summary>OSC 4/10/11/12 answers come from the current theme's terminal colours (read on the UI thread when the theme changes).</summary>
    private sealed class ThemePalette : ITerminalPalette
    {
        private readonly int _fg, _bg, _cursor;
        private readonly int[] _ansi = new int[16];

        public ThemePalette()
        {
            var c = TerminalColors.FromTheme(true, 1);
            static int Rgb(Avalonia.Media.Color x) => (x.R << 16) | (x.G << 8) | x.B;
            _fg = Rgb(c.Foreground);
            _bg = Rgb(c.Background);
            _cursor = Rgb(c.Cursor);
            for (var i = 0; i < 16; i++) _ansi[i] = Rgb(c.Ansi[i]);
        }

        public int Foreground => _fg;
        public int Background => _bg;
        public int Cursor => _cursor;
        public int Ansi(int index) => _ansi[index & 15];
    }

    /// <summary>For hosts without the shell's dispatcher (tests composing the panel alone).</summary>
    private sealed class AvaloniaFallbackDispatcher : UiDispatcherBase
    {
        public override bool CheckAccess() => Dispatcher.UIThread.CheckAccess();
        protected override void PostCore(Action action, UiPriority priority) => Dispatcher.UIThread.Post(action, DispatcherPriority.Normal);
    }
}
