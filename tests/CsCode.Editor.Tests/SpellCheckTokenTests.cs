using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Editor.Documents;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.TextMate;
using CsCode.Editor.Threading;
using CsCode.SpellCheck;
using CsCode.SpellCheck.Engine;
using CsCode.SpellCheck.Settings;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>
/// The spell checker over the editor's REAL tokens (VS Code's TextMate grammars through the token store, read by <see cref="SyntaxClassifier"/>):
/// in code only comments and strings are read — not identifiers, not the XML of a documentation comment, not escapes or interpolated code.
/// </summary>
public sealed class SpellCheckTokenTests : IDisposable
{
    private readonly ThreadAffinityUiThread _ui = new();
    private readonly TestHost _host;
    private readonly EditorWorker _worker = new("test-worker");
    private readonly TokenizationManager _manager;
    private readonly SyntaxClassifier _classifier;
    private readonly List<IDisposable> _registrations = [];

    public SpellCheckTokenTests()
    {
        _host = TestHost.Create(_ui);
        _manager = new TokenizationManager(_host.Services, _worker);
        _classifier = new SyntaxClassifier(_manager, _host.Documents);
    }

    public void Dispose()
    {
        foreach (var r in _registrations) r.Dispose();
        _classifier.Dispose();
        _manager.Dispose();
        _worker.Dispose();
        _host.Dispose();
    }

    private EditorDocument Open(string text, string language, string name)
    {
        var doc = new EditorDocument(new Uri($"file:///t/{name}"), language, EditorDocument.CreateBuffer(text, _ui), _ui);
        _registrations.Add(_host.Documents.Register(doc));
        var session = _manager.CreateSession(doc);
        session.AttachView();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (session.Store.ValidLines < doc.LineCount && sw.ElapsedMilliseconds < 30_000)
        {
            _ui.Pump();
            Thread.Sleep(5);
        }
        Assert.Equal(doc.LineCount, session.Store.ValidLines);
        return doc;
    }

    private IReadOnlyList<SpellIssue> Check(EditorDocument doc, SpellSettings? settings = null)
    {
        var classification = _classifier.Capture(doc.Uri);
        Assert.Equal(SyntaxClassificationState.Tokenized, classification.State);
        return CheckRun.CheckAll(doc.GetText(), classification, prose: false, settings ?? SpellSettings.Default, Dictionaries.EnUs);
    }

    private static string Words(IEnumerable<SpellIssue> issues) => string.Join(" ", issues.Select(i => $"{i.Line}:{i.Word}"));

    private const string CSharp = """
        // A small sample whose comments have misspelings; its code has its own.
        using System;

        namespace Sample.Recieve;

        /// <summary>
        /// Reads the <paramref name="configuraton"/> and returns the <see cref="Widgt"/> it describes.
        /// </summary>
        public static class Factory
        {
            public static string Describe(int count)
            {
                var mesage = $"Found {count} itmes in the {nameof(Widgt)} cach";
                var path = @"C:\Users\recieve\file.txt";
                return mesage + "\n\tdone with the regex " + System.Text.RegularExpressions.Regex.Escape("[a-z]+qwrty");
            }

            /* A block coment with a typo. */
        }
        """;

    [Fact]
    public void Csharp_comments_and_strings_are_read_and_code_documentation_markup_and_paths_are_not()
    {
        var doc = Open(CSharp, "csharp", "Sample.cs");
        var issues = Check(doc);
        // not: Recieve (a namespace), configuraton / Widgt (doc-comment attribute values), mesage (an identifier), Widgt in {nameof()}
        // (interpolated code), recieve (inside a path), the tags summary / paramref / see / cref
        Assert.Equal("0:misspelings 12:itmes 12:cach 14:qwrty 17:coment", Words(issues));
        // with identifiers on, the code's words are read as well
        var all = Check(doc, SpellSettings.Default with { CheckIdentifiers = true });
        Assert.Contains(all, i => i.Word == "Recieve" && i.Line == 3);
        Assert.Contains(all, i => i.Word == "mesage");
        Assert.DoesNotContain(all, i => i.Word == "configuraton"); // markup in a comment is never read
    }

    private const string Python = """
        #!/usr/bin/env python3
        \"\"\"Modle docstring with a speling mistake.\"\"\"
        import os  # an importent comment

        def comput_totl(itmes):
            \"\"\"Retruns the total.\"\"\"
            mesage = f"Total is {sum(itmes)} for {os.sep}"
            return mesage  # no tpyo here, honestly: just one
        """;

    [Fact]
    public void Python_comments_docstrings_and_strings_are_read_and_code_is_not()
    {
        var doc = Open(Python.Replace("\\\"", "\""), "python", "sample.py");
        var issues = Check(doc);
        // not: comput_totl / itmes / mesage (code), itmes inside the f-string's braces (code), env (a path's), python3 (digits)
        Assert.Equal("1:Modle 1:speling 2:importent 5:Retruns 7:tpyo", Words(issues));
    }

    [Fact]
    public void Markdown_prose_is_read_but_code_spans_fences_and_links_are_not()
    {
        const string md = """
            # A titel

            Some prose with a wierd word, `inlinee codde`, and [a linnk](https://exmaple.com/pathh).

            ```csharp
            var notprose = "a strng in a fence"; // a coment in a fence
            ```
            """;
        var doc = Open(md, "markdown", "README.md");
        var classification = _classifier.Capture(doc.Uri);
        var issues = CheckRun.CheckAll(doc.GetText(), classification, prose: true, SpellSettings.Default, Dictionaries.EnUs);
        // a fence is code; the comments and strings in it are read like any code's
        Assert.Equal("0:titel 2:wierd 2:linnk 5:strng 5:coment", Words(issues));
    }

    [Fact]
    public async Task The_checker_rechecks_when_the_editor_publishes_tokens()
    {
        var config = new CsCode.TestDoubles.TestConfigurationService();
        config.Set(SpellSettingsLoader.Delay, 0);
        var diagnostics = new DiagnosticService();
        using var service = new SpellCheckService(_host.Documents, diagnostics, config, classifier: _classifier);
        using var started = service.Start();
        var doc = new EditorDocument(new Uri("file:///t/late.cs"), "csharp", EditorDocument.CreateBuffer("int x; // a wierd word\n", _ui), _ui);
        _registrations.Add(_host.Documents.Register(doc));
        // no view yet: no tokens, nothing checked
        await Until(() => service.ResultFor(doc.Uri)?.State == SpellDocumentState.NoTokens);
        var session = _manager.CreateSession(doc);
        session.AttachView(); // a view shows it: tokens arrive, the editor says so, the checker reads them
        await Until(() => service.ResultFor(doc.Uri) is { Issues.Count: 1 });
        Assert.Equal("\"wierd\": Unknown word.", Assert.Single(diagnostics.Get(doc.Uri)).Message);
    }

    private async Task Until(Func<bool> condition)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > 20_000) throw new TimeoutException("condition not met");
            _ui.Pump();
            await Task.Delay(10);
        }
    }
}

internal static class Dictionaries
{
    private static readonly Lazy<(IReadOnlyList<DictionarySource> Sources, WeCantSpell.Hunspell.WordList[] Lists)> Bundled = new(() =>
    {
        var (chosen, _) = DictionaryCatalog.Choose(DictionaryCatalog.Discover(null), ["en"]);
        return (chosen, chosen.Select(DictionaryCatalog.Load).ToArray());
    });

    /// <summary>The bundled en_US and the software terms; a set per caller (its verdict cache is single-threaded).</summary>
    public static SpellDictionarySet EnUs => new(Bundled.Value.Sources, Bundled.Value.Lists, DictionaryCatalog.LoadSoftwareTerms(), []);
}
