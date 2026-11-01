using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Platform;
using CsCode.App.Tests.RealApp.Vim;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Editor.View;
using CsCode.Panels.Navigation;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>
/// Call hierarchy, Peek Definition and linked editing in the REAL application with Vim on (the default): the preset keys go through the window's
/// keyboard pipeline and the Vim adapter's key policy, Esc through the engine, and linked editing mirrors Vim's own Insert-mode typing.
/// Language servers are off in <see cref="RealAppHost"/>; the providers are registered into the real feature registry.
/// </summary>
public class LanguageNavigationTests
{
    private const string Source = "class Program\n{\n    static void Main()\n    {\n        Run();\n    }\n\n    static void Run() { }\n}\n";

    private static RawInputModifiers Command => OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    private sealed class Calls(Uri uri) : ICallHierarchyProvider
    {
        private HierarchyItem Item(string name, int line) =>
            new(name, SymbolKind.Method, uri, TextRange.FromLines(line, 4, line, 30), TextRange.FromLines(line, 16, line, 16 + name.Length), null, JsonSerializer.SerializeToElement(name));

        public ValueTask<IReadOnlyList<HierarchyItem>?> PrepareCallHierarchyAsync(ITextDocument document, TextPosition position, CancellationToken ct) =>
            new((IReadOnlyList<HierarchyItem>?)(position.Line == 7 ? [Item("Run", 7)] : []));

        public ValueTask<IReadOnlyList<CallHierarchyIncomingCall>?> IncomingCallsAsync(HierarchyItem item, CancellationToken ct) =>
            new((IReadOnlyList<CallHierarchyIncomingCall>?)(item.Name == "Run" ? [new CallHierarchyIncomingCall(Item("Main", 2), [TextRange.FromLines(4, 8, 4, 11)])] : []));

        public ValueTask<IReadOnlyList<CallHierarchyOutgoingCall>?> OutgoingCallsAsync(HierarchyItem item, CancellationToken ct) => new((IReadOnlyList<CallHierarchyOutgoingCall>?)[]);
    }

    private sealed class Definitions(Uri uri) : IDefinitionProvider
    {
        public ValueTask<IReadOnlyList<LocationLink>?> ProvideDefinitionAsync(ITextDocument document, TextPosition position, DefinitionKind kind, CancellationToken ct) =>
            new((IReadOnlyList<LocationLink>?)[new LocationLink(uri, TextRange.FromLines(7, 4, 7, 25), TextRange.FromLines(7, 16, 7, 19))]);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task The_keymaps_View_Call_Hierarchy_key_shows_the_callers_in_the_Hierarchy_window_and_a_row_previews_its_call_site()
    {
        await using var app = await VimApp.StartAsync(Source, "Program.cs");
        var uri = app.View.Document.Uri;
        using var reg = app.Host.Get<ILanguageFeatureRegistry>().Register<ICallHierarchyProvider>(DocumentSelector.All, new Calls(uri));
        await DevScene.SettleAsync(5);
        app.SetCaret(7, 18);
        await DevScene.SettleAsync(3);

        // Ctrl+K, Ctrl+T (Cmd+K, Cmd+T on a Mac), Visual Studio's View Call Hierarchy; Vim leaves Ctrl+K to the shell
        app.Press(Key.K, Command);
        app.Press(Key.T, Command);
        var host = app.Host.Get<HierarchyHost>();
        Assert.True(await RealAppHost.WaitFor(() => host.Model.Rows.Count == 2), $"rows: {string.Join("|", host.Model.Rows)}");
        Assert.Equal("Calls To 'Run'", host.Model.Header);
        Assert.True(await RealAppHost.WaitFor(() => app.Host.Shell.Tools.IsVisible(HierarchyHost.ViewId)), "the Hierarchy window shows");

        var view = (HierarchyView)app.Host.Shell.Tools.Get(HierarchyHost.ViewId)!.LiveView!;
        view.GetVisualDescendants().OfType<Avalonia.Controls.ListBox>().Single().SelectedIndex = 1; // Main: its call of Run
        Assert.True(await RealAppHost.WaitFor(() => app.View.Modal.Controller.Selections[0].Start == new TextPosition(4, 8)), $"the call site selected: {app.View.Modal.Controller.Selections[0]}");
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Alt_F12_peeks_the_definition_under_the_line_and_Vims_Escape_closes_it()
    {
        await using var app = await VimApp.StartAsync(Source, "Program.cs");
        var uri = app.View.Document.Uri;
        using var reg = app.Host.Get<ILanguageFeatureRegistry>().Register<IDefinitionProvider>(DocumentSelector.All, new Definitions(uri));
        await DevScene.SettleAsync(5);
        app.SetCaret(4, 9);
        var keys = app.Host.Get<IContextKeyService>();
        CodeEditorControl? Embedded() => app.View.GetVisualDescendants().OfType<CodeEditorControl>().FirstOrDefault();
        app.Press(Key.F12, RawInputModifiers.Alt);
        Assert.True(await RealAppHost.WaitFor(() => Embedded() is not null), "the peek opened");
        Assert.True(keys.Get("referenceSearchVisible") is true);
        Assert.Same(app.View.Document, Embedded()!.Document); // the same file: its own document, editable
        Assert.Equal(new TextPosition(7, 16), Embedded()!.Modal.Controller.Caret);
        Assert.Equal(new TextPosition(4, 9), app.Caret); // the view's caret did not move

        await app.KeysAsync("<Esc>");
        Assert.Null(Embedded());
        Assert.True(keys.Get("referenceSearchVisible") is false);
        Assert.True(app.View.Modal.Controller.IsFocused);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Linked_editing_mirrors_Vims_insert_mode_typing_and_one_u_undoes_both_tags()
    {
        const string html = "<div>\n  <span>text</span>\n</div>\n";
        await using var app = await VimApp.StartAsync(html, "index.html", settings: "\"editor.linkedEditing\": true");
        using var reg = app.Host.Get<ILanguageFeatureRegistry>().Register<ILinkedEditingRangeProvider>(DocumentSelector.All, new SpanLinker());
        await DevScene.SettleAsync(5);
        app.SetCaret(1, 6); // on the 'n' of <span: a appends at the end of the name
        await app.Host.RunAsync("editor.action.linkedEditing"); // asks at once (the setting asks after the caret rests)

        await app.KeysAsync("ax");
        await app.KeysAsync("y<Esc>");
        Assert.Equal("<div>\n  <spanxy>text</spanxy>\n</div>\n", app.Text);
        await app.KeysAsync("u");
        Assert.Equal(html, app.Text);
    }

    /// <summary>Links "span" in <c>&lt;span&gt;text&lt;/span&gt;</c> on line 1, wherever its name has grown to.</summary>
    private sealed class SpanLinker : ILinkedEditingRangeProvider
    {
        public ValueTask<LinkedEditingRanges?> ProvideLinkedEditingRangesAsync(ITextDocument document, TextPosition position, CancellationToken ct)
        {
            var line = document.GetLineText(1);
            var open = line.IndexOf('<') + 1;
            var openEnd = line.IndexOf('>');
            var close = line.IndexOf("</", StringComparison.Ordinal) + 2;
            var closeEnd = line.LastIndexOf('>');
            if (position.Line != 1 || position.Character < open || position.Character > openEnd) return new((LinkedEditingRanges?)null);
            return new(new LinkedEditingRanges([TextRange.FromLines(1, open, 1, openEnd), TextRange.FromLines(1, close, 1, closeEnd)], "[A-Za-z0-9]+"));
        }
    }
}
