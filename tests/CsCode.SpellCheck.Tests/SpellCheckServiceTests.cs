using System.Diagnostics;
using System.Text.Json;
using CsCode.Core.Diagnostics;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.SpellCheck.Engine;
using CsCode.SpellCheck.Settings;
using CsCode.TestDoubles;

namespace CsCode.SpellCheck.Tests;

public class SpellCheckServiceTests
{
    private const string Code = "var count = Compute(\"a wierd string\"); // we recieve it\nint misspeled = 0;\n";

    [Fact]
    public async Task Misspelt_words_in_comments_and_strings_become_cSpell_diagnostics()
    {
        using var h = new SpellHarness();
        var doc = h.Open(Code);
        await h.ResultAsync(doc, r => r.Issues.Count == 2);
        var diagnostics = h.DiagnosticsOf(doc).OrderBy(d => d.Range.Start).ToArray();
        Assert.Equal(2, diagnostics.Length);
        Assert.Equal("\"wierd\": Unknown word.", diagnostics[0].Message);
        Assert.Equal(new TextRange(new TextPosition(0, 23), new TextPosition(0, 28)), diagnostics[0].Range);
        Assert.Equal("\"recieve\": Unknown word.", diagnostics[1].Message);
        Assert.All(diagnostics, d =>
        {
            Assert.Equal("cSpell", d.Source);
            Assert.Equal(DiagnosticSeverity.Information, d.Severity);
        });
        Assert.Equal(2, h.Diagnostics.GetAll().Count); // what the Error List lists
    }

    [Fact]
    public async Task The_diagnostic_level_and_the_words_settings_apply_live()
    {
        using var h = new SpellHarness();
        var doc = h.Open(Code);
        await h.ResultAsync(doc, r => r.Issues.Count == 2);
        h.Config.Set(SpellSettingsLoader.DiagnosticLevel, "Warning");
        await WaitAsync(() => h.DiagnosticsOf(doc) is { Count: 2 } d && d.All(x => x.Severity == DiagnosticSeverity.Warning));
        h.Config.Set(SpellSettingsLoader.Words, new[] { "Wierd" });
        await h.ResultAsync(doc, r => r.Issues.Select(i => i.Word).SequenceEqual(["recieve"]));
        h.Config.Set(SpellSettingsLoader.IgnoreWords, new[] { "recieve" });
        await h.ResultAsync(doc, r => r.Issues.Count == 0);
        Assert.Empty(h.DiagnosticsOf(doc));
    }

    [Fact]
    public async Task Edits_are_checked_again_after_the_delay()
    {
        using var h = new SpellHarness();
        var doc = h.Open("// fine\n");
        await h.ResultAsync(doc, r => r.State == SpellDocumentState.Checked);
        Assert.Empty(h.DiagnosticsOf(doc));
        h.Edit(doc, TextEdit.Insert(new TextPosition(0, 7), " definately"));
        await h.ResultAsync(doc, r => r.Issues.Count == 1);
        Assert.Equal("\"definately\": Unknown word.", Assert.Single(h.DiagnosticsOf(doc)).Message);
    }

    [Fact]
    public async Task Disabled_languages_ignored_states_and_large_files_publish_nothing()
    {
        using var h = new SpellHarness(c => c.Set(SpellSettingsLoader.EnabledFileTypes, new Dictionary<string, bool> { ["csharp"] = false }));
        var cs = h.Open(Code);
        var md = h.Open("Thsi is wierd.", "markdown", "a.md", classify: false);
        Assert.Equal(SpellDocumentState.LanguageNotEnabled, (await h.ResultAsync(cs)).State);
        Assert.Equal(["Thsi", "wierd"], (await h.ResultAsync(md, r => r.Issues.Count == 2)).Issues.Select(i => i.Word));
        Assert.Empty(h.DiagnosticsOf(cs));

        h.Config.Set(SettingKeys.EditorLargeFileOptimizationsLimit, 5);
        Assert.Equal(SpellDocumentState.LargeFile, (await h.ResultAsync(md, r => r.State == SpellDocumentState.LargeFile)).State);
        Assert.Empty(h.DiagnosticsOf(md));

        h.Config.Set(SettingKeys.EditorLargeFileOptimizationsLimit, 5_000_000);
        h.Config.Set(SpellSettingsLoader.Enabled, false);
        Assert.Equal(SpellDocumentState.Disabled, (await h.ResultAsync(md, r => r.State == SpellDocumentState.Disabled)).State);
        Assert.Empty(h.Diagnostics.GetAll());
    }

    [Fact]
    public async Task Code_the_editor_has_no_tokens_for_is_not_checked_until_tokens_arrive()
    {
        using var h = new SpellHarness();
        var doc = h.Open(Code, classify: false);
        Assert.Equal(SpellDocumentState.NoTokens, (await h.ResultAsync(doc)).State);
        h.Classifier.Set(doc.Uri, FakeClassification.SlashComments(Code)); // the view showed it: tokens, and the editor says so
        Assert.Equal(2, (await h.ResultAsync(doc, r => r.Issues.Count == 2)).Issues.Count);
    }

    [Fact]
    public void Ignore_paths_match_like_cspell()
    {
        Assert.True(SpellCheckService.PathMatches("node_modules", "web/node_modules/lib/x.js"));
        Assert.True(SpellCheckService.PathMatches("package-lock.json", "web/package-lock.json"));
        Assert.True(SpellCheckService.PathMatches(".git/objects", ".git/objects/ab/cdef"));
        Assert.True(SpellCheckService.PathMatches("**/*.min.js", "dist/app.min.js"));
        Assert.True(SpellCheckService.PathMatches("/generated/", "generated/Api.cs"));
        Assert.False(SpellCheckService.PathMatches("node_modules", "src/modules/x.js"));
        Assert.False(SpellCheckService.PathMatches(".git/objects", ".git/COMMIT_EDITMSG"));
    }

    /// <summary>A classifier whose runs are handed out only when the test says so: a check stopped half-way at will.</summary>
    private sealed class GatedClassifier : ISyntaxClassificationService
    {
        public readonly ManualResetEventSlim Gate = new(true);
        public readonly ManualResetEventSlim Entered = new(false);
        public string? Thread;
        private readonly Emitter<SyntaxClassificationChange> _changed = new();
        public IEventSource<SyntaxClassificationChange> Changed => _changed;
        public Func<string>? Text;

        public SyntaxClassification Capture(Uri uri)
        {
            Thread = System.Threading.Thread.CurrentThread.Name;
            return new Gated(this, FakeClassification.SlashComments(Text!()));
        }

        private sealed class Gated(GatedClassifier owner, SyntaxClassification inner) : SyntaxClassification
        {
            public override SyntaxClassificationState State => inner.State;
            public override int LineCount => inner.LineCount;

            public override bool TryGetRuns(int line, List<SyntaxTextRun> runs)
            {
                owner.Entered.Set();
                owner.Gate.Wait(TimeSpan.FromSeconds(10));
                return inner.TryGetRuns(line, runs);
            }
        }
    }

    /// <summary>Records every publication with the text the document had when it was published.</summary>
    private sealed class RecordingDiagnostics(ITextDocument? doc = null) : IDiagnosticService
    {
        private readonly DiagnosticService _inner = new();
        public readonly List<(int Version, int DocVersion, IReadOnlyList<Diagnostic> Set)> Published = [];
        public ITextDocument? Document { get; set; } = doc;

        public void Set(Uri uri, string owner, IReadOnlyList<Diagnostic> diagnostics, int? documentVersion = null)
        {
            lock (Published) Published.Add((documentVersion ?? -1, Document?.Version ?? -1, diagnostics));
            _inner.Set(uri, owner, diagnostics, documentVersion);
        }

        public void Clear(string owner) => _inner.Clear(owner);
        public void Clear(Uri uri, string owner) => _inner.Clear(uri, owner);
        public IReadOnlyList<Diagnostic> Get(Uri uri) => _inner.Get(uri);
        public IReadOnlyList<(Uri Uri, Diagnostic Diagnostic)> GetAll() => _inner.GetAll();
        public (int Errors, int Warnings, int Infos, int Hints) Counts => _inner.Counts;
        public IEventSource<IReadOnlyCollection<Uri>> Changed => _inner.Changed;
    }

    [Fact]
    public async Task A_check_of_older_text_is_dropped_and_the_newer_one_published()
    {
        var classifier = new GatedClassifier();
        var diagnostics = new RecordingDiagnostics();
        var documents = new DocumentManager();
        var config = new TestConfigurationService();
        config.Set("cscode.spellCheck.delay", 0);
        using var service = new SpellCheckService(documents, diagnostics, config, classifier: classifier) { SliceBudget = TimeSpan.Zero };
        using var started = service.Start();
        var text = string.Join("\n", Enumerable.Range(0, 50).Select(i => $"// line {i} wierd"));
        var doc = new InMemoryTextDocument(new Uri("file:///work/stale.cs"), "csharp", text);
        diagnostics.Document = doc;
        classifier.Text = doc.GetText;
        await service.SettingsReady;
        classifier.Gate.Reset();
        using var registration = documents.Register(doc);

        // the first check is now stopped inside its first line: the user types meanwhile
        Assert.True(classifier.Entered.Wait(TimeSpan.FromSeconds(10)));
        doc.ApplyEdits([new TextEdit(new TextRange(new TextPosition(0, 0), new TextPosition(49, 100)), "// only one wierd line\n// and a definately")]);
        classifier.Gate.Set();

        await WaitAsync(() => service.ResultFor(doc.Uri) is { } r && r.Version == doc.Version);
        Assert.Equal(["wierd", "definately"], service.ResultFor(doc.Uri)!.Issues.Select(i => i.Word));
        lock (diagnostics.Published)
        {
            Assert.NotEmpty(diagnostics.Published);
            // nothing computed for the 50-line text reached the diagnostics: every set was published for the version the document had then
            Assert.All(diagnostics.Published, p => Assert.Equal(p.DocVersion, p.Version));
            Assert.DoesNotContain(diagnostics.Published, p => p.Set.Count > 2);
        }
        Assert.Equal(SpellLane.ThreadName, classifier.Thread);
    }

    [Fact]
    public async Task Typing_does_not_wait_for_the_checker()
    {
        var classifier = new GatedClassifier();
        var documents = new DocumentManager();
        var config = new TestConfigurationService();
        config.Set("cscode.spellCheck.delay", 0);
        using var service = new SpellCheckService(documents, new DiagnosticService(), config, classifier: classifier);
        using var started = service.Start();
        var text = string.Join("\n", Enumerable.Range(0, 20_000).Select(i => $"// line {i} has a wierd word in it"));
        var doc = new InMemoryTextDocument(new Uri("file:///work/big.cs"), "csharp", text);
        classifier.Text = doc.GetText;
        await service.SettingsReady;
        classifier.Gate.Reset(); // the lane is stuck in a check: nothing may wait for it
        using var registration = documents.Register(doc);
        Assert.True(classifier.Entered.Wait(TimeSpan.FromSeconds(10)));

        var times = new List<double>();
        for (var i = 0; i < 200; i++)
        {
            var sw = Stopwatch.StartNew();
            doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 2), "x")]); // the Changed handlers run synchronously, here
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        classifier.Gate.Set();
        times.Sort();
        // an InMemoryTextDocument edit of a 700 KB text costs a copy of it; the checker's share is a timer change
        Assert.True(times[^1] < 50, $"slowest keystroke {times[^1]:F2} ms");
        await WaitAsync(() => service.ResultFor(doc.Uri) is { } r && r.Version == doc.Version, 30_000);
        Assert.Equal(SpellLane.ThreadName, classifier.Thread);
    }

    [Fact]
    public async Task A_closed_document_and_its_state_are_not_retained()
    {
        using var h = new SpellHarness();
        await LeakAssert.NotRetainedAsync(async () =>
        {
            var doc = new InMemoryTextDocument(new Uri("file:///work/leak.cs"), "csharp", Code);
            h.Classifier.Set(doc.Uri, FakeClassification.SlashComments(Code), notify: false);
            var registration = h.Documents.Register(doc);
            await h.ResultAsync(doc, r => r.Issues.Count == 2);
            var state = h.Service.StateFor(doc.Uri)!;
            var result = h.Service.ResultFor(doc.Uri)!;
            registration.Dispose();
            h.Classifier.Set(doc.Uri, SyntaxClassification.Empty(SyntaxClassificationState.Unavailable), notify: false);
            Assert.False(h.Service.IsTracked(doc.Uri));
            Assert.Empty(h.DiagnosticsOf(doc));
            return [doc, state, result];
        });
    }

    [Fact]
    public async Task A_disposed_checker_is_not_retained()
    {
        var documents = new DocumentManager();
        var diagnostics = new DiagnosticService();
        var config = new TestConfigurationService();
        config.Set("cscode.spellCheck.delay", 0);
        var doc = new InMemoryTextDocument(new Uri("file:///work/x.md"), "markdown", "Thsi text");
        using var registration = documents.Register(doc);
        await LeakAssert.NotRetainedAsync(async () =>
        {
            var service = new SpellCheckService(documents, diagnostics, config);
            var started = service.Start();
            await WaitAsync(() => service.ResultFor(doc.Uri) is { Issues.Count: 1 });
            started.Dispose();
            return [service];
        });
        Assert.Empty(diagnostics.GetAll());
    }

    [Fact]
    public async Task The_dictionaries_load_lazily_on_the_lane_and_an_aggressive_trim_releases_them()
    {
        using var h = new SpellHarness();
        Assert.Equal(0, h.Service.EstimatedBytes); // nothing loaded before the first check
        var doc = h.Open(Code);
        await h.ResultAsync(doc, r => r.Issues.Count == 2);
        Assert.InRange(h.Service.EstimatedBytes, 5_000_000, 30_000_000);
        Assert.NotNull(h.Service.LastDictionaryLoadMs);
        Assert.True(h.Service.Trim(MemoryTrimLevel.Aggressive) > 5_000_000);
        await WaitAsync(() => h.Service.EstimatedBytes == 0);
        h.Edit(doc, TextEdit.Insert(new TextPosition(1, 0), "// occured\n"));
        await h.ResultAsync(doc, r => r.Issues.Count == 3); // loaded again on demand
    }

    // ---- quick fixes and commands ----

    private static async Task<IReadOnlyList<CodeAction>> ActionsAsync(SpellHarness h, ITextDocument doc, TextPosition at)
    {
        var provider = h.Features.GetProviders<ICodeActionProvider>(doc).Single();
        var diagnostics = h.DiagnosticsOf(doc).Where(d => d.Range.Start <= at && d.Range.End >= at).ToArray();
        return await provider.ProvideCodeActionsAsync(doc, new TextRange(at, at), new CodeActionContext(diagnostics), CancellationToken.None) ?? [];
    }

    [Fact]
    public async Task Quick_fixes_offer_suggestions_in_the_words_case_and_apply_them()
    {
        using var h = new SpellHarness();
        var doc = h.Open("// Recieve the data\n");
        await h.ResultAsync(doc, r => r.Issues.Count == 1);
        var actions = await ActionsAsync(h, doc, new TextPosition(0, 5));
        Assert.Equal("Change spelling to \"Receive\"", actions[0].Title);
        Assert.True(actions[0].IsPreferred);
        Assert.Equal("quickfix", actions[0].Kind);
        Assert.InRange(actions.Count(a => a.Edit is not null && a.Title.StartsWith("Change spelling", StringComparison.Ordinal)), 1, 5);
        Assert.Contains(actions, a => a.Title == "Add \"Recieve\" to user dictionary" && a.Command?.CommandId == SpellCheckHost.AddWordToUserDictionary);
        Assert.Contains(actions, a => a.Title == "Ignore \"Recieve\" in this file");
        Assert.DoesNotContain(actions, a => a.Title.Contains("workspace dictionary", StringComparison.Ordinal)); // no folder open

        await h.Navigation.ApplyWorkspaceEditAsync(actions[0].Edit!);
        Assert.Equal("// Receive the data\n", doc.GetText());
        h.Classifier.Set(doc.Uri, FakeClassification.SlashComments(doc.GetText()));
        await h.ResultAsync(doc, r => r.Issues.Count == 0);
        Assert.Empty(h.DiagnosticsOf(doc));
    }

    [Fact]
    public async Task Add_to_user_dictionary_writes_cSpell_words_and_clears_the_diagnostic_at_once()
    {
        using var h = new SpellHarness();
        var doc = h.Open("// a wierd line\n");
        var other = h.Open("// another wierd one\n", name: "b.cs");
        await h.ResultAsync(doc, r => r.Issues.Count == 1);
        await h.ResultAsync(other, r => r.Issues.Count == 1);
        var add = (await ActionsAsync(h, doc, new TextPosition(0, 6))).Single(a => a.Command?.CommandId == SpellCheckHost.AddWordToUserDictionary);
        await h.Commands.ExecuteAsync(add.Command!.CommandId, add.Command.Arguments!.Cast<object?>().ToArray());
        Assert.Equal(["wierd"], h.Config.Get<string[]>(SpellSettingsLoader.Words)!);
        // every open document is checked again with the word accepted
        await h.ResultAsync(doc, r => r.Issues.Count == 0);
        await h.ResultAsync(other, r => r.Issues.Count == 0);
        Assert.Empty(h.Diagnostics.GetAll());
    }

    [Fact]
    public async Task Ignore_in_this_file_writes_a_cspell_directive_that_the_checker_honours()
    {
        using var h = new SpellHarness();
        var doc = h.Open("#!/usr/bin/env thing\n// a wierd line and a definately\n", "csharp");
        await h.ResultAsync(doc, r => r.Issues.Count == 2);
        var ignore = (await ActionsAsync(h, doc, new TextPosition(1, 6))).Single(a => a.Title == "Ignore \"wierd\" in this file");
        await h.Navigation.ApplyWorkspaceEditAsync(ignore.Edit!);
        Assert.Equal("#!/usr/bin/env thing\n// cspell:ignore wierd\n// a wierd line and a definately\n", doc.GetText());
        h.Classifier.Set(doc.Uri, FakeClassification.SlashComments(doc.GetText()));
        await h.ResultAsync(doc, r => r.Issues.Select(i => i.Word).SequenceEqual(["definately"]));

        // a second word joins the same directive
        var second = (await ActionsAsync(h, doc, new TextPosition(2, 25))).Single(a => a.Title == "Ignore \"definately\" in this file");
        await h.Navigation.ApplyWorkspaceEditAsync(second.Edit!);
        Assert.StartsWith("#!/usr/bin/env thing\n// cspell:ignore wierd definately\n", doc.GetText());
        h.Classifier.Set(doc.Uri, FakeClassification.SlashComments(doc.GetText()));
        await h.ResultAsync(doc, r => r.Issues.Count == 0);

        // Markdown: an HTML comment; plain text has no comments: not offered
        var md = h.Open("Thsi line\n", "markdown", "a.md", classify: false);
        await h.ResultAsync(md, r => r.Issues.Count == 1);
        Assert.Equal("<!-- cspell:ignore Thsi -->\n", h.Host.CodeActions.IgnoreInFile(md, "Thsi")!.NewText);
        var txt = h.Open("Thsi line\n", "plaintext", "a.txt", classify: false);
        await h.ResultAsync(txt, r => r.Issues.Count == 1);
        Assert.DoesNotContain(await ActionsAsync(h, txt, new TextPosition(0, 1)), a => a.Title.StartsWith("Ignore", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Next_and_previous_spelling_issue_move_the_caret_and_wrap()
    {
        using var h = new SpellHarness();
        var doc = h.Open("// wierd one\nint x;\n// a definately\n// occured here\n");
        await h.ResultAsync(doc, r => r.Issues.Count == 3);
        h.Navigation.ActiveDocument = new EditorDocumentView(doc);
        h.Navigation.ActiveCaret = new TextPosition(1, 0);
        await h.Commands.ExecuteAsync(SpellCheckHost.GoToNextSpellingIssue);
        Assert.Equal(new TextRange(new TextPosition(2, 5), new TextPosition(2, 15)), h.Navigation.Opened[^1].Selection);
        h.Navigation.ActiveCaret = new TextPosition(2, 6);
        await h.Commands.ExecuteAsync(SpellCheckHost.GoToNextSpellingIssue, 2); // a count: two issues on, wrapping past the end
        Assert.Equal(new TextPosition(0, 3), h.Navigation.Opened[^1].Selection!.Value.Start);
        h.Navigation.ActiveCaret = new TextPosition(0, 0);
        await h.Commands.ExecuteAsync(SpellCheckHost.GoToPreviousSpellingIssue); // wraps to the last
        Assert.Equal(new TextPosition(3, 3), h.Navigation.Opened[^1].Selection!.Value.Start);
        h.Navigation.ActiveCaret = new TextPosition(2, 8); // inside "definately": [s goes to its start
        await h.Commands.ExecuteAsync(SpellCheckHost.GoToPreviousSpellingIssue);
        Assert.Equal(new TextPosition(2, 5), h.Navigation.Opened[^1].Selection!.Value.Start);
    }

    [Fact]
    public async Task Suggestions_at_the_caret_replace_the_word_or_add_it()
    {
        using var h = new SpellHarness();
        var doc = h.Open("// we RECIEVE it\n");
        await h.ResultAsync(doc, r => r.Issues.Count == 1);
        h.Navigation.ActiveDocument = new EditorDocumentView(doc);
        h.Navigation.ActiveCaret = new TextPosition(0, 8);
        h.Window.QuickPickResponses.Enqueue(0);
        await h.Commands.ExecuteAsync(SpellCheckHost.SuggestSpellingCorrections);
        Assert.Equal("// we RECEIVE it\n", doc.GetText());

        h.Edit(doc, TextEdit.Insert(new TextPosition(0, 16), " zorblax"));
        await h.ResultAsync(doc, r => r.Issues.Count == 1);
        h.Navigation.ActiveCaret = new TextPosition(0, 19);
        await h.Commands.ExecuteAsync(SpellCheckHost.AddWordToUserDictionary); // no argument: the word at the caret
        Assert.Equal(["zorblax"], h.Config.Get<string[]>(SpellSettingsLoader.Words)!);
        await h.ResultAsync(doc, r => r.Issues.Count == 0);
    }

    [Fact]
    public async Task Flag_word_reports_a_dictionary_word_as_forbidden()
    {
        using var h = new SpellHarness();
        var doc = h.Open("// the colour and the color\n");
        await h.ResultAsync(doc, r => r.Issues.Count == 1);
        await h.Commands.ExecuteAsync(SpellCheckHost.FlagWord, "color");
        var result = await h.ResultAsync(doc, r => r.Issues.Count == 2);
        Assert.Equal(new SpellIssue(0, 22, 5, "color", true), result.Issues[1]);
        Assert.Equal(["color"], h.Config.Get<string[]>(SpellSettingsLoader.FlagWords)!);
        Assert.Contains(h.DiagnosticsOf(doc), d => d.Message == "\"color\": Forbidden word.");
    }

    [Fact]
    public async Task Toggle_and_the_status_bar_follow_the_active_file()
    {
        using var h = new SpellHarness();
        var doc = h.Open(Code);
        await h.ResultAsync(doc, r => r.Issues.Count == 2);
        h.Navigation.ActiveDocument = new EditorDocumentView(doc);
        h.Config.Set("cscode.spellCheck.checkIdentifiers", false); // any settings change refreshes the item
        await WaitAsync(() => StatusText(h) == "$(book) Spell: 2");
        await h.Commands.ExecuteAsync(SpellCheckHost.ToggleEnableSpellChecker);
        Assert.False(h.Config.Get(SpellSettingsLoader.Enabled, true));
        await WaitAsync(() => StatusText(h) == "$(circle-slash) Spell");
        await h.ResultAsync(doc, r => r.State == SpellDocumentState.Disabled);
        Assert.Empty(h.Diagnostics.GetAll());
        await h.Commands.ExecuteAsync(SpellCheckHost.ToggleEnableSpellChecker);
        await h.ResultAsync(doc, r => r.Issues.Count == 2);
        Assert.Contains(h.Commands.All, c => c.Id == SpellCheckHost.EnableForWorkspace && c.Title == "Enable Spell Checking for Workspace");
    }

    [Fact]
    public async Task Free_text_is_checked_whole_with_offsets_into_it()
    {
        using var h = new SpellHarness();
        await h.Service.SettingsReady;
        ITextSpellChecker checker = new TextSpellChecker(h.Service, h.Config);
        const string message = "Fix the wierd bug\r\n\r\nIt is definately fixed.";
        var issues = await checker.CheckTextAsync(message, "scminput");
        Assert.Equal([(8, 5, "wierd"), (27, 10, "definately")], issues.Select(i => (i.Start, i.Length, i.Word)));
        Assert.Equal("wierd", message.Substring(issues[0].Start, issues[0].Length));
        Assert.Equal(["weird", "wired", "wider", "wield"], (await checker.SuggestAsync("wierd")).Take(4));

        var changed = 0;
        using var subscription = checker.Changed.Subscribe(_ => Interlocked.Increment(ref changed));
        await checker.AddToUserDictionaryAsync("wierd");
        Assert.Equal(["wierd"], h.Config.Get<string[]>(SpellSettingsLoader.Words)!);
        Assert.True(changed > 0);
        Assert.Equal(["definately"], (await checker.CheckTextAsync(message, "scminput")).Select(i => i.Word));

        h.Config.Set(SpellSettingsLoader.EnabledFileTypes, new Dictionary<string, bool> { ["scminput"] = false });
        await WaitAsync(() => !h.Service.Settings.IsLanguageEnabled("scminput"));
        Assert.Empty(await checker.CheckTextAsync(message, "scminput"));
    }

    private static string? StatusText(SpellHarness h) => h.Status.Items.FirstOrDefault(i => i.Id == SpellCheckHost.StatusItemId)?.Text;

    internal static async Task WaitAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Condition not met in time");
            await Task.Delay(10);
        }
    }
}
