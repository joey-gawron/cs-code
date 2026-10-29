using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CsCode.Core.Debugging;

namespace CsCode.Panels.Debug.Variables;

public enum VariableRowKind
{
    /// <summary>A variable returned by the adapter.</summary>
    Variable,
    /// <summary>A scope group header (shown only when a frame has several scopes of interest).</summary>
    Scope,
    /// <summary>A user watch expression (root of a Watch tree).</summary>
    Watch,
    /// <summary>The "… N more" pager row of a node with more children than were loaded.</summary>
    More,
    /// <summary>The empty last row of the Watch window where a new expression is typed.</summary>
    NewWatch,
    /// <summary>A non-data row (error while loading children, cap notice).</summary>
    Message,
}

/// <summary>Which text of a row an inline editor is changing.</summary>
public enum EditField { Value, Expression }

/// <summary>
/// One row of a variables tree: a variable, a watch expression, a scope header or a pager/message row. Children are loaded lazily (and paged)
/// by the owning <see cref="VariablesTreeViewModel"/>; a node never talks to the debug session itself. The tree is virtualised by flattening:
/// visible nodes live in one list and a node only knows its depth.
/// </summary>
public sealed class VariableNodeViewModel : ObservableObject
{
    private readonly List<VariableNodeViewModel> _children = new();
    private string _name;
    private string _fullValue = "";
    private string _displayValue = "";
    private string? _type;
    private int _variablesReference;
    private bool _isExpanded;
    private bool _isLoading;
    private bool _isChanged;
    private bool _isStale;
    private bool _isError;
    private string? _errorMessage;
    private bool _isEditing;
    private string _editText = "";
    private string? _editError;
    private EditField _editField;
    private NumberFormat? _formatOverride;

    internal VariableNodeViewModel(VariablesTreeViewModel tree, VariableRowKind kind, VariableNodeViewModel? parent, string name, string path)
    {
        Tree = tree;
        Kind = kind;
        Parent = parent;
        Depth = parent is null ? 0 : parent.Depth + 1;
        _name = name;
        Path = path;
    }

    public VariablesTreeViewModel Tree { get; }
    public VariableRowKind Kind { get; }
    public VariableNodeViewModel? Parent { get; }
    public int Depth { get; internal set; }

    /// <summary>Stable identity across stops (names from the root): expansion state and change detection are keyed by it.</summary>
    public string Path { get; }

    /// <summary>Variable name, watch expression, scope name or the pager/message text.</summary>
    public string Name { get => _name; internal set => SetProperty(ref _name, value); }

    public string FullValue => _fullValue;
    public string DisplayValue { get => _displayValue; private set => SetProperty(ref _displayValue, value); }
    public string? Type { get => _type; private set => SetProperty(ref _type, value); }
    public int VariablesReference { get => _variablesReference; private set { if (SetProperty(ref _variablesReference, value)) OnPropertyChanged(nameof(HasChildren)); } }
    public int? NamedVariables { get; private set; }
    public int? IndexedVariables { get; private set; }
    public string? EvaluateName { get; private set; }
    public string? MemoryReference { get; private set; }

    /// <summary>Reference of the container this variable was read from (what DAP <c>setVariable</c> needs); 0 when it cannot be edited that way.</summary>
    public int ParentReference { get; internal set; }

    /// <summary>The watch this row represents (Watch rows only).</summary>
    public WatchExpression? Watch { get; internal set; }

    /// <summary>The value this (persistent, Watch) row showed at the previous stop; the next evaluation is compared with it.</summary>
    internal string? BaselineValue { get; set; }

    /// <summary>Session generation the node's handle belongs to; a node from an older generation is dead.</summary>
    internal long Generation { get; set; }

    public bool HasChildren => (Kind is VariableRowKind.Variable or VariableRowKind.Watch or VariableRowKind.Scope) && (_variablesReference > 0 || _children.Count > 0);

    public bool IsExpanded { get => _isExpanded; internal set { if (SetProperty(ref _isExpanded, value)) OnPropertyChanged(nameof(ChevronKind)); } }

    /// <summary>Codicon name of the expander glyph (empty = no expander).</summary>
    public string? ChevronKind => HasChildren ? (_isExpanded ? "chevron-down" : "chevron-right") : null;

    public bool IsLoading { get => _isLoading; internal set => SetProperty(ref _isLoading, value); }

    /// <summary>The value differs from the previous stop (shown red).</summary>
    public bool IsChanged { get => _isChanged; internal set => SetProperty(ref _isChanged, value); }

    /// <summary>The value belongs to an earlier stop while the debuggee runs (shown dimmed).</summary>
    public bool IsStale { get => _isStale; internal set => SetProperty(ref _isStale, value); }

    /// <summary>The row is an evaluation/loading error (message in <see cref="DisplayValue"/>, shown red).</summary>
    public bool IsError { get => _isError; private set => SetProperty(ref _isError, value); }

    public string? ErrorMessage { get => _errorMessage; private set => SetProperty(ref _errorMessage, value); }

    public bool IsEditing { get => _isEditing; internal set { if (SetProperty(ref _isEditing, value)) { OnPropertyChanged(nameof(IsEditingValue)); OnPropertyChanged(nameof(IsEditingExpression)); OnPropertyChanged(nameof(ShowValueText)); OnPropertyChanged(nameof(ShowNameText)); } } }
    public bool IsEditingValue => _isEditing && _editField == EditField.Value;
    public bool IsEditingExpression => _isEditing && _editField == EditField.Expression;
    public bool ShowValueText => !IsEditingValue;
    public bool ShowNameText => !IsEditingExpression && Kind != VariableRowKind.NewWatch;
    public string EditText { get => _editText; set => SetProperty(ref _editText, value); }
    public string? EditError { get => _editError; internal set { if (SetProperty(ref _editError, value)) OnPropertyChanged(nameof(HasEditError)); } }
    public bool HasEditError => _editError is not null;
    internal EditField EditFieldKind => _editField;

    public bool IsMore => Kind == VariableRowKind.More;
    public bool IsNewWatch => Kind == VariableRowKind.NewWatch;
    public bool IsDataRow => Kind is VariableRowKind.Variable or VariableRowKind.Watch or VariableRowKind.Scope;
    public bool IsPlaceholder => Kind is VariableRowKind.More or VariableRowKind.NewWatch or VariableRowKind.Message;

    /// <summary>Indentation of the row's first column (16 px per level, like VS).</summary>
    public Thickness Indent => new(Depth * 14, 0, 0, 0);

    public bool ChildrenLoaded { get; internal set; }
    internal IReadOnlyList<VariableNodeViewModel> Children => _children;
    internal VariableNodeViewModel? MoreRow { get; set; }

    /// <summary>Variables requested from the adapter so far (the start of the next page).</summary>
    internal int NextStart { get; set; }
    internal int LoadedCount => _children.Count;
    internal string? FirstChildName { get; set; }

    /// <summary>Total children the adapter announced (named + indexed), or null when it did not say.</summary>
    public int? KnownChildCount => NamedVariables is null && IndexedVariables is null ? null : (NamedVariables ?? 0) + (IndexedVariables ?? 0);

    public NumberFormat? FormatOverride
    {
        get => _formatOverride;
        set { if (SetProperty(ref _formatOverride, value)) Refresh(); }
    }

    public bool CanViewAsNumber => ValueFormat.IsIntegerType(_type) && IsDataRow && !IsError;

    public string Tooltip => _isError ? (_errorMessage ?? _fullValue)
        : IsDataRow ? (_type is { Length: > 0 } ? $"{_fullValue}\n({_type})" : _fullValue) : string.Empty;

    internal void AddChildren(IEnumerable<VariableNodeViewModel> children) => _children.AddRange(children);
    internal void ClearChildren() => _children.Clear();

    /// <summary>Re-renders <see cref="DisplayValue"/> (the tree's radix changed, or the value did).</summary>
    internal void Refresh()
    {
        if (!IsDataRow || _isError) return;
        DisplayValue = ValueFormat.Display(_fullValue, _type, _formatOverride ?? Tree.NumberFormat);
        OnPropertyChanged(nameof(Tooltip));
        OnPropertyChanged(nameof(CanViewAsNumber));
    }

    internal void SetFrom(DebugVariable v)
    {
        EvaluateName = v.EvaluateName;
        MemoryReference = v.MemoryReference;
        SetValue(v.Value, v.Type, v.VariablesReference, v.NamedVariables, v.IndexedVariables);
    }

    internal void SetFrom(DebugEvaluateResult r)
    {
        MemoryReference = r.MemoryReference;
        SetValue(r.Result, r.Type, r.VariablesReference, r.NamedVariables, r.IndexedVariables);
    }

    internal void SetValue(string value, string? type, int variablesReference, int? named, int? indexed)
    {
        _fullValue = value;
        _type = type;
        NamedVariables = named;
        IndexedVariables = indexed;
        IsError = false;
        ErrorMessage = null;
        VariablesReference = variablesReference;
        OnPropertyChanged(nameof(FullValue));
        OnPropertyChanged(nameof(Type));
        OnPropertyChanged(nameof(ChevronKind));
        OnPropertyChanged(nameof(HasChildren));
        Refresh();
    }

    internal void SetError(string message)
    {
        _fullValue = message;
        ErrorMessage = message;
        IsError = true;
        VariablesReference = 0;
        Type = null;
        DisplayValue = ValueFormat.SingleLine(message, ValueFormat.MaxDisplayChars);
        OnPropertyChanged(nameof(FullValue));
        OnPropertyChanged(nameof(Tooltip));
        OnPropertyChanged(nameof(ChevronKind));
    }

    /// <summary>Forgets everything that depends on a live debuggee (the handle died when it resumed).</summary>
    internal void DropHandles()
    {
        _children.Clear();
        MoreRow = null;
        ChildrenLoaded = false;
        NextStart = 0;
        FirstChildName = null;
        _isLoading = false;
        _isExpanded = false;
        VariablesReference = 0;
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(IsExpanded));
        OnPropertyChanged(nameof(ChevronKind));
    }

    internal void BeginEdit(EditField field, string text)
    {
        _editField = field;
        EditText = text;
        EditError = null;
        IsEditing = true;
    }

    internal void EndEdit() => IsEditing = false;

    public override string ToString() => $"{Name} = {DisplayValue}";
}
