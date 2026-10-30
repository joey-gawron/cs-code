using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CsCode.App.Docking;
using CsCode.App.Input;
using CsCode.Core.Contributions;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor;
using CsCode.Editor.Documents;
using CsCode.Editor.View;
using CsCode.Macros;
using CsCode.Ui.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.App.Macros;

/// <summary>
/// Connects the macro module (<c>CsCode.Macros</c>, Avalonia-free) to the shell and the editor views (docs/MACROS.md). It lives in the shell for the
/// same reason as the Vim adapter: only the composition root references both the editor and the module.
/// <list type="bullet">
/// <item><b>Recording taps.</b> The keyboard pipeline's tap (<see cref="IKeyboardTap"/>) reports every key the key bindings left to the focused
/// control; a key aimed at an editor's text area that is not a character is recorded as a key step, Enter / F3 in the editor's find panel as a
/// search step (<see cref="MacroCommands.Find"/>). Keys a modal layer (Vim) took never reach the pipeline, so they are never recorded. Typed text
/// comes from the editor views' text-input tap (<see cref="IEditorViewController.TextInput"/>), subscribed only while recording. Commands are
/// recorded by the service from <see cref="ICommandService.Executed"/>; while the editor handles a recorded key or text the service is told
/// (<see cref="MacroService.BeginEditorInput"/>), so a command the editor itself starts then is not recorded twice.</item>
/// <item><b>Playback host</b> (<see cref="IMacroHost"/>): keys go to the active editor through <see cref="IEditorViewController.ReplayKey"/> (the
/// window's handlers see them first; the pipeline lets them through unresolved), text through <see cref="IEditorViewController.ReplayText"/>.
/// Esc stops playback; other keys and typing aimed at an editor are swallowed while a macro plays.</item>
/// <item><b>Vim.</b> While Vim emulation is on, recording is refused with a hint (Vim has q/@), recording stops when Vim is switched on, and a
/// macro that types (key or text steps) does not play: Vim would read its keys as commands. Command-only macros play.</item>
/// </list>
/// UI-thread object; everything here runs inside a key or text event or a command, and waits for nothing.
/// </summary>
public sealed class MacroIntegration : IKeyboardTap, IMacroHost, IDisposable
{
    /// <summary>Default key bindings (Visual Studio "Macros for Visual Studio" style chords; both are Ctrl chords so Vim leaves them to the shell).</summary>
    public static readonly IReadOnlyList<KeybindingContribution> DefaultKeybindings =
    [
        new(MacroCommands.Record, "ctrl+m ctrl+r"),
        new(MacroCommands.PlayLast, "ctrl+m ctrl+enter"),
    ];

    private readonly MacroService _service;
    private readonly EditorViewFactory _editors;
    private readonly DocumentWell _well;
    private readonly ICommandService _commands;
    private readonly IUiDispatcher _ui;
    private readonly WindowHolder _windowHolder;
    private readonly IContributionRegistry? _contributions;
    private readonly DisposableStore _subscriptions = new();
    private readonly Dictionary<CodeEditorControl, IDisposable> _textTaps = new(ReferenceEqualityComparer.Instance);
    private DisposableStore? _recording;
    private Vim.VimIntegration? _vim;
    private KeyboardPipeline? _keyboard;
    private bool _injecting, _started, _disposed;

    private MacroIntegration(IServiceProvider sp, MacroService service, EditorViewFactory editors)
    {
        _service = service;
        _editors = editors;
        _well = sp.GetRequiredService<DocumentWell>();
        _commands = sp.GetRequiredService<ICommandService>();
        _ui = sp.GetRequiredService<IUiDispatcher>();
        _windowHolder = sp.GetRequiredService<WindowHolder>();
        _contributions = sp.GetService<IContributionRegistry>();
    }

    /// <summary>The integration for a composition with the macro module and the real editor; null otherwise (fakes in the dev host, some tests).</summary>
    public static MacroIntegration? TryCreate(IServiceProvider sp) =>
        sp.GetService<MacroService>() is { } service && sp.GetService<IEditorViewFactory>() is EditorViewFactory editors ? new MacroIntegration(sp, service, editors) : null;

    public MacroService Service => _service;

    /// <summary>Wires the integration (UI thread, once, after the keyboard pipeline and the Vim adapter exist).</summary>
    public void Start(KeyboardPipeline keyboard, Vim.VimIntegration? vim)
    {
        if (_started) return;
        _started = true;
        _keyboard = keyboard;
        _vim = vim;
        keyboard.Tap = this;
        _subscriptions.Add(Disposable.Create(() => { if (ReferenceEquals(keyboard.Tap, this)) keyboard.Tap = null; }));
        _subscriptions.Add(_service.AttachHost(this));
        _subscriptions.Add(_service.StateChanged.Subscribe(OnStateChanged));
        _subscriptions.Add(_commands.Register(MacroCommands.Find, FindAsync)); // no title: a recorded step, not a palette command
        if (_contributions is not null) _subscriptions.Add(_contributions.Contribute(ContributionSources.Builtin, AppContext.BaseDirectory, DefaultKeybindings));
        if (vim is not null)
            _subscriptions.Add(vim.Service.EnabledChanged.Subscribe(on =>
            {
                if (!on) return;
                void Stop()
                {
                    if (!_disposed && _service.IsRecording) _service.StopRecording("Recording stopped: Vim emulation was switched on.");
                }
                // at once on the UI thread: no key may be recorded between the switch and the stop
                if (_ui.CheckAccess()) Stop();
                else _ui.Post(Stop);
            }));
        if (_windowHolder.Window is { } top)
        {
            // typing aimed at an editor while a macro plays would mix with the macro's own: swallowed (before the editor and after Vim)
            top.AddHandler(InputElement.TextInputEvent, OnPreviewTextInput, RoutingStrategies.Tunnel);
            // the end of a key's or text's route (handled or not): the editor is done with the input the recorder was told about
            top.AddHandler(InputElement.KeyDownEvent, OnInputDone, RoutingStrategies.Bubble, handledEventsToo: true);
            top.AddHandler(InputElement.TextInputEvent, OnInputDone, RoutingStrategies.Bubble, handledEventsToo: true);
            _subscriptions.Add(Disposable.Create(() =>
            {
                top.RemoveHandler(InputElement.TextInputEvent, OnPreviewTextInput);
                top.RemoveHandler(InputElement.KeyDownEvent, OnInputDone);
                top.RemoveHandler(InputElement.TextInputEvent, OnInputDone);
            }));
        }
        _ = StartServiceAsync();
    }

    private async Task StartServiceAsync()
    {
        try { await _service.StartAsync().ConfigureAwait(true); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _service.ShowStatus("Macros could not be loaded: " + ex.Message); }
    }

    // The Vim service's switch, not the adapter's: the adapter follows it on a later dispatcher turn, and a macro step right after vim.toggle
    // must already see Vim on (its keys would otherwise reach the editor while Vim takes over).
    private bool VimOn => _vim?.Service.Enabled == true;

    // ------------------------------------------------------------------------------------------------------------ keys (IKeyboardTap)

    public KeyDispatch? BeforeResolve(KeyStroke stroke)
    {
        if (_injecting) return KeyDispatch.PassThrough; // a replayed key: the editor's, as recorded (never a binding)
        if (_service.State != MacroState.Playing) return null;
        var focused = Focused();
        var inEditor = FocusedEditor() is not null;
        // Esc stops the macro from the editor (or when nothing has the focus); in a prompt a step is waiting for (the palette, an input box, a
        // Save dialog) it keeps its own meaning, and the status bar item still stops the macro
        if (stroke is { Key: "escape", Ctrl: false, Shift: false, Alt: false, Meta: false } && (inEditor || focused is null or TopLevel))
        {
            _service.Stop();
            return KeyDispatch.Handled;
        }
        return inEditor ? KeyDispatch.Handled : null; // the user's keys must not interleave with the macro's
    }

    public void AfterResolve(KeyStroke stroke, KeyDispatch dispatch)
    {
        if (_recording is null || dispatch != KeyDispatch.PassThrough) return;
        var focused = Focused();
        if (focused is AvaloniaEdit.Editing.TextArea area && area.FindAncestorOfType<CodeEditorControl>() is not null)
        {
            _service.BeginEditorInput();
            if (stroke.Key == "f3" && !stroke.Ctrl && !stroke.Alt && !stroke.Meta && area.FindAncestorOfType<AvaloniaEdit.TextEditor>()?.SearchPanel is { IsClosed: false } open)
            {
                RecordFind(open, backwards: stroke.Shift);
                return;
            }
            if (!Vim.VimKeyPolicy.IsTextKey(stroke)) _service.RecordKey(stroke); // characters come as text input
            return;
        }
        // the editor's find box: Enter / Shift+Enter / F3 / Shift+F3 search; what is typed there is the search step's text, not a step
        if ((focused as Visual)?.FindAncestorOfType<AvaloniaEdit.Search.SearchPanel>() is { } panel && !stroke.Ctrl && !stroke.Alt && !stroke.Meta
            && stroke.Key is "enter" or "f3")
            RecordFind(panel, backwards: stroke.Shift);
    }

    private void RecordFind(AvaloniaEdit.Search.SearchPanel panel, bool backwards)
    {
        if (string.IsNullOrEmpty(panel.SearchPattern)) return;
        _service.RecordCommand(MacroCommands.Find, new MacroFindArgs(panel.SearchPattern, panel.UseRegex, panel.MatchCase, panel.WholeWords, backwards).ToJson());
    }

    private object? Focused() => _windowHolder.Window?.FocusManager?.GetFocusedElement();

    /// <summary>The editor view whose text area has the keyboard focus, or null.</summary>
    private CodeEditorControl? FocusedEditor() =>
        Focused() is AvaloniaEdit.Editing.TextArea area ? area.FindAncestorOfType<CodeEditorControl>() : null;

    private void OnPreviewTextInput(object? sender, TextInputEventArgs e)
    {
        if (_injecting || _service.State != MacroState.Playing) return;
        if (e.Source is AvaloniaEdit.Editing.TextArea area && area.FindAncestorOfType<CodeEditorControl>() is not null) e.Handled = true;
    }

    private void OnInputDone(object? sender, RoutedEventArgs e) => _service.EndEditorInput();

    // ------------------------------------------------------------------------------------------------------------ recording: text taps

    private void OnStateChanged(MacroState state)
    {
        if (state == MacroState.Recording && _recording is null)
        {
            _recording = new DisposableStore();
            foreach (var view in _editors.Views) TapText(view);
            _recording.Add(_editors.ViewOpened.Subscribe(TapText));
            _recording.Add(_editors.ViewClosing.Subscribe(UntapText));
        }
        else if (state != MacroState.Recording && _recording is not null)
        {
            // no editor reference survives the recording
            _recording.Dispose();
            _recording = null;
            foreach (var tap in _textTaps.Values) tap.Dispose();
            _textTaps.Clear();
        }
    }

    private void TapText(CodeEditorControl view)
    {
        if (_recording is null || _textTaps.ContainsKey(view) || _editors.GetController(view) is not { } controller) return;
        _textTaps[view] = controller.TextInput.Subscribe(text =>
        {
            _service.BeginEditorInput();
            _service.RecordText(text);
        });
    }

    private void UntapText(CodeEditorControl view)
    {
        if (_textTaps.Remove(view, out var tap)) tap.Dispose();
    }

    // ------------------------------------------------------------------------------------------------------------ playback (IMacroHost)

    private IEditorViewController? ActiveController => _well.ActiveEditor?.Controller;

    public string? TypingUnavailableReason
    {
        get
        {
            if (VimOn) return "Vim emulation is on, and Vim would read the macro's keys as Vim commands. Use Vim's own macros (q and @), or turn Vim off (Toggle Vim Mode).";
            if (_well.ActiveEditor is null) return "No editor is open.";
            return ActiveController is null ? "The editor is not shown." : null;
        }
    }

    public MacroBlock? RecordingBlocked => VimOn
        ? new MacroBlock("Vim emulation is on: record a macro with q followed by a register (a-z), stop with q, play it with @ and the register. " +
            "To record a general macro instead, turn Vim off (Toggle Vim Mode).", "Turn Off Vim", global::CsCode.Vim.VimService.DisableCommand)
        : null;

    public string? SendKey(KeyStroke key)
    {
        if (ActiveController is not { } controller) return TypingUnavailableReason ?? "No editor is open.";
        if (!MacroKeys.TryGetKey(key, out var avaloniaKey, out var modifiers)) return $"The key '{key}' cannot be pressed.";
        _injecting = true;
        try
        {
            // whether a handler took the key does not matter (Esc with nothing to close is still the key the user pressed)
            return controller.ReplayKey(avaloniaKey, modifiers) || controller.IsFocused ? null : "The editor cannot take the keyboard focus.";
        }
        finally { _injecting = false; }
    }

    public string? TypeText(string text)
    {
        if (ActiveController is not { } controller) return TypingUnavailableReason ?? "No editor is open.";
        _injecting = true;
        try { return controller.ReplayText(text) ? null : controller.Document.IsReadOnly ? "The document is read-only." : "The editor is not shown."; }
        finally { _injecting = false; }
    }

    public MacroCaret? Caret => ActiveController is { } c
        ? new MacroCaret(c.Document.Uri, c.Caret.Line, c.Caret.Character, c.Document.LineCount, c.Document.Version)
        : null;

    public (int First, int Last)? SelectedLines
    {
        get
        {
            if (ActiveController is not { } c || c.Selections is not [var s, ..]) return null;
            var last = s.End.Line > s.Start.Line && s.End.Character == 0 ? s.End.Line - 1 : s.End.Line;
            return (s.Start.Line, last);
        }
    }

    public bool MoveCaretToLineStart(int line)
    {
        if (ActiveController is not { } c || line < 0 || line >= c.Document.LineCount) return false;
        c.SetSelection(TextRange.At(line, 0), reveal: true);
        return true;
    }

    // ------------------------------------------------------------------------------------------------------------ the search step

    /// <summary>
    /// <see cref="MacroCommands.Find"/>: searches the active editor from the selection (the previous match) or the caret, without wrapping around,
    /// on a snapshot off the UI thread, and selects the match with the caret at its start (as the editor's own find does).
    /// </summary>
    private async ValueTask<object?> FindAsync(CommandInvocation inv)
    {
        var args = inv.Arg<MacroFindArgs>(0);
        if (args is not { Text.Length: > 0 }) throw new ArgumentException("macro.find needs an argument such as { \"text\": \"foo\" }.");
        if (ActiveController is not { } controller || controller.Document is not EditorDocument document) throw new InvalidOperationException("No editor is open.");
        var snapshot = document.Snapshot();
        var selection = controller.Selections is [var s, ..] ? s : TextRange.At(controller.Caret.Line, controller.Caret.Character);
        var start = snapshot.OffsetAt(selection.Start);
        var end = snapshot.OffsetAt(selection.End);
        var hit = await Task.Run(() => MacroFind.Search(snapshot.GetText(), start, end, args)).ConfigureAwait(true);
        if (hit is not { } match)
            throw new MacroSearchFailedException($"'{args.Text}' was not found {(args.Backwards ? "before" : "after")} the caret.");
        if (document.Version != snapshot.Version || !ReferenceEquals(ActiveController?.Document, document))
            throw new InvalidOperationException("The document changed during the search.");
        var from = snapshot.PositionAt(match.Start);
        var to = snapshot.PositionAt(match.Start + match.Length);
        controller.SetSelection(new TextRange(to, from), reveal: true); // the selection ends where it started: the caret at the match's start
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        OnStateChanged(MacroState.Idle);
        _subscriptions.Dispose();
        _keyboard = null;
        _vim = null;
    }
}

/// <summary>Key strokes back to Avalonia keys (the inverse of <see cref="AvaloniaKeyMapper"/>), for replayed key steps.</summary>
public static class MacroKeys
{
    private static readonly Dictionary<string, Key> s_keys = Build();

    private static Dictionary<string, Key> Build()
    {
        var map = new Dictionary<string, Key>(StringComparer.Ordinal);
        foreach (var key in Enum.GetValues<Key>())
            if (AvaloniaKeyMapper.NameOf(key) is { } name) map.TryAdd(name, key);
        return map;
    }

    public static bool TryGetKey(KeyStroke stroke, out Key key, out KeyModifiers modifiers)
    {
        modifiers = (stroke.Ctrl ? KeyModifiers.Control : 0) | (stroke.Shift ? KeyModifiers.Shift : 0) | (stroke.Alt ? KeyModifiers.Alt : 0) | (stroke.Meta ? KeyModifiers.Meta : 0);
        return s_keys.TryGetValue(stroke.Key, out key);
    }
}
