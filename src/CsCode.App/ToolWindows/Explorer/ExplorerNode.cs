using CommunityToolkit.Mvvm.ComponentModel;
using CsCode.Core.Platform;

namespace CsCode.App.ToolWindows.Explorer;

/// <summary>One file or folder in the Explorer. UI-thread object; children are loaded lazily and kept sorted (folders first).</summary>
public sealed class ExplorerNode : ObservableObject
{
    private bool _isExpanded, _isLoading, _isEditing, _isCut, _isDropTarget;
    private string _name;
    private string _editText = "";

    public ExplorerNode(Uri uri, string name, bool isDirectory, int depth, ExplorerNode? parent, bool isRoot = false)
    {
        Uri = uri;
        _name = name;
        IsDirectory = isDirectory;
        Depth = depth;
        Parent = parent;
        IsRoot = isRoot;
    }

    public Uri Uri { get; internal set; }
    public bool IsDirectory { get; }
    public bool IsRoot { get; }
    public int Depth { get; internal set; }
    public ExplorerNode? Parent { get; internal set; }
    /// <summary>Null until the folder was read; then the sorted children (folders first, then files, case-insensitive).</summary>
    public List<ExplorerNode>? Children { get; internal set; }

    public string Name { get => _name; internal set => SetProperty(ref _name, value); }
    public bool IsExpanded { get => _isExpanded; internal set => SetProperty(ref _isExpanded, value); }
    public bool IsLoading { get => _isLoading; internal set => SetProperty(ref _isLoading, value); }
    public bool IsEditing { get => _isEditing; internal set => SetProperty(ref _isEditing, value); }
    public bool IsCut { get => _isCut; internal set => SetProperty(ref _isCut, value); }
    /// <summary>The folder a drag over the Explorer would drop into (its row is highlighted).</summary>
    public bool IsDropTarget { get => _isDropTarget; internal set => SetProperty(ref _isDropTarget, value); }
    public string EditText { get => _editText; set => SetProperty(ref _editText, value); }

    private Core.Scm.FileDecoration? _decoration;

    /// <summary>The source-control decoration (status letter and colour), set by the view-model from <see cref="Core.Scm.IFileDecorationProvider"/>.</summary>
    public Core.Scm.FileDecoration? Decoration { get => _decoration; internal set => SetProperty(ref _decoration, value); }

    public double IndentWidth => Depth * 14;
    public string IconKey => IsDirectory ? (_isExpanded ? "folder-opened" : "folder") : ExplorerIcons.For(Name).Key;
    public string IconColor => IsDirectory ? "#DCB67A" : ExplorerIcons.For(Name).Color;
    public string Chevron => IsDirectory ? (_isExpanded ? "chevron-down" : "chevron-right") : "";

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(IsExpanded))
        {
            OnPropertyChanged(nameof(IconKey));
            OnPropertyChanged(nameof(Chevron));
        }
        else if (e.PropertyName == nameof(Name)) { OnPropertyChanged(nameof(IconKey)); OnPropertyChanged(nameof(IconColor)); }
    }

    public IEnumerable<ExplorerNode> Descendants()
    {
        if (Children is null) yield break;
        foreach (var c in Children)
        {
            yield return c;
            foreach (var d in c.Descendants()) yield return d;
        }
    }

    /// <summary>Visible descendants in display order (expanded subtrees only).</summary>
    public IEnumerable<ExplorerNode> VisibleDescendants()
    {
        if (!_isExpanded || Children is null) yield break;
        foreach (var c in Children)
        {
            yield return c;
            foreach (var d in c.VisibleDescendants()) yield return d;
        }
    }

    public override string ToString() => Name;
}

/// <summary>Built-in file icon set by extension (codicon glyph + a fixed type colour). Extension icon themes are a later-wave feature.</summary>
public static class ExplorerIcons
{
    public readonly record struct Icon(string Key, string Color);

    private static readonly Dictionary<string, Icon> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = new("symbol-class", "#68A8E4"), [".csx"] = new("symbol-class", "#68A8E4"), [".csproj"] = new("file-code", "#A074C4"), [".slnx"] = new("file-code", "#A074C4"), [".sln"] = new("file-code", "#A074C4"),
        [".ts"] = new("file-code", "#3178C6"), [".tsx"] = new("file-code", "#3178C6"), [".js"] = new("file-code", "#E8D44D"), [".jsx"] = new("file-code", "#E8D44D"), [".mjs"] = new("file-code", "#E8D44D"),
        [".json"] = new("json", "#CBCB41"), [".jsonc"] = new("json", "#CBCB41"), [".md"] = new("markdown", "#519ABA"), [".markdown"] = new("markdown", "#519ABA"),
        [".py"] = new("file-code", "#4B8BBE"), [".rs"] = new("file-code", "#DEA584"), [".go"] = new("file-code", "#00ADD8"), [".java"] = new("file-code", "#CC3E44"),
        [".c"] = new("file-code", "#599EFF"), [".h"] = new("file-code", "#A074C4"), [".cpp"] = new("file-code", "#599EFF"), [".hpp"] = new("file-code", "#A074C4"),
        [".html"] = new("file-code", "#E44D26"), [".css"] = new("file-code", "#42A5F5"), [".scss"] = new("file-code", "#F55385"), [".xml"] = new("file-code", "#E37933"),
        [".axaml"] = new("file-code", "#A074C4"), [".xaml"] = new("file-code", "#A074C4"), [".yml"] = new("settings", "#CB171E"), [".yaml"] = new("settings", "#CB171E"), [".toml"] = new("settings", "#9C4221"),
        [".sh"] = new("terminal-bash", "#89E051"), [".ps1"] = new("terminal", "#519ABA"), [".png"] = new("file-media", "#A074C4"), [".jpg"] = new("file-media", "#A074C4"), [".jpeg"] = new("file-media", "#A074C4"),
        [".gif"] = new("file-media", "#A074C4"), [".svg"] = new("file-media", "#FFB13B"), [".pdf"] = new("file-pdf", "#CC3E44"), [".zip"] = new("file-zip", "#CBCB41"), [".gz"] = new("file-zip", "#CBCB41"),
        [".dll"] = new("file-binary", "#8C8C8C"), [".exe"] = new("file-binary", "#8C8C8C"), [".so"] = new("file-binary", "#8C8C8C"), [".txt"] = new("note", "#9A9A9A"), [".log"] = new("note", "#9A9A9A"),
        [".gitignore"] = new("source-control", "#F05133"), [".editorconfig"] = new("settings", "#9A9A9A"),
    };

    private static readonly Icon Default = new("file", "#9A9A9A");

    public static Icon For(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (ext.Length > 0 && ByExtension.TryGetValue(ext, out var icon)) return icon;
        return ByExtension.TryGetValue(fileName, out var byName) ? byName : Default;
    }
}
