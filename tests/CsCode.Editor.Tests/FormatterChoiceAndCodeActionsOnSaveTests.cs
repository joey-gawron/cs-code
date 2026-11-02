using System.Text.Json;
using Avalonia.Headless.XUnit;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Editor.Features;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.View;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>Language stage 1: <c>editor.defaultFormatter</c> (+ Format Document With…) and <c>editor.codeActionsOnSave</c>.</summary>
public class FormatterChoiceAndCodeActionsOnSaveTests
{
    /// <summary>A formatter that says which language server it comes from, as the LSP module's providers do.</summary>
    private sealed class ServerFormatter(string id, string replacement) : IDocumentFormattingProvider, ILanguageProviderSource
    {
        public string SourceId => id;
        public string SourceName => id.ToUpperInvariant();
        public int Calls;

        public ValueTask<IReadOnlyList<TextEdit>?> FormatDocumentAsync(ITextDocument document, FormattingOptions options, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return ValueTask.FromResult<IReadOnlyList<TextEdit>?>([new TextEdit(new TextRange(TextPosition.Zero, document.PositionAt(document.Length)), replacement)]);
        }
    }

    private static IFormattingFeature Fmt(CodeEditorControl v) => v.Feature<IFormattingFeature>()!;

    private static void ForPython(ViewHost host, string key, object value) => host.Config.Set("[python]", new Dictionary<string, object> { [key] = value });

    // ---- editor.defaultFormatter ----

    [Fact]
    public void Select_matches_the_server_id_or_a_VS_Code_extension_id_ending_in_it()
    {
        var pyright = new ServerFormatter("pyright", "");
        var ruff = new ServerFormatter("ruff", "");
        Assert.Equal([ruff], FormatterChoice.Select([pyright, ruff], "ruff").Providers);
        Assert.Equal([ruff], FormatterChoice.Select([pyright, ruff], "charliermarsh.ruff").Providers);
        Assert.Equal([ruff], FormatterChoice.Select([pyright, ruff], "RUFF").Providers);
        Assert.Equal([pyright, ruff], FormatterChoice.Select([pyright, ruff], null).Providers); // unset: every formatter, as before
        var (none, missing) = FormatterChoice.Select([pyright, ruff], "black");
        Assert.Empty(none);
        Assert.Equal("black", missing);
    }

    [Fact]
    public void Select_falls_back_to_unidentified_providers_when_the_choice_is_not_among_the_identified_ones()
    {
        // a remote workspace's bridged providers cannot say which server they are: they keep formatting as before
        var bridged = new FakeFormattingProvider((_, _) => Task.FromResult<IReadOnlyList<TextEdit>?>(null));
        var (providers, missing) = FormatterChoice.Select([new ServerFormatter("pyright", ""), bridged], "ruff");
        Assert.Equal([bridged], providers);
        Assert.Null(missing);
    }

    [AvaloniaFact]
    public async Task Format_document_uses_only_the_language_s_default_formatter()
    {
        using var host = ViewHost.Create();
        var pyright = new ServerFormatter("pyright", "by pyright\n");
        var ruff = new ServerFormatter("ruff", "by ruff\n");
        // the registry hands out the newest registration first: pyright, then ruff
        host.Factory.Services.Features.Register<IDocumentFormattingProvider>(DocumentSelector.All, ruff);
        host.Factory.Services.Features.Register<IDocumentFormattingProvider>(DocumentSelector.All, pyright);
        var (doc, view, _) = await host.OpenAsync("x = 1\n", "a.py");
        Assert.Equal("python", doc.LanguageId);

        await Fmt(view).FormatDocumentAsync();
        Assert.Equal("by pyright\n", doc.GetText()); // unset: the first formatter that answers

        ForPython(host, "editor.defaultFormatter", "ruff");
        await Fmt(view).FormatDocumentAsync();
        Assert.Equal("by ruff\n", doc.GetText());
        Assert.Equal(1, pyright.Calls); // never asked once ruff was chosen
    }

    [AvaloniaFact]
    public async Task A_default_formatter_that_is_not_running_formats_nothing_and_says_so()
    {
        using var host = ViewHost.Create();
        host.Factory.Services.Features.Register<IDocumentFormattingProvider>(DocumentSelector.All, new ServerFormatter("pyright", "changed\n"));
        host.Config.Set("editor.defaultFormatter", "ruff");
        var (doc, view, _) = await host.OpenAsync("x = 1\n", "a.py");
        await Fmt(view).FormatDocumentAsync();
        Assert.Equal("x = 1\n", doc.GetText());
        Assert.Contains(host.Window.StatusMessages, m => m.Contains("'ruff'", StringComparison.Ordinal) && m.Contains("editor.defaultFormatter", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Format_on_save_uses_the_default_formatter()
    {
        using var host = ViewHost.Create();
        host.Config.Set("editor.formatOnSave", true);
        ForPython(host, "editor.defaultFormatter", "ruff");
        host.Factory.Services.Features.Register<IDocumentFormattingProvider>(DocumentSelector.All, new ServerFormatter("pyright", "by pyright\n"));
        host.Factory.Services.Features.Register<IDocumentFormattingProvider>(DocumentSelector.All, new ServerFormatter("ruff", "by ruff\n"));
        var (doc, _, _) = await host.OpenAsync("x = 1\n", "a.py");
        doc.ApplyEdits([TextEdit.Insert(TextPosition.Zero, "#")]);
        var save = doc.SaveAsync();
        await ViewHost.WaitFor(() => save.IsCompleted, "save finished");
        await save;
        Assert.Equal("by ruff\n", await File.ReadAllTextAsync(doc.Uri.LocalPath));
    }

    [AvaloniaFact]
    public async Task Format_on_save_without_a_default_formatter_uses_the_first_that_answers_with_edits()
    {
        using var host = ViewHost.Create();
        host.Config.Set("editor.formatOnSave", true);
        // basedpyright registers formatting and answers nothing; the registry hands it out first (the newest registration)
        host.Factory.Services.Features.Register<IDocumentFormattingProvider>(DocumentSelector.All, new ServerFormatter("ruff", "by ruff\n"));
        host.Factory.Services.Features.Register<IDocumentFormattingProvider>(DocumentSelector.All,
            new FakeFormattingProvider((_, _) => Task.FromResult<IReadOnlyList<TextEdit>?>(null)));
        var (doc, _, _) = await host.OpenAsync("x = 1\n", "a.py");
        doc.ApplyEdits([TextEdit.Insert(TextPosition.Zero, "#")]);
        var save = doc.SaveAsync();
        await ViewHost.WaitFor(() => save.IsCompleted, "save finished");
        await save;
        Assert.Equal("by ruff\n", await File.ReadAllTextAsync(doc.Uri.LocalPath));
    }

    [AvaloniaFact]
    public async Task Format_document_with_offers_every_formatter_marks_the_default_and_runs_the_picked_one_once()
    {
        using var host = ViewHost.Create();
        ForPython(host, "editor.defaultFormatter", "ruff");
        var pyright = new ServerFormatter("pyright", "by pyright\n");
        host.Factory.Services.Features.Register<IDocumentFormattingProvider>(DocumentSelector.All, new ServerFormatter("ruff", "by ruff\n"));
        host.Factory.Services.Features.Register<IDocumentFormattingProvider>(DocumentSelector.All, pyright); // newest first: PYRIGHT, RUFF
        var (doc, view, _) = await host.OpenAsync("x = 1\n", "a.py");
        host.Window.QuickPickResponses.Enqueue(0); // PYRIGHT
        await Fmt(view).FormatDocumentWithAsync();
        Assert.Equal("by pyright\n", doc.GetText());
        var items = FormattingFeature.PickItems(host.Factory.Services.Features.GetProviders<IDocumentFormattingProvider>(doc), "ruff");
        Assert.Equal(["PYRIGHT", "RUFF"], items.Select(i => i.Label));
        Assert.Equal("default", items[1].Description);
        Assert.Null(items[0].Description);
        Assert.Equal("ruff", host.Config.Get<Dictionary<string, string>>("[python]")!["editor.defaultFormatter"]); // a one-off: the setting is unchanged
    }

    // ---- editor.codeActionsOnSave: the setting ----

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void The_object_form_runs_fix_all_first_and_honours_explicit_always_never_and_the_legacy_booleans()
    {
        var setting = Json("""{"source.organizeImports": "explicit", "source.fixAll": true, "source.addMissingImports": "always", "source.sortMembers": "never", "source.x": false}""");
        Assert.Equal(["source.fixAll", "source.organizeImports", "source.addMissingImports"], CodeActionsOnSaveParticipant.KindsToRun(setting, DocumentSaveReason.Manual));
        // auto-save runs only "always"
        Assert.Equal(["source.addMissingImports"], CodeActionsOnSaveParticipant.KindsToRun(setting, DocumentSaveReason.AfterDelay));
        Assert.Equal(["source.addMissingImports"], CodeActionsOnSaveParticipant.KindsToRun(setting, DocumentSaveReason.FocusOut));
    }

    [Fact]
    public void The_array_form_runs_each_kind_explicitly_in_the_listed_order()
    {
        var setting = Json("""["source.organizeImports", "source.fixAll", "source.organizeImports", 3]""");
        Assert.Equal(["source.organizeImports", "source.fixAll"], CodeActionsOnSaveParticipant.KindsToRun(setting, DocumentSaveReason.Manual));
        Assert.Empty(CodeActionsOnSaveParticipant.KindsToRun(setting, DocumentSaveReason.AfterDelay));
        Assert.Empty(CodeActionsOnSaveParticipant.KindsToRun(Json("\"source.fixAll\""), DocumentSaveReason.Manual)); // not a shape VS Code knows
        Assert.Empty(CodeActionsOnSaveParticipant.KindsToRun(null, DocumentSaveReason.Manual));
    }

    // ---- editor.codeActionsOnSave: running it ----

    private static CodeAction Action(string title, string kind, ITextDocument doc, Func<string, string> rewrite) =>
        new(title, kind, Edit: new WorkspaceEdit(new Dictionary<Uri, IReadOnlyList<TextEdit>>
        {
            [doc.Uri] = [new TextEdit(new TextRange(TextPosition.Zero, doc.PositionAt(doc.Length)), rewrite(doc.GetText()))],
        }));

    private static CodeActionsOnSaveParticipant Participant(ViewHost host) => host.Provider.GetRequiredService<CodeActionsOnSaveParticipant>();

    [AvaloniaFact]
    public async Task Fix_all_then_organize_imports_each_see_the_text_the_last_one_left_and_undo_as_one_unit()
    {
        using var host = ViewHost.Create();
        host.Config.Set("editor.codeActionsOnSave", new Dictionary<string, object> { ["source.organizeImports"] = "explicit", ["source.fixAll"] = "explicit" });
        var asked = new List<string>();
        var provider = new FakeCodeActionProvider((doc, range, ctx, _) =>
        {
            var kind = ctx.Only!.Single();
            lock (asked) asked.Add(kind);
            Assert.Equal(CodeActionTriggerKind.Automatic, ctx.TriggerKind);
            Assert.Equal(doc.PositionAt(doc.Length), range.End); // the whole document
            IReadOnlyList<CodeAction> actions = kind switch
            {
                // kinds match hierarchically: source.fixAll runs Ruff's source.fixAll.ruff
                "source.fixAll" => [Action("Ruff: Fix all", "source.fixAll.ruff", doc, t => t.Replace("unused", "fixed", StringComparison.Ordinal))],
                "source.organizeImports" => [Action("Ruff: Organize imports", "source.organizeImports.ruff", doc, t => "import a\n" + t.Replace("import a\n", "", StringComparison.Ordinal))],
                _ => [],
            };
            return Task.FromResult<IReadOnlyList<CodeAction>?>(actions);
        });
        host.Factory.Services.Features.Register<ICodeActionProvider>(DocumentSelector.All, provider);
        var (doc, _, _) = await host.OpenAsync("x = unused\nimport a\n", "a.py");
        doc.ApplyEdits([TextEdit.Insert(TextPosition.Zero, "#")]);

        var edits = await Participant(host).OnWillSaveAsync(doc, DocumentSaveReason.Manual, CancellationToken.None);
        Assert.Null(edits); // applied by the participant itself
        Assert.Equal(["source.fixAll", "source.organizeImports"], asked);
        Assert.Equal("import a\n#x = fixed\n", doc.GetText());
        doc.Undo();
        Assert.Equal("#x = unused\nimport a\n", doc.GetText()); // both actions were one undo unit; the typing before stays
    }

    [AvaloniaFact]
    public async Task Explicit_actions_do_not_run_on_auto_save_but_always_ones_do()
    {
        using var host = ViewHost.Create();
        ForPython(host, "editor.codeActionsOnSave", new Dictionary<string, object> { ["source.fixAll"] = "explicit", ["source.organizeImports"] = "always" });
        host.Factory.Services.Features.Register<ICodeActionProvider>(DocumentSelector.All, new FakeCodeActionProvider((doc, _, ctx, _) =>
            Task.FromResult<IReadOnlyList<CodeAction>?>([Action(ctx.Only!.Single(), ctx.Only!.Single(), doc, t => t + ctx.Only!.Single() + "\n")])));
        var (doc, _, _) = await host.OpenAsync("", "a.py");
        await Participant(host).OnWillSaveAsync(doc, DocumentSaveReason.AfterDelay, CancellationToken.None);
        Assert.Equal("source.organizeImports\n", doc.GetText());
        var (other, _, _) = await host.OpenAsync("", "b.txt"); // the setting is Python's only
        await Participant(host).OnWillSaveAsync(other, DocumentSaveReason.Manual, CancellationToken.None);
        Assert.Equal("", other.GetText());
    }

    [AvaloniaFact]
    public async Task Deferred_edits_are_resolved_and_command_only_actions_are_executed()
    {
        using var host = ViewHost.Create();
        host.Config.Set("editor.codeActionsOnSave", new[] { "source.fixAll", "source.organizeImports" });
        var executed = new List<string>();
        using var _ = host.Commands.Register("pyright.organizeimports", inv =>
        {
            executed.Add(((JsonElement)inv.Args[0]!).GetString()!);
            return ValueTask.FromResult<object?>(null);
        });
        var provider = new FakeCodeActionProvider((doc, _, ctx, _) => Task.FromResult<IReadOnlyList<CodeAction>?>(ctx.Only!.Single() switch
        {
            "source.fixAll" => [new CodeAction("Fix all", "source.fixAll.ruff", Data: Json("{\"id\":1}"))], // edit deferred to codeAction/resolve
            _ => [new CodeAction("Organize Imports", "source.organizeImports", Command: new Command("Organize Imports", "pyright.organizeimports", [Json("\"file:///a.py\"")]))],
        }));
        host.Factory.Services.Features.Register<ICodeActionProvider>(DocumentSelector.All, provider);
        var (doc, _, _) = await host.OpenAsync("bad\n", "a.py");
        provider.Resolve = a => Action(a.Title, a.Kind!, doc, _ => "good\n");
        await Participant(host).OnWillSaveAsync(doc, DocumentSaveReason.Manual, CancellationToken.None);
        Assert.Equal("good\n", doc.GetText());
        Assert.Equal(["file:///a.py"], executed);
    }

    [AvaloniaFact]
    public async Task A_more_specific_listed_kind_is_left_to_its_own_entry_and_disabled_actions_never_run()
    {
        using var host = ViewHost.Create();
        host.Config.Set("editor.codeActionsOnSave", new[] { "source.fixAll", "source.fixAll.eslint" });
        var runs = new List<string>();
        host.Factory.Services.Features.Register<ICodeActionProvider>(DocumentSelector.All, new FakeCodeActionProvider((doc, _, ctx, _) =>
            Task.FromResult<IReadOnlyList<CodeAction>?>(
            [
                Action("eslint", "source.fixAll.eslint", doc, t => { lock (runs) runs.Add(ctx.Only!.Single() + ":eslint"); return t + "e"; }),
                new CodeAction("off", "source.fixAll.other", DisabledReason: "not now", Edit: WorkspaceEdit.Empty),
            ])));
        var (doc, _, _) = await host.OpenAsync("", "a.js");
        await Participant(host).OnWillSaveAsync(doc, DocumentSaveReason.Manual, CancellationToken.None);
        Assert.Equal("e", doc.GetText()); // once, under its own entry
    }

    [AvaloniaFact]
    public async Task A_hung_provider_costs_at_most_the_timeout_and_what_was_applied_before_stays()
    {
        using var host = ViewHost.Create();
        host.Config.Set("editor.codeActionsOnSave", new[] { "source.fixAll", "source.organizeImports" });
        host.Config.Set("editor.codeActionsOnSaveTimeout", 300);
        host.Factory.Services.Features.Register<ICodeActionProvider>(DocumentSelector.All, new FakeCodeActionProvider(async (doc, _, ctx, ct) =>
        {
            if (ctx.Only!.Single() == "source.organizeImports") await Task.Delay(Timeout.Infinite, ct); // hangs until cancelled
            return [Action("fix", "source.fixAll", doc, t => "fixed\n")];
        }));
        var (doc, _, _) = await host.OpenAsync("raw\n", "a.py");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var run = Participant(host).OnWillSaveAsync(doc, DocumentSaveReason.Manual, CancellationToken.None).AsTask();
        await ViewHost.WaitFor(() => run.IsCompleted, "participant finished", 5_000);
        await run;
        Assert.InRange(clock.ElapsedMilliseconds, 250, 3_000);
        Assert.Equal("fixed\n", doc.GetText());
    }

    [AvaloniaFact]
    public async Task A_failing_provider_is_skipped_and_the_next_one_still_runs()
    {
        using var host = ViewHost.Create();
        host.Config.Set("editor.codeActionsOnSave", new Dictionary<string, object> { ["source.fixAll"] = true });
        host.Factory.Services.Features.Register<ICodeActionProvider>(DocumentSelector.All, new FakeCodeActionProvider((_, _, _, _) => throw new InvalidOperationException("server crashed")));
        host.Factory.Services.Features.Register<ICodeActionProvider>(DocumentSelector.All, new FakeCodeActionProvider((doc, _, _, _) =>
            Task.FromResult<IReadOnlyList<CodeAction>?>([Action("fix", "source.fixAll", doc, _ => "ok\n")])));
        var (doc, _, _) = await host.OpenAsync("raw\n", "a.py");
        await Participant(host).OnWillSaveAsync(doc, DocumentSaveReason.Manual, CancellationToken.None);
        Assert.Equal("ok\n", doc.GetText());
    }

    [Fact]
    public void AddCodeActionsOnSave_makes_it_the_first_will_save_participant()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWillSaveParticipant>(new NoParticipant());
        services.AddCsCodeEditor();
        services.AddCodeActionsOnSave();
        var descriptors = services.Where(d => d.ServiceType == typeof(IWillSaveParticipant)).ToList();
        Assert.Equal(2, descriptors.Count);
        Assert.NotNull(descriptors[0].ImplementationFactory);
    }

    private sealed class NoParticipant : IWillSaveParticipant
    {
        public ValueTask<IReadOnlyList<TextEdit>?> OnWillSaveAsync(ITextDocument document, DocumentSaveReason reason, CancellationToken ct) => default;
    }
}
