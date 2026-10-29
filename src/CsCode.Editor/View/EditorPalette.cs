using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CsCode.Core.Theming;

namespace CsCode.Editor.View;

/// <summary>
/// AvaloniaEdit's <c>TextEditor</c> is a templated control: without its theme it renders nothing. The editor adds that theme to the
/// application's styles itself the first time a view is created, so the shell does not have to know about it.
/// </summary>
internal static class EditorStyles
{
    private static readonly ConditionalWeakTable<Application, object> Applied = new();

    public static void Ensure()
    {
        var app = Application.Current;
        if (app is null || Applied.TryGetValue(app, out _)) return;
        Applied.Add(app, new object());
        app.Styles.Add(new StyleInclude(new Uri("avares://AvaloniaEdit/")) { Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml") });
    }
}

/// <summary>The brushes and pens of one theme as the editor uses them. Built from <see cref="IColorThemeService"/>; replaced wholesale on a theme change.</summary>
public sealed class EditorPalette
{
    private EditorPalette() { }

    public required IBrush Background { get; init; }
    public required IBrush Foreground { get; init; }
    public required IBrush LineNumber { get; init; }
    public required IBrush ActiveLineNumber { get; init; }
    public required IBrush Selection { get; init; }
    public required IBrush Caret { get; init; }
    public required IBrush CurrentLine { get; init; }
    public required IPen CurrentLineBorder { get; init; }
    public required IBrush Whitespace { get; init; }
    public required IPen Ruler { get; init; }
    public required IPen IndentGuide { get; init; }
    public required IPen IndentGuideActive { get; init; }
    public required IBrush FindMatch { get; init; }
    public required IBrush BracketMatchBackground { get; init; }
    public required IPen BracketMatchBorder { get; init; }
    public required IBrush[] BracketColors { get; init; }
    public required IBrush UnexpectedBracket { get; init; }
    public required IPen Error { get; init; }
    public required IPen Warning { get; init; }
    public required IPen Info { get; init; }
    public required IPen Hint { get; init; }
    public required IBrush WordHighlight { get; init; }
    public required IBrush WordHighlightStrong { get; init; }
    public required IBrush SelectionHighlight { get; init; }
    public required IBrush StackFrame { get; init; }
    public required IBrush FocusedStackFrame { get; init; }
    public required IBrush BreakpointFill { get; init; }
    public required IBrush BreakpointDisabled { get; init; }
    public required IBrush StackFrameArrow { get; init; }
    public required IBrush Link { get; init; }
    public required IBrush WidgetBackground { get; init; }
    public required IBrush WidgetForeground { get; init; }
    public required IBrush WidgetBorder { get; init; }
    public required IBrush SuggestSelected { get; init; }
    public required IBrush SuggestSelectedForeground { get; init; }
    public required IBrush SuggestHighlight { get; init; }
    public required IBrush DescriptionForeground { get; init; }
    public required IBrush CodeBlockBackground { get; init; }
    public required IBrush InlayForeground { get; init; }
    public required IBrush InlayBackground { get; init; }
    public required IBrush CodeLens { get; init; }
    public required IBrush Gutter { get; init; }
    public required IBrush FoldingControl { get; init; }
    public required IBrush Overview { get; init; }
    public required IBrush SnippetTabstop { get; init; }
    public required Color DefaultForeground { get; init; }

    public static EditorPalette From(IColorThemeService themes)
    {
        IBrush B(string key) => new ImmutableSolidColorBrush(ToColor(themes.GetColor(key)));
        IPen P(string key, double thickness = 1) => new Pen(B(key), thickness);
        // Bracket pair colours cycle through three (VS Code's editorBracketHighlight.foreground1..6 repeat 1-3).
        var brackets = Enumerable.Range(1, 6).Select(i => B($"editorBracketHighlight.foreground{i}")).ToArray();
        return new EditorPalette
        {
            Background = B("editor.background"),
            Foreground = B("editor.foreground"),
            LineNumber = B("editorLineNumber.foreground"),
            ActiveLineNumber = B("editorLineNumber.activeForeground"),
            Selection = B("editor.selectionBackground"),
            Caret = B("editorCursor.foreground"),
            CurrentLine = B("editor.lineHighlightBackground"),
            CurrentLineBorder = P("editor.lineHighlightBorder"),
            Whitespace = B("editorWhitespace.foreground"),
            Ruler = P("editorRuler.foreground"),
            IndentGuide = P("editorIndentGuide.background1"),
            IndentGuideActive = P("editorIndentGuide.activeBackground1"),
            FindMatch = B("editor.findMatchHighlightBackground"),
            BracketMatchBackground = B("editorBracketMatch.background"),
            BracketMatchBorder = P("editorBracketMatch.border"),
            BracketColors = brackets,
            UnexpectedBracket = B("editorBracketHighlight.unexpectedBracket.foreground"),
            Error = P("editorError.foreground"),
            Warning = P("editorWarning.foreground"),
            Info = P("editorInfo.foreground"),
            Hint = P("editorHint.foreground"),
            WordHighlight = B("editor.wordHighlightBackground"),
            WordHighlightStrong = B("editor.wordHighlightStrongBackground"),
            SelectionHighlight = B("editor.selectionHighlightBackground"),
            StackFrame = B("editor.stackFrameHighlightBackground"),
            FocusedStackFrame = B("editor.focusedStackFrameHighlightBackground"),
            BreakpointFill = B("debugIcon.breakpointForeground"),
            BreakpointDisabled = B("debugIcon.breakpointDisabledForeground"),
            StackFrameArrow = B("debugIcon.breakpointCurrentStackframeForeground"),
            Link = B("editorLink.activeForeground"),
            WidgetBackground = B("editorWidget.background"),
            WidgetForeground = B("editorWidget.foreground"),
            WidgetBorder = B("editorWidget.border"),
            SuggestSelected = B("editorSuggestWidget.selectedBackground"),
            SuggestSelectedForeground = B("editorSuggestWidget.selectedForeground"),
            SuggestHighlight = B("editorSuggestWidget.highlightForeground"),
            DescriptionForeground = B("descriptionForeground"),
            CodeBlockBackground = B("textCodeBlock.background"),
            InlayForeground = B("editorInlayHint.foreground"),
            InlayBackground = B("editorInlayHint.background"),
            CodeLens = B("editorCodeLens.foreground"),
            Gutter = B("editorGutter.background"),
            FoldingControl = B("editorGutter.foldingControlForeground"),
            Overview = B("editorOverviewRuler.border"),
            SnippetTabstop = B("editor.snippetTabstopHighlightBackground"),
            DefaultForeground = ToColor(themes.GetColor("editor.foreground")),
        };
    }

    public static Color ToColor(Rgba c) => Color.FromArgb(c.A, c.R, c.G, c.B);
}
