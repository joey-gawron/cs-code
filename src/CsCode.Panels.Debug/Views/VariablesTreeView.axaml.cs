using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CsCode.Panels.Debug.Infrastructure;
using CsCode.Panels.Debug.Variables;

namespace CsCode.Panels.Debug.Views;

/// <summary>
/// The Locals / Autos / Watch grid: header, virtualised rows, inline editors, context menu and the keyboard model
/// (↑↓ move, → expand / into the children, ← collapse / to the parent, Enter edit or activate, F2 rename or edit, Delete remove,
/// Ctrl+C copy the value, Alt+↑/↓ move a watch).
/// </summary>
public sealed partial class VariablesTreeView : UserControl
{
    public VariablesTreeView()
    {
        InitializeComponent();
        var rows = this.FindControl<ListBox>("Rows")!;
        rows.AddHandler(KeyDownEvent, OnRowsKeyDown);
        rows.DoubleTapped += OnRowsDoubleTapped;
        rows.ContextRequested += OnContextRequested;
    }

    private VariablesTreeViewModel? Tree => DataContext as VariablesTreeViewModel;
    private ListBox RowsList => this.FindControl<ListBox>("Rows")!;

    /// <summary>The row the event came from (the pointer's row for mouse events), or the selected row.</summary>
    private VariableNodeViewModel? RowOf(object? source)
    {
        if (source is Visual visual && visual.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is VariableNodeViewModel node) return node;
        return Tree?.SelectedRow;
    }

    private async void OnRowsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox || Tree is not { } tree || tree.SelectedRow is not { } row) return;
        try
        {
            var ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
            var alt = (e.KeyModifiers & KeyModifiers.Alt) != 0;
            switch (e.Key)
            {
                case Key.Right when !ctrl:
                    e.Handled = true;
                    if (row.HasChildren && !row.IsExpanded) await tree.ExpandAsync(row);
                    else if (row.IsExpanded) Select(tree, tree.Rows.IndexOf(row) + 1);
                    break;
                case Key.Left when !ctrl:
                    e.Handled = true;
                    if (row.IsExpanded) tree.Collapse(row);
                    else if (tree.ParentRow(row) is { } parent) Select(tree, tree.Rows.IndexOf(parent));
                    break;
                case Key.Enter:
                    e.Handled = true;
                    await ActivateAsync(tree, row, editFirst: true);
                    break;
                case Key.F2:
                    e.Handled = true;
                    BeginEdit(tree, row);
                    break;
                case Key.Delete:
                    e.Handled = true;
                    tree.DeleteRequested?.Invoke(row);
                    break;
                case Key.C when ctrl:
                    e.Handled = true;
                    await tree.CopyValueAsync(row);
                    break;
                case Key.Up when alt:
                    e.Handled = true;
                    tree.MoveRequested?.Invoke(row, -1);
                    break;
                case Key.Down when alt:
                    e.Handled = true;
                    tree.MoveRequested?.Invoke(row, +1);
                    break;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException) { /* the debuggee resumed under the key press */ }
    }

    private static void BeginEdit(VariablesTreeViewModel tree, VariableNodeViewModel row)
    {
        if (row.Kind is VariableRowKind.Watch or VariableRowKind.NewWatch) tree.BeginEditExpression(row);
        else tree.BeginEditValue(row);
    }

    private async Task ActivateAsync(VariablesTreeViewModel tree, VariableNodeViewModel row, bool editFirst)
    {
        switch (row.Kind)
        {
            case VariableRowKind.More: await tree.LoadMoreAsync(row); break;
            case VariableRowKind.NewWatch: tree.BeginEditExpression(row); break;
            case VariableRowKind.Watch when editFirst: tree.BeginEditExpression(row); break;
            case VariableRowKind.Variable when editFirst && tree.CanEditValue(row): tree.BeginEditValue(row); break;
            default:
                if (row.HasChildren) await tree.ToggleAsync(row);
                else BeginEdit(tree, row);
                break;
        }
    }

    private void Select(VariablesTreeViewModel tree, int index)
    {
        if (index < 0 || index >= tree.Rows.Count) return;
        tree.SelectedRow = tree.Rows[index];
        RowsList.ScrollIntoView(tree.Rows[index]);
    }

    private async void OnRowsDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is TextBox || Tree is not { } tree || RowOf(e.Source) is not { } row) return;
        try { await ActivateAsync(tree, row, editFirst: false); }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException) { }
    }

    private async void OnEditorLostFocus(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not VariableNodeViewModel node) return;
        try { await node.EditorLostFocusAsync(); }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException) { }
    }

    // ------------------------------------------------------------------ context menu

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (Tree is not { } tree) return;
        var row = RowOf(e.Source);
        if (row is not null) tree.SelectedRow = row;
        var menu = BuildMenu(tree, row);
        if (menu.Items.Count == 0) return;
        menu.Open(RowsList);
        e.Handled = true;
    }

    internal ContextMenu BuildMenu(VariablesTreeViewModel tree, VariableNodeViewModel? row)
    {
        var menu = new ContextMenu();
        void Add(string header, Func<Task> run, bool enabled = true, string? gesture = null) =>
            menu.Items.Add(new MenuItem { Header = header, Command = new UiCommand(run), IsEnabled = enabled, InputGesture = gesture is null ? null : KeyGesture.Parse(gesture) });
        void AddSync(string header, Action run, bool enabled = true, string? gesture = null) => Add(header, () => { run(); return Task.CompletedTask; }, enabled, gesture);

        if (row is { IsDataRow: true })
        {
            if (row.HasChildren) AddSync(row.IsExpanded ? "Collapse" : "Expand", () => _ = tree.ToggleAsync(row), gesture: row.IsExpanded ? "Left" : "Right");
            if (row.Kind == VariableRowKind.Variable) AddSync("Edit Value", () => tree.BeginEditValue(row), tree.CanEditValue(row), "Enter");
            if (row.Kind == VariableRowKind.Watch) AddSync("Edit Expression", () => tree.BeginEditExpression(row), gesture: "F2");
            Add("Copy Value", () => tree.CopyValueAsync(row), gesture: "Ctrl+C");
            Add("Copy Expression", () => tree.CopyExpressionAsync(row));
            if (row.Kind == VariableRowKind.Variable) AddSync("Add Watch", () => tree.AddToWatch(row));
            if (row.CanViewAsNumber)
            {
                menu.Items.Add(new Separator());
                AddSync("View as Hexadecimal", () => tree.ViewAs(row, Variables.NumberFormat.Hexadecimal));
                AddSync("View as Decimal", () => tree.ViewAs(row, Variables.NumberFormat.Decimal));
            }
            if (row.Kind == VariableRowKind.Watch)
            {
                menu.Items.Add(new Separator());
                AddSync("Delete Watch", () => tree.DeleteRequested?.Invoke(row), gesture: "Delete");
                AddSync("Move Up", () => tree.MoveRequested?.Invoke(row, -1), gesture: "Alt+Up");
                AddSync("Move Down", () => tree.MoveRequested?.Invoke(row, +1), gesture: "Alt+Down");
            }
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem
        {
            Header = "Hexadecimal Display",
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = tree.NumberFormat == Variables.NumberFormat.Hexadecimal,
            Command = new UiCommand(() => { tree.NumberFormat = tree.NumberFormat == Variables.NumberFormat.Hexadecimal ? Variables.NumberFormat.Decimal : Variables.NumberFormat.Hexadecimal; }),
        });
        if (tree.DeleteAllRequested is { } deleteAll) AddSync("Delete All Watches", deleteAll);
        return menu;
    }
}
