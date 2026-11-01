using System.Text.Json;
using CsCode.Core.Platform;

namespace CsCode.Editor.Markdown.Preview;

/// <summary>The preview's settings: VS Code's <c>markdown.preview.*</c> keys where VS Code has one, CsCode's own for the rest.</summary>
public static class MarkdownPreviewSettings
{
    public const string FontFamily = "markdown.preview.fontFamily";
    public const string FontSize = "markdown.preview.fontSize";
    public const string LineHeight = "markdown.preview.lineHeight";
    public const string Breaks = "markdown.preview.breaks";
    public const string Linkify = "markdown.preview.linkify";
    public const string ScrollPreviewWithEditor = "markdown.preview.scrollPreviewWithEditor";
    public const string ScrollEditorWithPreview = "markdown.preview.scrollEditorWithPreview";
    public const string DoubleClickToSwitchToEditor = "markdown.preview.doubleClickToSwitchToEditor";
    /// <summary>CsCode's own: images from http(s) URLs are fetched only when this is on (VS Code's preview security "Allow insecure content" analogue).</summary>
    public const string AllowRemoteImages = "cscode.markdown.preview.allowRemoteImages";
    /// <summary>CsCode's own: milliseconds between the last keystroke and the preview's update.</summary>
    public const string UpdateDelay = "cscode.markdown.preview.updateDelayMs";

    public const double DefaultFontSize = 14;
    public const double DefaultLineHeight = 1.6;
    public const int DefaultUpdateDelay = 300;

    private static ConfigurationProperty P(string key, object value, string description, string schema) =>
        new(key, JsonSerializer.SerializeToElement(value), description, JsonDocument.Parse(schema).RootElement.Clone());

    public static IReadOnlyList<ConfigurationProperty> Properties { get; } =
    [
        P(FontFamily, "", "Font family of the Markdown preview's text (empty: the user interface font). Code uses editor.fontFamily.", """{"type":"string"}"""),
        P(FontSize, DefaultFontSize, "Font size of the Markdown preview's text, in pixels.", """{"type":"number","minimum":6,"maximum":100}"""),
        P(LineHeight, DefaultLineHeight, "Line height of the Markdown preview's text, relative to its font size.", """{"type":"number","minimum":1,"maximum":3}"""),
        P(Breaks, false, "Render single line breaks in paragraphs as line breaks in the Markdown preview.", """{"type":"boolean"}"""),
        P(Linkify, true, "Turn URL-like text (www.example.com, https://example.com) into links in the Markdown preview.", """{"type":"boolean"}"""),
        P(ScrollPreviewWithEditor, true, "Scroll the Markdown preview when its editor scrolls.", """{"type":"boolean"}"""),
        P(ScrollEditorWithPreview, true, "Scroll the editor when its Markdown preview scrolls.", """{"type":"boolean"}"""),
        P(DoubleClickToSwitchToEditor, true, "Double-click in the Markdown preview to switch to the editor at that line.", """{"type":"boolean"}"""),
        P(AllowRemoteImages, false, "Load images from http(s) URLs in the Markdown preview. Off by default: a remote image tells its server that the file was opened.", """{"type":"boolean"}"""),
        P(UpdateDelay, DefaultUpdateDelay, "Milliseconds after the last change before the Markdown preview updates.", """{"type":"integer","minimum":0,"maximum":5000}"""),
    ];
}
