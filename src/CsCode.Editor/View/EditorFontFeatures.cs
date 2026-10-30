using System.Reflection;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit.Rendering;

namespace CsCode.Editor.View;

/// <summary>
/// <c>editor.fontLigatures</c> for one view (<see cref="EditorSettings.FontFeatures"/>): on by default, so the bundled Fira Code draws its
/// <c>!=</c>, <c>=&gt;</c> and <c>===</c> ligatures; off draws them as their characters. AvaloniaEdit 12 has no font-feature setting and its text run properties never
/// carry any, so this line transformer gives every element of a visual line run properties that do (the formatter hands them to HarfBuzz).
/// They survive AvaloniaEdit's own splitting of elements (a colourizer's) because their <c>Clone</c> keeps them. Removed on dispose.
/// </summary>
internal sealed class EditorFontFeatures : IVisualLineTransformer, IDisposable, ISettingsAware
{
    // VisualLineElement.TextRunProperties has a private setter (AvaloniaEdit sets it before the transformers run); null if an upgrade drops it,
    // and CodeFontTests then fails: ligatures could not be turned off.
    private static readonly Action<VisualLineElement, VisualLineElementTextRunProperties>? SetProperties =
        typeof(VisualLineElement).GetProperty(nameof(VisualLineElement.TextRunProperties))?.GetSetMethod(nonPublic: true) is { } setter
            ? setter.CreateDelegate<Action<VisualLineElement, VisualLineElementTextRunProperties>>()
            : null;

    private readonly CodeEditorControl _v;
    private string _applied;
    private FontFeatureCollection? _features;

    public EditorFontFeatures(CodeEditorControl view)
    {
        _v = view;
        _applied = view.Settings.FontFeatures;
        _features = Parse(_applied);
        view.TextView.LineTransformers.Insert(0, this);
        if (_features is not null) view.TextView.Redraw();
    }

    /// <summary>The features this view's text is shaped with (null: the font's defaults).</summary>
    internal FontFeatureCollection? Features => _features;

    public void Transform(ITextRunConstructionContext context, IList<VisualLineElement> elements)
    {
        if (_features is null || SetProperties is null) return;
        foreach (var element in elements)
            if (element.TextRunProperties is not WithFeatures)
                SetProperties(element, new WithFeatures(element.TextRunProperties, _features));
    }

    public void OnSettingsChanged()
    {
        if (_v.Settings.FontFeatures == _applied) return;
        _applied = _v.Settings.FontFeatures;
        _features = Parse(_applied);
        _v.TextView.Redraw();
    }

    public void Dispose() => _v.TextView.LineTransformers.Remove(this);

    private static FontFeatureCollection? Parse(string features)
    {
        var collection = new FontFeatureCollection();
        foreach (var f in features.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try { collection.Add(FontFeature.Parse(f)); }
            catch (Exception e) when (e is FormatException or ArgumentException) { }
        }
        return collection.Count == 0 ? null : collection;
    }

    private sealed class WithFeatures(TextRunProperties properties, FontFeatureCollection features) : VisualLineElementTextRunProperties(properties)
    {
        public override FontFeatureCollection? FontFeatures => features;
        public override VisualLineElementTextRunProperties Clone() => new WithFeatures(this, features);
    }
}
