using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CsCode.Panels.Debug.Console;
using CsCode.Panels.Debug.Infrastructure;
using CsCode.Ui;
using CsCode.Ui.Menus;

namespace CsCode.Panels.Debug.Views;

/// <summary>
/// Debug Console: the output list follows new lines unless scrolling is locked; the REPL box runs on Enter, adds a line on Shift+Enter,
/// walks the history with ↑/↓ (when the caret is on the first/last line), and drives the completion list (↑↓ select, Tab/Enter accept,
/// Esc close, Ctrl+Space ask).
/// </summary>
public sealed partial class ConsoleView : UserControl, IDisposable, IClipboardTarget, IToolWindowFocus
{
    /// <summary>
    /// View &gt; Tool Windows &gt; Debug Console, as Visual Studio's Immediate window: the keyboard in the expression box, where what the user types
    /// is evaluated. Not on Clear Console, which leads the tab order: the next Enter would press it and wipe the output (with the history kept for
    /// late subscribers) while the expression typed went nowhere.
    /// </summary>
    public bool FocusPreferred() => this.FindControl<TextBox>("Input") is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true } input && input.Focus();

    private ListBox LinesList => this.FindControl<ListBox>("Lines")!;

    /// <summary>The output lines' clipboard (the REPL box, a text box, has its own Cut/Copy/Paste).</summary>
    public bool CanCopy => LinesList.SelectedItems is { Count: > 0 } && _model is not null;

    public async Task CopyAsync()
    {
        if (_model is { } model) await model.CopyAsync(LinesList.SelectedItems?.OfType<ConsoleLine>() ?? []);
    }

    /// <summary>The output's menu: Copy (the selected lines), Copy All, Clear All.</summary>
    internal ContextMenu BuildLinesMenu()
    {
        var model = _model;
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "Copy", InputGesture = KeyGesture.Parse(CommandKeys.Gesture("C")), IsEnabled = CanCopy, Command = new UiCommand(CopyAsync) });
        menu.Items.Add(new MenuItem { Header = "Copy All", IsEnabled = model is { Lines.Count: > 0 }, Command = new UiCommand(() => model?.CopyAsync(model.Lines) ?? Task.CompletedTask) });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Clear All", IsEnabled = model is { Lines.Count: > 0 }, Command = new UiCommand(() => model?.Clear()) });
        return menu;
    }

    private void OnLinesContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_model is null) return;
        if (e.Source is Avalonia.Visual v && v.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: ConsoleLine line } && !LinesList.SelectedItems!.Contains(line))
            LinesList.SelectedItem = line;
        e.Handled = true;
        BuildLinesMenu().Open(LinesList);
    }

    private DebugConsoleViewModel? _model;
    private bool _scrollQueued;

    public ConsoleView()
    {
        InitializeComponent();
        var input = this.FindControl<TextBox>("Input")!;
        input.AddHandler(KeyDownEvent, OnInputKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        var lines = this.FindControl<ListBox>("Lines")!;
        lines.AddHandler(KeyDownEvent, OnLinesKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        lines.ContextRequested += OnLinesContextRequested;
        this.FindControl<ListBox>("Completions")!.DoubleTapped += (_, _) =>
        {
            if (_model?.SelectedCompletion is { } c) Accept(c);
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Detach();
        _model = DataContext as DebugConsoleViewModel;
        if (_model is null) return;
        _model.Lines.CollectionChanged += OnLinesChanged;
    }

    private void Detach()
    {
        if (_model is not null) _model.Lines.CollectionChanged -= OnLinesChanged;
        _model = null;
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_model is not { AutoScroll: true } || _scrollQueued || e.Action == NotifyCollectionChangedAction.Remove) return;
        _scrollQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollQueued = false;
            if (_model is { AutoScroll: true, Lines.Count: > 0 } m) this.FindControl<ListBox>("Lines")!.ScrollIntoView(m.Lines[^1]);
        }, DispatcherPriority.Background);
    }

    private async void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (_model is not { } model || sender is not TextBox box) return;
        var shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        var ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
        switch (e.Key)
        {
            case Key.Enter when !shift:
                e.Handled = true;
                if (model is { CompletionsOpen: true, SelectedCompletion: { } picked }) Accept(picked);
                else await model.SubmitAsync();
                break;
            case Key.Tab when model.CompletionsOpen && model.SelectedCompletion is { } tabbed:
                e.Handled = true;
                Accept(tabbed);
                break;
            case Key.Escape when model.CompletionsOpen:
                e.Handled = true;
                model.CloseCompletions();
                break;
            case Key.Up when model.CompletionsOpen:
                e.Handled = true;
                model.SelectNextCompletion(-1);
                break;
            case Key.Down when model.CompletionsOpen:
                e.Handled = true;
                model.SelectNextCompletion(+1);
                break;
            case Key.Up when !shift && !ctrl && OnFirstLine(box):
                if (model.HistoryPrevious()) { e.Handled = true; box.CaretIndex = box.Text?.Length ?? 0; }
                break;
            case Key.Down when !shift && !ctrl && OnLastLine(box):
                if (model.HistoryNext()) { e.Handled = true; box.CaretIndex = box.Text?.Length ?? 0; }
                break;
            case Key.Space when ctrl:
                e.Handled = true;
                await model.RequestCompletionsAsync(box.CaretIndex);
                break;
        }
    }

    private static bool OnFirstLine(TextBox box) => (box.Text ?? "").AsSpan(0, Math.Min(box.CaretIndex, box.Text?.Length ?? 0)).IndexOf('\n') < 0;
    private static bool OnLastLine(TextBox box) => (box.Text ?? "").AsSpan(Math.Min(box.CaretIndex, box.Text?.Length ?? 0)).IndexOf('\n') < 0;

    private void Accept(CompletionEntry entry)
    {
        if (_model is not { } model) return;
        model.AcceptCompletion(entry);
        var box = this.FindControl<TextBox>("Input")!;
        box.CaretIndex = model.CaretAfterCompletion;
        box.Focus();
    }

    private async void OnLinesKeyDown(object? sender, KeyEventArgs e)
    {
        if (ClipboardKeys.Classify(e) != ClipboardAction.Copy || _model is not { } model) return; // Ctrl+C, Ctrl+Insert
        var list = this.FindControl<ListBox>("Lines")!;
        e.Handled = true;
        await model.CopyAsync(list.SelectedItems?.OfType<ConsoleLine>() ?? []);
    }

    public void Dispose()
    {
        Detach();
        (DataContext as IDisposable)?.Dispose();
    }
}
