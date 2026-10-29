using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.Features;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.View;
using CsCode.TestDoubles;
using Xunit;
using static CsCode.Editor.Tests.Support.ViewTestHelpers;

namespace CsCode.Editor.Tests;

/// <summary>MUST 9: signature help, code actions (lightbulb + quick fix), go to definition / references, rename and formatting — through real providers' interfaces, in a real control.</summary>
public class SignatureActionsNavigationTests
{
    // ---- signature help ----

    private static SignatureHelp Help(int active = 0, int activeSignature = 0) => new(
        [
            new SignatureInformation("Add(int first, int second)", MarkupContent.Plain("Adds two numbers"), [new ParameterInformation("int first"), new ParameterInformation("int second", MarkupContent.Markdown("the *second* operand"))]),
            new SignatureInformation("Add(double a, double b, double c)", null, [new ParameterInformation("double a"), new ParameterInformation("double b"), new ParameterInformation("double c")]),
        ],
        activeSignature, active);

    private static SignatureFeature Sig(CodeEditorControl v) => v.Feature<SignatureFeature>()!;

    [AvaloniaFact]
    public async Task Typing_the_open_paren_shows_the_signature_and_comma_moves_the_active_parameter()
    {
        using var host = ViewHost.Create();
        var provider = new FakeSignatureProvider((_, pos, _, _) => Task.FromResult<SignatureHelp?>(Help(active: pos.Character >= 6 ? 1 : 0)));
        host.Factory.Services.Features.Register<ISignatureHelpProvider>(DocumentSelector.All, provider);
        var (_, view, _) = await host.OpenAsync("", "a.txt");
        Type(view, "Add(");
        await ViewHost.WaitFor(() => Sig(view).IsOpen, "signature popup");
        Assert.Contains("Add(int first, int second)", TextOf(Sig(view).PopupRoot));
        Assert.Equal(["int first"], BoldRuns(Sig(view).PopupRoot));
        Type(view, "1,");
        await ViewHost.WaitFor(() => BoldRuns(Sig(view).PopupRoot) is ["int second"], "active parameter moves on comma");
        Assert.Contains(provider.Contexts, c => c.IsRetrigger && c.TriggerCharacter == ",");
        Assert.Contains("second", TextOf(Sig(view).PopupRoot)); // the active parameter's documentation
    }

    [AvaloniaFact]
    public async Task Up_and_down_cycle_overloads_and_escape_closes()
    {
        using var host = ViewHost.Create();
        host.Factory.Services.Features.Register<ISignatureHelpProvider>(DocumentSelector.All, new FakeSignatureProvider((_, _, _, _) => Task.FromResult<SignatureHelp?>(Help())));
        var (_, view, window) = await host.OpenAsync("", "a.txt");
        Type(view, "Add(");
        await ViewHost.WaitFor(() => Sig(view).IsOpen, "popup");
        Assert.Contains("1/2", TextOf(Sig(view).PopupRoot));
        Key(window, Avalonia.Input.Key.Down);
        ViewHost.Pump();
        Assert.Equal(1, Sig(view).ActiveSignature);
        Assert.Contains("Add(double a, double b, double c)", TextOf(Sig(view).PopupRoot));
        Key(window, Avalonia.Input.Key.Down);
        ViewHost.Pump();
        Assert.Equal(0, Sig(view).ActiveSignature); // wraps
        Key(window, Avalonia.Input.Key.Up);
        ViewHost.Pump();
        Assert.Equal(1, Sig(view).ActiveSignature);
        Key(window, Avalonia.Input.Key.Escape);
        ViewHost.Pump();
        Assert.False(Sig(view).IsOpen);
        Assert.Equal("Add()", view.Document.GetText()); // (the closing paren was auto-inserted) and the arrow keys were not typed into the document
    }

    [AvaloniaFact]
    public async Task The_closing_paren_retriggers_and_a_null_answer_closes_the_popup()
    {
        using var host = ViewHost.Create();
        host.Factory.Services.Features.Register<ISignatureHelpProvider>(DocumentSelector.All, new FakeSignatureProvider((doc, pos, _, _) =>
            Task.FromResult<SignatureHelp?>(doc.GetLineText(0)[..pos.Character].Contains(')') ? null : Help())));
        var (_, view, _) = await host.OpenAsync("", "a.txt");
        Type(view, "Add(1");
        await ViewHost.WaitFor(() => Sig(view).IsOpen, "popup");
        Type(view, ")");
        await ViewHost.WaitFor(() => !Sig(view).IsOpen, "closed after the call ended");
    }

    [AvaloniaFact]
    public async Task A_signature_answer_for_older_text_is_not_shown_but_asked_for_again()
    {
        using var host = ViewHost.Create();
        var first = true;
        var gate = new TaskCompletionSource();
        var provider = new FakeSignatureProvider(async (doc, _, _, _) =>
        {
            if (first)
            {
                first = false;
                await gate.Task.ConfigureAwait(false);
            }
            return Help(active: doc.GetText().Contains("x") ? 1 : 0);
        });
        host.Factory.Services.Features.Register<ISignatureHelpProvider>(DocumentSelector.All, provider);
        var (_, view, _) = await host.OpenAsync("", "a.txt");
        Type(view, "Add(");
        await ViewHost.WaitFor(() => provider.Calls == 1, "first request in flight");
        Type(view, "x"); // typed while the answer is outstanding
        gate.SetResult();
        await ViewHost.WaitFor(() => Sig(view).IsOpen, "popup after the re-request");
        Assert.True(provider.Calls >= 2, "the stale answer must trigger a fresh request");
        Assert.Equal(["int second"], BoldRuns(Sig(view).PopupRoot)); // from the NEW text, not the stale answer
    }

    // ---- code actions ----

    private static CodeAction Fix(string title, Uri uri, TextRange range, string newText, bool preferred = false, string kind = "quickfix") =>
        new(title, kind, IsPreferred: preferred, Edit: new WorkspaceEdit(new Dictionary<Uri, IReadOnlyList<TextEdit>> { [uri] = [new TextEdit(range, newText)] }));

    [AvaloniaFact]
    public async Task Quick_fix_lists_actions_for_the_diagnostic_applies_the_chosen_edit_and_runs_its_command()
    {
        using var host = ViewHost.Create();
        var executed = new List<string>();
        host.Commands.Register("test.after", inv => { executed.Add(string.Join(",", inv.Args)); return ValueTask.FromResult<object?>(null); });
        var (doc, view, _) = await host.OpenAsync("int yy = 2;\n", "a.txt");
        var provider = new FakeCodeActionProvider((d, range, ctx, _) => Task.FromResult<IReadOnlyList<CodeAction>?>(
        [
            Fix("Remove unused variable", doc.Uri, TextRange.FromLines(0, 0, 0, 12), "", kind: "quickfix"),
            Fix("Rename to _yy", doc.Uri, TextRange.FromLines(0, 4, 0, 6), "_yy", preferred: true) with { Command = new Command("after", "test.after", [System.Text.Json.JsonSerializer.SerializeToElement("arg1")]) },
        ]));
        host.Factory.Services.Features.Register<ICodeActionProvider>(DocumentSelector.All, provider);
        view.Services.Diagnostics.Set(doc.Uri, "t", [new Diagnostic(TextRange.FromLines(0, 4, 0, 6), "unused", DiagnosticSeverity.Warning)]);
        await ViewHost.WaitFor(() => view.Feature<DiagnosticsFeature>()!.Count == 1, "diagnostic");
        view.Controller.SetSelection(TextRange.At(0, 5));
        host.Window.QuickPickResponses.Enqueue(0); // the preferred action sorts first
        await view.Feature<CodeActionFeature>()!.ShowQuickFixAsync();
        ViewHost.Pump();
        Assert.Equal("int _yy = 2;\n", doc.GetText());
        Assert.Single(host.Navigation.AppliedEdits);
        Assert.Single(executed);
        Assert.Contains("arg1", executed[0]);
        Assert.Contains(provider.Contexts, c => c.Diagnostics.Count == 1 && c.Diagnostics[0].Message == "unused" && c.TriggerKind == CodeActionTriggerKind.Invoked);
    }

    [AvaloniaFact]
    public async Task The_lightbulb_appears_when_a_provider_offers_something_and_not_otherwise()
    {
        using var host = ViewHost.Create();
        var offer = true;
        var (doc, view, _) = await host.OpenAsync("int yy = 2;\nint zz = 3;\n", "a.txt");
        host.Factory.Services.Features.Register<ICodeActionProvider>(DocumentSelector.All, new FakeCodeActionProvider((_, _, _, _) =>
            Task.FromResult<IReadOnlyList<CodeAction>?>(offer ? [Fix("Fix it", doc.Uri, TextRange.FromLines(0, 0, 0, 3), "var")] : [])));
        var bulb = view.Feature<CodeActionFeature>()!;
        view.Controller.SetSelection(TextRange.At(0, 2));
        await ViewHost.WaitFor(() => bulb.BulbVisible, "lightbulb after the caret settled");
        offer = false;
        view.Controller.SetSelection(TextRange.At(1, 2));
        await ViewHost.WaitFor(() => !bulb.BulbVisible, "lightbulb gone when nothing is offered");
    }

    [AvaloniaFact]
    public async Task Organize_imports_asks_for_the_source_kind_and_applies_it()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync("using B;\nusing A;\n", "a.txt");
        var provider = new FakeCodeActionProvider((_, _, ctx, _) => Task.FromResult<IReadOnlyList<CodeAction>?>(
        [
            Fix("Unrelated refactor", doc.Uri, TextRange.FromLines(0, 0, 0, 1), "X", kind: "refactor.extract"),
            Fix("Organize Usings", doc.Uri, TextRange.FromLines(0, 0, 1, 8), "using A;\nusing B;", kind: "source.organizeImports"),
        ]));
        host.Factory.Services.Features.Register<ICodeActionProvider>(DocumentSelector.All, provider);
        await view.Feature<CodeActionFeature>()!.OrganizeImportsAsync();
        ViewHost.Pump();
        Assert.Equal("using A;\nusing B;\n", doc.GetText());
        Assert.Contains(provider.Contexts, c => c.Only is ["source.organizeImports"]);
    }

    [AvaloniaFact]
    public async Task A_deferred_edit_is_resolved_before_it_is_applied()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync("abc\n", "a.txt");
        var lazy = new CodeAction("Lazy fix", "quickfix", Data: System.Text.Json.JsonSerializer.SerializeToElement(1));
        var provider = new FakeCodeActionProvider((_, _, _, _) => Task.FromResult<IReadOnlyList<CodeAction>?>([lazy]))
        {
            Resolve = a => a with { Edit = Fix("x", doc.Uri, TextRange.FromLines(0, 0, 0, 3), "resolved").Edit },
        };
        host.Factory.Services.Features.Register<ICodeActionProvider>(DocumentSelector.All, provider);
        await view.Feature<CodeActionFeature>()!.ShowQuickFixAsync();
        Assert.Equal("resolved\n", doc.GetText());
    }

    [AvaloniaFact]
    public async Task A_code_action_computed_for_older_text_is_not_applied()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync("abc\n", "a.txt");
        var gate = new TaskCompletionSource();
        host.Factory.Services.Features.Register<ICodeActionProvider>(DocumentSelector.All, new FakeCodeActionProvider(async (_, _, _, _) =>
        {
            await gate.Task.ConfigureAwait(false);
            return [Fix("late fix", doc.Uri, TextRange.FromLines(0, 0, 0, 3), "NO")];
        }));
        var pending = view.Feature<CodeActionFeature>()!.ShowQuickFixAsync();
        doc.ApplyEdits([TextEdit.Insert(TextPosition.Zero, "// typed meanwhile\n")]);
        gate.SetResult();
        await pending;
        Assert.Equal("// typed meanwhile\nabc\n", doc.GetText());
        Assert.Empty(host.Navigation.AppliedEdits);
    }

    [AvaloniaFact]
    public async Task No_actions_says_so_in_the_status_bar()
    {
        using var host = ViewHost.Create();
        host.Factory.Services.Features.Register<ICodeActionProvider>(DocumentSelector.All, new FakeCodeActionProvider((_, _, _, _) => Task.FromResult<IReadOnlyList<CodeAction>?>([])));
        var (_, view, _) = await host.OpenAsync("abc", "a.txt");
        await view.Feature<CodeActionFeature>()!.ShowQuickFixAsync();
        Assert.Contains("No code actions available", host.Window.StatusMessages);
    }

    // ---- navigation ----

    private static INavigationFeature Nav(CodeEditorControl v) => v.Feature<INavigationFeature>()!;

    private static LocationLink Link(Uri uri, int line, int from, int to) => new(uri, TextRange.FromLines(line, 0, line + 1, 0), TextRange.FromLines(line, from, line, to));

    [AvaloniaFact]
    public async Task Go_to_definition_in_another_file_goes_through_the_navigation_service()
    {
        using var host = ViewHost.Create();
        var target = new Uri("file:///other/Target.cs");
        host.Factory.Services.Features.Register<IDefinitionProvider>(DocumentSelector.All, new FakeDefinitionProvider((_, _, _) => Task.FromResult<IReadOnlyList<LocationLink>?>([Link(target, 10, 4, 9)])));
        var (_, view, _) = await host.OpenAsync("Target t;", "a.cs");
        view.Controller.SetSelection(TextRange.At(0, 2));
        await Nav(view).GoToDefinitionAsync();
        Assert.Equal((target, (TextRange?)TextRange.FromLines(10, 4, 10, 9)), host.Navigation.Opened.Single());
    }

    [AvaloniaFact]
    public async Task Go_to_definition_in_the_same_file_moves_the_caret_there()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync("int a;\nint b = a;\n", "a.cs");
        host.Factory.Services.Features.Register<IDefinitionProvider>(DocumentSelector.All, new FakeDefinitionProvider((_, _, _) => Task.FromResult<IReadOnlyList<LocationLink>?>([Link(doc.Uri, 0, 4, 5)])));
        view.Controller.SetSelection(TextRange.At(1, 9));
        await Nav(view).GoToDefinitionAsync();
        Assert.Equal(TextRange.FromLines(0, 4, 0, 5), view.Controller.Selections[0]);
        Assert.Empty(host.Navigation.Opened);
    }

    [AvaloniaFact]
    public async Task Several_definitions_are_offered_in_a_quick_pick()
    {
        using var host = ViewHost.Create();
        var a = new Uri("file:///x/A.cs");
        var b = new Uri("file:///x/B.cs");
        host.Factory.Services.Features.Register<IDefinitionProvider>(DocumentSelector.All, new FakeDefinitionProvider((_, _, _) => Task.FromResult<IReadOnlyList<LocationLink>?>([Link(a, 1, 0, 1), Link(b, 2, 0, 1)])));
        var (_, view, _) = await host.OpenAsync("x", "a.cs");
        host.Window.QuickPickResponses.Enqueue(1);
        await Nav(view).GoToDefinitionAsync();
        Assert.Equal(b, host.Navigation.Opened.Single().Uri);
    }

    [AvaloniaFact]
    public async Task No_definition_says_so_in_the_status_bar()
    {
        using var host = ViewHost.Create();
        host.Factory.Services.Features.Register<IDefinitionProvider>(DocumentSelector.All, new FakeDefinitionProvider((_, _, _) => Task.FromResult<IReadOnlyList<LocationLink>?>(null)));
        var (_, view, _) = await host.OpenAsync("symbol", "a.cs");
        view.Controller.SetSelection(TextRange.At(0, 2));
        await Nav(view).GoToDefinitionAsync();
        Assert.Contains("No definition found for 'symbol'", host.Window.StatusMessages);
    }

    [AvaloniaFact]
    public async Task One_reference_navigates_directly_and_many_use_the_references_command_when_the_shell_has_it_else_a_quick_pick()
    {
        using var host = ViewHost.Create();
        var a = new Uri("file:///x/A.cs");
        var b = new Uri("file:///x/B.cs");
        IReadOnlyList<Location> locations = [new Location(a, TextRange.FromLines(1, 0, 1, 3)), new Location(b, TextRange.FromLines(2, 0, 2, 3))];
        var next = locations;
        host.Factory.Services.Features.Register<IReferenceProvider>(DocumentSelector.All, new FakeReferenceProvider((_, _) => Task.FromResult<IReadOnlyList<Location>?>(next)));
        var (_, view, _) = await host.OpenAsync("ref", "a.cs");

        next = [locations[0]];
        await Nav(view).FindReferencesAsync();
        Assert.Equal(a, host.Navigation.Opened.Single().Uri);

        next = locations;
        host.Window.QuickPickResponses.Enqueue(1);
        await Nav(view).FindReferencesAsync();
        Assert.Equal(b, host.Navigation.Opened.Last().Uri); // quick pick fallback (no shell command registered)

        object?[]? received = null;
        host.Commands.Register(NavigationFeature.ShowReferencesCommand, inv => { received = inv.Args.ToArray(); return ValueTask.FromResult<object?>(null); });
        var openedBefore = host.Navigation.Opened.Count;
        await Nav(view).FindReferencesAsync();
        Assert.NotNull(received);
        Assert.Equal(2, ((IReadOnlyList<Location>)received![0]!).Count);
        Assert.Equal(openedBefore, host.Navigation.Opened.Count); // the shell presents them; no quick pick, no navigation
    }

    [AvaloniaFact]
    public async Task Rename_asks_for_the_name_and_applies_the_workspace_edit()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync("int count = 1;\ncount++;\n", "a.cs");
        var provider = new FakeRenameProvider(
            (d, _, name) => Task.FromResult<WorkspaceEdit?>(new WorkspaceEdit(new Dictionary<Uri, IReadOnlyList<TextEdit>>
            {
                [d.Uri] = [new TextEdit(TextRange.FromLines(0, 4, 0, 9), name), new TextEdit(TextRange.FromLines(1, 0, 1, 5), name)],
            })),
            (_, _) => new PrepareRenameResult(TextRange.FromLines(0, 4, 0, 9), "count"));
        host.Factory.Services.Features.Register<IRenameProvider>(DocumentSelector.All, provider);
        host.Window.InputResponses.Enqueue("total");
        view.Controller.SetSelection(TextRange.At(0, 6));
        await Nav(view).RenameAsync();
        ViewHost.Pump();
        Assert.Equal("int total = 1;\ntotal++;\n", doc.GetText());
        Assert.Equal(["total"], provider.RequestedNames);
        doc.Undo();
        Assert.Equal("int count = 1;\ncount++;\n", doc.GetText()); // one undo unit for both occurrences
    }

    [AvaloniaFact]
    public async Task Cancelling_the_rename_prompt_or_keeping_the_name_changes_nothing()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync("int count = 1;\n", "a.cs");
        var provider = new FakeRenameProvider((d, _, name) => Task.FromResult<WorkspaceEdit?>(new WorkspaceEdit(new Dictionary<Uri, IReadOnlyList<TextEdit>> { [d.Uri] = [new TextEdit(TextRange.FromLines(0, 4, 0, 9), name)] })));
        host.Factory.Services.Features.Register<IRenameProvider>(DocumentSelector.All, provider);
        view.Controller.SetSelection(TextRange.At(0, 6));
        host.Window.InputResponses.Enqueue(null); // Escape
        await Nav(view).RenameAsync();
        host.Window.InputResponses.Enqueue("count"); // same name
        await Nav(view).RenameAsync();
        Assert.Equal("int count = 1;\n", doc.GetText());
        Assert.Empty(provider.RequestedNames);
    }

    [AvaloniaFact]
    public async Task A_rename_result_that_arrives_after_the_text_changed_is_discarded()
    {
        using var host = ViewHost.Create();
        var gate = new TaskCompletionSource();
        var (doc, view, _) = await host.OpenAsync("int count = 1;\n", "a.cs");
        host.Factory.Services.Features.Register<IRenameProvider>(DocumentSelector.All, new FakeRenameProvider(async (d, _, name) =>
        {
            await gate.Task.ConfigureAwait(false);
            return new WorkspaceEdit(new Dictionary<Uri, IReadOnlyList<TextEdit>> { [d.Uri] = [new TextEdit(TextRange.FromLines(0, 4, 0, 9), name)] });
        }));
        host.Window.InputResponses.Enqueue("total");
        view.Controller.SetSelection(TextRange.At(0, 6));
        var rename = Nav(view).RenameAsync();
        await Task.Delay(50);
        ViewHost.Pump();
        doc.ApplyEdits([TextEdit.Insert(TextPosition.Zero, "// edit\n")]);
        gate.SetResult();
        await rename;
        Assert.Equal("// edit\nint count = 1;\n", doc.GetText());
        Assert.Empty(host.Navigation.AppliedEdits);
        Assert.Contains(host.Window.StatusMessages, m => m.Contains("changed"));
    }

    [AvaloniaFact]
    public async Task Ctrl_click_underlines_the_symbol_and_navigates()
    {
        using var host = ViewHost.Create();
        var target = new Uri("file:///other/Target.cs");
        host.Factory.Services.Features.Register<IDefinitionProvider>(DocumentSelector.All, new FakeDefinitionProvider((_, _, _) => Task.FromResult<IReadOnlyList<LocationLink>?>([Link(target, 3, 0, 6)])));
        var (_, view, window) = await host.OpenAsync("Target t;", "a.cs");
        ViewHost.Pump();
        var point = PointOf(window, view, 2);
        window.MouseMove(point, RawInputModifiers.Control);
        await ViewHost.WaitFor(() => view.Editor.Cursor is not null, "link state while Ctrl is held");
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.Control);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.Control);
        await ViewHost.WaitFor(() => host.Navigation.Opened.Count == 1, "navigated");
        Assert.Equal(target, host.Navigation.Opened[0].Uri);
    }

    // ---- formatting ----

    private static IFormattingFeature Fmt(CodeEditorControl v) => v.Feature<IFormattingFeature>()!;

    [AvaloniaFact]
    public async Task Format_document_applies_the_edits_as_one_undo_unit_with_the_view_s_options()
    {
        using var host = ViewHost.Create();
        host.Config.Set("editor.tabSize", 2);
        var provider = new FakeFormattingProvider((d, _) => Task.FromResult<IReadOnlyList<TextEdit>?>(
        [
            new TextEdit(TextRange.FromLines(0, 0, 0, 0), "  "),
            new TextEdit(TextRange.FromLines(1, 0, 1, 0), "  "),
        ]));
        host.Factory.Services.Features.Register<IDocumentFormattingProvider>(DocumentSelector.All, provider);
        var (doc, view, _) = await host.OpenAsync("a\nb\n", "a.txt");
        await Fmt(view).FormatDocumentAsync();
        Assert.Equal("  a\n  b\n", doc.GetText());
        Assert.Equal(2, provider.Options.Single().TabSize);
        doc.Undo();
        Assert.Equal("a\nb\n", doc.GetText());
    }

    [AvaloniaFact]
    public async Task A_format_result_for_older_text_is_discarded()
    {
        using var host = ViewHost.Create();
        var gate = new TaskCompletionSource();
        host.Factory.Services.Features.Register<IDocumentFormattingProvider>(DocumentSelector.All, new FakeFormattingProvider(async (d, _) =>
        {
            await gate.Task.ConfigureAwait(false);
            return [new TextEdit(TextRange.FromLines(0, 0, 0, 1), "WRONG")];
        }));
        var (doc, view, _) = await host.OpenAsync("abc\n", "a.txt");
        var pending = Fmt(view).FormatDocumentAsync();
        doc.ApplyEdits([TextEdit.Insert(TextPosition.Zero, "x")]);
        gate.SetResult();
        await pending;
        Assert.Equal("xabc\n", doc.GetText());
    }

    [AvaloniaFact]
    public async Task Format_selection_uses_range_formatting_and_without_a_selection_the_document()
    {
        using var host = ViewHost.Create();
        TextRange? asked = null;
        host.Factory.Services.Features.Register<IDocumentFormattingProvider>(DocumentSelector.All, new FakeFormattingProvider(
            (d, _) => Task.FromResult<IReadOnlyList<TextEdit>?>([new TextEdit(TextRange.FromLines(0, 0, 0, 0), "DOC:")]),
            (d, r) =>
            {
                asked = r;
                return Task.FromResult<IReadOnlyList<TextEdit>?>([new TextEdit(TextRange.FromLines(1, 0, 1, 0), "SEL:")]);
            }));
        var (doc, view, _) = await host.OpenAsync("one\ntwo\n", "a.txt");
        view.Controller.SetSelection(TextRange.FromLines(1, 0, 1, 3));
        await Fmt(view).FormatSelectionAsync();
        Assert.Equal(TextRange.FromLines(1, 0, 1, 3), asked);
        Assert.Equal("one\nSEL:two\n", doc.GetText());
        view.Controller.SetSelection(TextRange.At(0, 0));
        await Fmt(view).FormatSelectionAsync();
        Assert.StartsWith("DOC:", doc.GetText());
    }

    [AvaloniaFact]
    public async Task On_type_formatting_runs_only_for_trigger_characters_and_only_when_enabled()
    {
        using var host = ViewHost.Create();
        var calls = 0;
        host.Factory.Services.Features.Register<IDocumentFormattingProvider>(DocumentSelector.All, new FakeFormattingProvider(
            (_, _) => Task.FromResult<IReadOnlyList<TextEdit>?>(null),
            null,
            (d, pos, ch) =>
            {
                calls++;
                return Task.FromResult<IReadOnlyList<TextEdit>?>([new TextEdit(TextRange.FromLines(0, 0, 0, 0), ">>")]);
            },
            [";"]));
        var (doc, view, _) = await host.OpenAsync("", "a.txt");
        Type(view, "a;");
        ViewHost.Pump();
        await Task.Delay(100);
        ViewHost.Pump();
        Assert.Equal(0, calls); // editor.formatOnType is off by default

        host.Config.Set("editor.formatOnType", true);
        ViewHost.Pump();
        Type(view, "b");
        Type(view, ";");
        await ViewHost.WaitFor(() => doc.GetText().StartsWith(">>"), "on-type edit applied");
        Assert.Equal(1, calls);
    }

    [AvaloniaFact]
    public async Task Format_on_save_formats_before_writing_the_file()
    {
        using var host = ViewHost.Create();
        host.Config.Set("editor.formatOnSave", true);
        host.Factory.Services.Features.Register<IDocumentFormattingProvider>(DocumentSelector.All, new FakeFormattingProvider((d, _) => Task.FromResult<IReadOnlyList<TextEdit>?>(
            [new TextEdit(TextRange.FromLines(0, 0, 0, 3), "formatted")])));
        var (doc, _, _) = await host.OpenAsync("raw\n", "a.txt");
        doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 3), "!")]); // dirty, so the save has something to write
        var save = doc.SaveAsync();
        await ViewHost.WaitFor(() => save.IsCompleted, "save finished");
        await save;
        Assert.Equal("formatted!\n", await File.ReadAllTextAsync(doc.Uri.LocalPath));
        Assert.Equal("formatted!\n", doc.GetText());
    }
}
