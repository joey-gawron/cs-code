using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using CsCode.Core.Debugging;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Editor.Features;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.View;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static CsCode.Editor.Tests.Support.ViewTestHelpers;

namespace CsCode.Editor.Tests;

/// <summary>
/// Representative editors rendered headlessly and written to <c>.scratch/screenshots/</c> (git-ignored) in the dark and the light built-in theme:
/// C# with squiggles, breakpoints, the execution line, a folded region, bracket matching and occurrence highlights; and the completion, hover and
/// signature-help popups. The tests assert the content is really on screen (pixel colours), and the PNGs are for a human or an agent to look at.
/// </summary>
public class ScreenshotTests
{
    private const string Source = """
        using System;
        using System.Collections.Generic;

        namespace Demo
        {
            // A small program
            public class Program
            {
                public static int Main(string[] args)
                {
                    var message = "hello, " + args.Length;
                    Console.WriteLine(message);
                    foreach (var arg in args)
                    {
                        Console.WriteLine(arg);
                    }
                    return Compute(1, 2);
                }

                private static int Compute(int first, int second) => first + second;
            }
        }
        """;

    private static string ScreenshotDir()
    {
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.scratch/screenshots"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Save(Window window, string name)
    {
        ViewHost.Pump();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var fs = File.Create(Path.Combine(ScreenshotDir(), name + ".png"));
        frame!.Save(fs, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static async Task<(ViewHost Host, FakeDebugService Debug, EditorDocumentHolder Doc, CodeEditorControl View, Window Window)> Prepare(string theme)
    {
        var debug = new FakeDebugService();
        var host = ViewHost.Create(s => s.AddSingleton<IDebugService>(debug));
        await host.Factory.Themes.SetCurrentAsync(theme);
        host.Factory.Services.Features.Register<ICompletionProvider>(DocumentSelector.All, FakeCompletionProvider.Of(
        [
            new CompletionItem { Label = "WriteLine", Kind = CompletionItemKind.Method, Detail = "void Console.WriteLine(string value)", Documentation = MarkupContent.Markdown("Writes the specified string value, followed by the current line terminator, to the **standard output** stream.") },
            new CompletionItem { Label = "Write", Kind = CompletionItemKind.Method, Detail = "void Console.Write(string value)" },
            new CompletionItem { Label = "WindowWidth", Kind = CompletionItemKind.Property, Detail = "int Console.WindowWidth" },
            new CompletionItem { Label = "Warning", Kind = CompletionItemKind.Enum },
            new CompletionItem { Label = "while", Kind = CompletionItemKind.Keyword },
            new CompletionItem { Label = "Wrapper", Kind = CompletionItemKind.Class },
        ]));
        host.Factory.Services.Features.Register<IHoverProvider>(DocumentSelector.All, new FakeHoverProvider((_, _, _) => Task.FromResult<Hover?>(new Hover(
        [
            MarkupContent.Markdown("```csharp\npublic static int Compute(int first, int second)\n```\n\nAdds the **two** operands and returns the _sum_. See [docs](https://example.com).\n\n- `first`: the left operand\n- `second`: the right operand"),
        ]))));
        host.Factory.Services.Features.Register<ISignatureHelpProvider>(DocumentSelector.All, new FakeSignatureProvider((_, _, _, _) => Task.FromResult<SignatureHelp?>(new SignatureHelp(
        [
            new SignatureInformation("int Compute(int first, int second)", MarkupContent.Plain("Adds two numbers."), [new ParameterInformation("int first"), new ParameterInformation("int second", MarkupContent.Plain("The right operand."))], ActiveParameter: 1),
            new SignatureInformation("double Compute(double a, double b, double c)", null, [new ParameterInformation("double a"), new ParameterInformation("double b"), new ParameterInformation("double c")]),
        ]))));
        var (doc, view, window) = await host.OpenAsync(Source, "Program.cs", 900, 560);
        await ViewHost.WaitFor(() => view.Tokens is { } t && t.Store.ValidLines >= 20, "tokens");
        return (host, debug, new EditorDocumentHolder(doc), view, window);
    }

    private sealed record EditorDocumentHolder(CsCode.Editor.Documents.EditorDocument Doc);

    [AvaloniaTheory]
    [InlineData("dark", "vscode.dark-plus")]
    [InlineData("light", "vscode.light-plus")]
    public async Task The_main_editor_with_squiggles_breakpoints_stop_line_fold_and_highlights(string name, string theme)
    {
        var (host, debug, holder, view, window) = await Prepare(theme);
        using var _ = host;
        var doc = holder.Doc;
        // diagnostics of every severity, a hint with "unnecessary" fading
        view.Services.Diagnostics.Set(doc.Uri, "demo",
        [
            new Diagnostic(TextRange.FromLines(10, 38, 10, 49), "Cannot implicitly convert type 'int' to 'string'", DiagnosticSeverity.Error, "CS0029", "csharp"),
            new Diagnostic(TextRange.FromLines(12, 25, 12, 28), "Variable 'arg' is declared but never used", DiagnosticSeverity.Warning, "CS0168", "csharp"),
            new Diagnostic(TextRange.FromLines(16, 19, 16, 26), "Method can be made static", DiagnosticSeverity.Information, "IDE0290", "csharp"),
            new Diagnostic(TextRange.FromLines(0, 0, 0, 13), "Using directive is unnecessary", DiagnosticSeverity.Hint, "IDE0005", "csharp", [DiagnosticTag.Unnecessary]),
        ]);
        // breakpoints: a plain one, a conditional one, an unverified one; the debugger is stopped at line 11
        debug.Manager.Add(doc.Uri, 10);
        debug.Manager.Add(doc.Uri, 11, condition: "args.Length > 2");
        var unverified = debug.Manager.Add(doc.Uri, 16);
        debug.Manager.ReportStatus(new DebugBreakpointStatus(new FakeDebugSession(), unverified.Id, false, null, "No executable code at this line"));
        debug.Stop(doc.Uri, 10);
        // fold the foreach body, put the caret on a bracket and on an identifier used several times
        var folding = view.Feature<FoldingFeature>()!;
        await ViewHost.WaitFor(() => folding.RangeCount > 3, "fold ranges");
        view.Controller.SetSelection(TextRange.At(13, 12)); // the '{' of the foreach body
        folding.Fold();
        Assert.NotEmpty(folding.FoldedStartLines);
        view.Controller.SetSelection(TextRange.At(10, 19)); // inside `message` (used twice)
        await ViewHost.WaitFor(() => view.Feature<DiagnosticsFeature>()!.Count == 4, "diagnostics");
        await ViewHost.WaitFor(() => view.Feature<HighlightFeature>()!.Ranges.Count > 1, "occurrence highlights");
        await ViewHost.WaitFor(() => view.Feature<BracketFeature>()!.Depths is not null, "bracket depth map");
        Save(window, $"editor-{name}-main");

        // content checks: the breakpoint red, the stop-line yellow arrow, and the error squiggle colour are all on screen
        var frame = window.CaptureRenderedFrame()!;
        Assert.True(CountPixels(frame, ((Avalonia.Media.ISolidColorBrush)view.Palette.BreakpointFill).Color, 20) > 30, "breakpoint glyphs");
        Assert.True(CountPixels(frame, ((Avalonia.Media.ISolidColorBrush)view.Palette.StackFrameArrow).Color, 20) > 8, "execution arrow");
        Assert.True(CountPixels(frame, ((Avalonia.Media.ISolidColorBrush)view.Palette.Error.Brush!).Color, 60) > 10, "error squiggle");
    }

    [AvaloniaTheory]
    [InlineData("dark", "vscode.dark-plus")]
    [InlineData("light", "vscode.light-plus")]
    public async Task The_completion_popup(string name, string theme)
    {
        var (host, _, _, view, window) = await Prepare(theme);
        using var _ = host;
        view.Controller.SetSelection(TextRange.At(11, 11)); // after "Console." on `Console.WriteLine(message);`
        view.Controller.SetSelection(TextRange.At(11, 20)); // end of `Console.WriteLine`
        // replace the call with a fresh `Console.W` so the popup has a prefix to filter on
        view.Document.ApplyEdits([TextEdit.Insert(new TextPosition(11, 8), "Console.W\n        ")]);
        view.Controller.SetSelection(TextRange.At(11, 17));
        Type(view, "r");
        await ViewHost.WaitFor(() => view.Feature<CompletionFeature>()!.IsOpen, "popup opens");
        await ViewHost.WaitFor(() => view.Feature<CompletionFeature>()!.VisibleLabels.Count > 0, "items");
        await Task.Delay(300); // the selected item's documentation is resolved after a short delay
        Save(window, $"editor-{name}-completion");
        Assert.NotEmpty(view.Feature<CompletionFeature>()!.VisibleLabels);
    }

    [AvaloniaTheory]
    [InlineData("dark", "vscode.dark-plus")]
    [InlineData("light", "vscode.light-plus")]
    public async Task The_hover_and_signature_help_popups(string name, string theme)
    {
        var (host, _, holder, view, window) = await Prepare(theme);
        using var _ = host;
        var doc = holder.Doc;
        view.Services.Diagnostics.Set(doc.Uri, "demo", [new Diagnostic(TextRange.FromLines(16, 31, 16, 38), "'Compute' is declared but its result is unused", DiagnosticSeverity.Warning, "CS8321", "csharp")]);
        await ViewHost.WaitFor(() => view.Feature<DiagnosticsFeature>()!.Count == 1, "diagnostic");
        await view.Feature<HoverFeature>()!.HoverAtAsync(doc.OffsetAt(new TextPosition(16, 18)));
        ViewHost.Pump();
        Save(window, $"editor-{name}-hover");
        Assert.True(view.Feature<HoverFeature>()!.IsOpen);
        view.Feature<HoverFeature>()!.Hide();

        view.Controller.SetSelection(TextRange.At(16, 28));
        view.Feature<SignatureFeature>()!.Trigger();
        await ViewHost.WaitFor(() => view.Feature<SignatureFeature>()!.IsOpen, "signature popup");
        ViewHost.Pump();
        Save(window, $"editor-{name}-signature");
    }

    /// <summary>Code lenses above Main and Compute (one resolved later, a selection across a lens line, the caret on one) and semantic tokens.</summary>
    [AvaloniaTheory]
    [InlineData("dark", "vscode.dark-plus")]
    [InlineData("light", "vscode.light-plus")]
    [InlineData("vs-dark", "Visual Studio Dark (TextMateSharp)")]
    public async Task Code_lenses_and_semantic_tokens(string name, string theme)
    {
        using var host = ViewHost.Create();
        await host.Factory.Themes.SetCurrentAsync(theme);
        var lines = Source.Split('\n');
        int Line(string text) => Array.FindIndex(lines, l => l.Contains(text, StringComparison.Ordinal));
        host.Factory.Services.Features.Register<ICodeLensProvider>(DocumentSelector.All, new CodeLensViewTests.FakeLenses(_ =>
        [
            CodeLensViewTests.Lens(Line("class Program"), 4, "2 references", "editor.action.showReferences"),
            CodeLensViewTests.Lens(Line("int Main"), 8, "0 references", "editor.action.showReferences"),
            CodeLensViewTests.Lens(Line("int Main"), 8, "$(beaker) Run Test", "test.lens"),
            CodeLensViewTests.Lens(Line("int Main"), 8, "Debug Test", "test.lens"),
            CodeLensViewTests.Lens(Line("int Compute"), 8, "1 reference", "editor.action.showReferences"),
        ]));
        // "message" and "arg" as readonly variables, "Compute" as a static method, "args" as parameters
        var tokens = new List<(int, int, int, int, int)>();
        for (var l = 0; l < lines.Length; l++)
        {
            foreach (var (word, type, mods) in new[] { ("message", 1, 2), ("args", 3, 0), ("Compute", 2, 0) })
                for (var c = lines[l].IndexOf(word, StringComparison.Ordinal); c >= 0; c = lines[l].IndexOf(word, c + word.Length, StringComparison.Ordinal))
                    tokens.Add((l, c, word.Length, type, mods));
        }
        tokens.Sort();
        host.Factory.Services.Features.Register<ISemanticTokensProvider>(DocumentSelector.All, new SemanticTokenTests.FakeSemantic(SemanticTokenTests.Legend, _ => SemanticTokenTests.Encode([.. tokens])));
        host.Config.Set(LanguageRenderSettings.SemanticHighlightingKey, true);
        var (doc, view, window) = await host.OpenAsync(Source, "Program.cs", 900, 520);
        var lens = view.Feature<CodeLensFeature>()!;
        await ViewHost.WaitFor(() => lens.Placements.Count == 5, "lenses");
        await ViewHost.WaitFor(() => view.Feature<SemanticTokensFeature>()!.TokenCount == tokens.Count, "semantic tokens");
        var main = doc.Buffer.GetLineByNumber(Line("int Main") + 1);
        view.Editor.Select(main.Offset - 6, 26); // a selection over the end of one line into the lens line (start, length)
        Save(window, $"editor-{name}-codelens");
        view.TextArea.ClearSelection();
        view.Editor.CaretOffset = doc.Buffer.GetLineByNumber(Line("int Compute") + 1).Offset + 8;
        Save(window, $"editor-{name}-codelens-caret");
        var lensColor = ((Avalonia.Media.ISolidColorBrush)view.Palette.CodeLens).Color;
        Assert.True(CountPixels(window.CaptureRenderedFrame()!, lensColor, 30) > 50, "the lens titles are on screen");
    }
}
