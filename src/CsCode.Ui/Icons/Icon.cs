using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace CsCode.Ui.Icons;

/// <summary>Codicon catalog. Icons are vector paths (crisp at any DPI, recoloured by foreground), parsed lazily and cached.</summary>
public static class IconCatalog
{
    private static readonly Dictionary<string, string> Data = IconPaths.All.ToDictionary(i => i.Name, i => i.Data, StringComparer.Ordinal);
    private static readonly Dictionary<string, Geometry> Cache = new(StringComparer.Ordinal);

    public static IReadOnlyCollection<string> Names => Data.Keys;
    public static bool Contains(string? name) => name is not null && Data.ContainsKey(name);

    /// <summary>Geometry in the catalog's native 100-unit box (y in [-100, 0]); see <see cref="Icon"/> for the transform. UI thread.</summary>
    public static Geometry? Get(string? name)
    {
        if (name is null || !Data.TryGetValue(name, out var path)) return null;
        if (!Cache.TryGetValue(name, out var g)) Cache[name] = g = Geometry.Parse(path);
        return g;
    }

    /// <summary>Drops parsed geometries (an <c>IMemoryTrimmable</c> hook; they re-parse on demand).</summary>
    public static int Trim()
    {
        var n = Cache.Count;
        Cache.Clear();
        return n;
    }
}

/// <summary>Names of the Codicons the shell itself uses (the full catalog is <see cref="IconCatalog.Names"/>).</summary>
public static class IconKey
{
    public const string Files = "files", Folder = "folder", FolderOpened = "folder-opened", File = "file", NewFile = "new-file", NewFolder = "new-folder";
    public const string Save = "save", SaveAll = "save-all", Search = "search", Close = "close", CloseAll = "close-all", Add = "add", Remove = "remove";
    public const string ChevronRight = "chevron-right", ChevronDown = "chevron-down", ChevronUp = "chevron-up", ChevronLeft = "chevron-left";
    public const string ArrowLeft = "arrow-left", ArrowRight = "arrow-right", Refresh = "refresh", Gear = "settings-gear", Check = "check";
    public const string Error = "error", Warning = "warning", Info = "info", Output = "output", Debug = "debug-alt", Play = "debug-start", Stop = "debug-stop";
    public const string Pause = "debug-pause", Continue = "debug-continue", StepOver = "debug-step-over", StepInto = "debug-step-into", StepOut = "debug-step-out", Restart = "debug-restart";
    public const string Terminal = "terminal", Bell = "bell", BellDot = "bell-dot", Pin = "pin", Pinned = "pinned", Filter = "filter", Copy = "copy", Trash = "trash", Edit = "edit";
    public const string Home = "home", Remote = "remote", SourceControl = "source-control", Symbol = "symbol-misc", References = "references", Layers = "layers", Dash = "dash";
    public const string WindowClose = "chrome-close", WindowMax = "chrome-maximize", WindowMin = "chrome-minimize", WindowRestore = "chrome-restore";
    public const string Undo = "discard", Redo = "redo", Ellipsis = "ellipsis", Lightbulb = "lightbulb", Sync = "sync", Loading = "loading", Circle = "circle-filled";
    public const string SplitRight = "split-horizontal", SplitDown = "split-vertical", Menu = "menu", Tools = "tools", History = "history", Key = "key", ListTree = "list-tree";
}

/// <summary>
/// Draws one Codicon at <see cref="Size"/> device-independent pixels in the inherited foreground colour. The catalog paths live in a
/// 100-unit box with y in [-100, 0], so the transform scales by Size/100 and shifts down by Size.
/// </summary>
public sealed class Icon : Control
{
    public static readonly StyledProperty<string?> KindProperty = AvaloniaProperty.Register<Icon, string?>(nameof(Kind));
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<Icon, double>(nameof(Size), 16);
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<Icon>();

    static Icon()
    {
        AffectsRender<Icon>(KindProperty, ForegroundProperty);
        AffectsMeasure<Icon>(SizeProperty);
    }

    public Icon() { }
    public Icon(string kind, double size = 16) { Kind = kind; Size = size; }

    public string? Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        var geometry = IconCatalog.Get(Kind);
        if (geometry is null || Foreground is not { } brush) return;
        var s = Size / 100.0;
        using (context.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(0, Size)))
            context.DrawGeometry(brush, null, geometry);
    }
}
