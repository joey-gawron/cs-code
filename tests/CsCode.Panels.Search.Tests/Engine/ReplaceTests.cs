using System.Text;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Panels.Search.Engine;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Panels.Search.Tests.Engine;

/// <summary>Emulates the shell's NavigationService for closed files: read, decode (BOM aware), apply on an in-memory document, write back.</summary>
public sealed class DiskApplyingNavigation(IDocumentManager documents) : INavigationService
{
    private readonly RecordingNavigationService _inner = new(documents);
    public List<WorkspaceEdit> Edits => _inner.AppliedEdits;
    public IEditorDocument? ActiveDocument => null;
    public TextPosition? ActiveCaret => null;
    public IEventSource<IEditorDocument?> ActiveDocumentChanged => _inner.ActiveDocumentChanged;
    public IReadOnlyList<IEditorDocument> OpenDocuments => _inner.OpenDocuments;
    public ValueTask<IEditorDocument?> OpenAsync(Uri uri, TextRange? selection = null, bool preview = false, bool preserveFocus = false, CancellationToken ct = default) => _inner.OpenAsync(uri, selection, preview, preserveFocus, ct);

    public async ValueTask<bool> ApplyWorkspaceEditAsync(WorkspaceEdit edit, string? label = null, CancellationToken ct = default)
    {
        Edits.Add(edit);
        foreach (var (uri, edits) in edit.Changes)
        {
            if (documents.Get(uri) is { } doc) { if (!doc.ApplyEdits(edits)) return false; continue; }
            var bytes = await File.ReadAllBytesAsync(uri.LocalPath, ct);
            var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var text = new UTF8Encoding(false, true).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
            var mem = new InMemoryTextDocument(uri, "plaintext", text);
            if (!mem.ApplyEdits(edits)) return false;
            var body = new UTF8Encoding(false).GetBytes(mem.GetText());
            await File.WriteAllBytesAsync(uri.LocalPath, bom ? [.. Encoding.UTF8.GetPreamble(), .. body] : body, ct);
        }
        return true;
    }
}

/// <summary>
/// <see cref="DiskApplyingNavigation"/> plus the shell's failure path: a closed file it cannot write (read-only) makes it AWAIT its action-less
/// error toast before returning false, as NavigationService.FailAsync does (resolved by the shell 20 s later, or never).
/// </summary>
public sealed class ShellLikeNavigation(IDocumentManager documents, IWindowService windows) : INavigationService
{
    private readonly DiskApplyingNavigation _inner = new(documents);
    public List<Uri> Asked { get; } = new();
    public IEditorDocument? ActiveDocument => null;
    public TextPosition? ActiveCaret => null;
    public IEventSource<IEditorDocument?> ActiveDocumentChanged => _inner.ActiveDocumentChanged;
    public IReadOnlyList<IEditorDocument> OpenDocuments => _inner.OpenDocuments;
    public ValueTask<IEditorDocument?> OpenAsync(Uri uri, TextRange? selection = null, bool preview = false, bool preserveFocus = false, CancellationToken ct = default) => _inner.OpenAsync(uri, selection, preview, preserveFocus, ct);

    public async ValueTask<bool> ApplyWorkspaceEditAsync(WorkspaceEdit edit, string? label = null, CancellationToken ct = default)
    {
        foreach (var uri in edit.Changes.Keys)
        {
            Asked.Add(uri);
            if (documents.Get(uri) is null && new FileInfo(uri.LocalPath).IsReadOnly)
            {
                await windows.ShowMessageAsync(MessageSeverity.Error, $"Workspace edit failed: Access to the path '{uri.LocalPath}' is denied.", ct: ct);
                return false;
            }
        }
        return await _inner.ApplyWorkspaceEditAsync(edit, label, ct);
    }
}

public class ReplaceTests : IDisposable
{
    private readonly SearchHarness _h = new();
    private readonly ReplaceService _replace;
    private readonly DiskApplyingNavigation _nav;
    private readonly List<IDisposable> _regs = [];

    public ReplaceTests()
    {
        _replace = new ReplaceService(_h.Workspace, _h.Documents);
        _nav = new DiskApplyingNavigation(_h.Documents);
    }

    public void Dispose()
    {
        foreach (var r in _regs) r.Dispose();
        _replace.Dispose();
        _h.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<ReplacePlan> Plan(SearchQuery q, string with, bool preserveCase = false, params ReplaceTarget[] targets)
    {
        if (targets.Length == 0)
        {
            var found = await _h.RunAsync(q);
            targets = found.Files.Select(f => new ReplaceTarget(f.Uri)).ToArray();
        }
        return await _replace.PlanAsync(q, targets, new ReplaceOptions(with, preserveCase), Ct);
    }

    private TestEditorDocument Open(string rel, string text, bool dirty = true, string eol = "\n")
    {
        var d = new TestEditorDocument(_h.UriOf(rel), text, dirty, eol);
        _regs.Add(_h.Documents.Register(d));
        return d;
    }

    [Fact]
    public async Task Literal_replace_lists_old_and_new_text_per_match()
    {
        _h.Write("a.txt", "foo bar foo");
        var plan = await Plan(new SearchQuery("foo"), "baz");
        var items = plan.Files.Single().Items;
        Assert.Equal(["foo", "foo"], items.Select(i => i.OldText));
        Assert.Equal(["baz", "baz"], items.Select(i => i.NewText));
        Assert.Equal(2, plan.ReplacementCount);
    }

    [Fact]
    public async Task Literal_replace_does_not_expand_dollar_signs()
    {
        _h.Write("a.txt", "foo");
        var plan = await Plan(new SearchQuery("foo"), "$1 \\n");
        Assert.Equal("$1 \\n", plan.Files.Single().Items.Single().NewText);
    }

    [Fact]
    public async Task Regex_replace_expands_numbered_and_named_groups()
    {
        _h.Write("a.txt", "john smith\nada lovelace");
        var plan = await Plan(new SearchQuery(@"(?<first>\w+) (\w+)", IsRegex: true), "$1, ${first}"); // .NET numbers unnamed groups first: $1 is (\w+), ${first} the named one
        Assert.Equal(["smith, john", "lovelace, ada"], plan.Files.Single().Items.Select(i => i.NewText));
    }

    [Fact]
    public async Task Regex_replace_understands_backslash_n_t_and_backslash()
    {
        _h.Write("a.txt", "a=b");
        var plan = await Plan(new SearchQuery("=", IsRegex: true), @"\n\t\\");
        Assert.Equal("\n\t\\", plan.Files.Single().Items.Single().NewText);
    }

    [Fact]
    public async Task Newlines_in_the_replacement_follow_the_files_line_endings()
    {
        _h.Write("crlf.txt", "a=b\r\nc=d\r\n");
        var plan = await Plan(new SearchQuery("=", IsRegex: true), @"\n");
        Assert.All(plan.Files.Single().Items, i => Assert.Equal("\r\n", i.NewText));
    }

    [Theory]
    [InlineData("foo", "bar", "bar")]
    [InlineData("FOO", "bar", "BAR")]
    [InlineData("Foo", "bar", "Bar")]
    [InlineData("fOO", "bar", "bar")]
    [InlineData("123", "bar", "bar")]
    public void Preserve_case_follows_the_matched_text(string matched, string replacement, string expected) =>
        Assert.Equal(expected, ReplaceService.PreserveCase(matched, replacement));

    [Fact]
    public async Task Preserve_case_applies_per_match()
    {
        _h.Write("a.txt", "foo Foo FOO");
        var plan = await Plan(new SearchQuery("foo"), "bar", preserveCase: true);
        Assert.Equal(["bar", "Bar", "BAR"], plan.Files.Single().Items.Select(i => i.NewText));
    }

    [Fact]
    public async Task Replace_in_a_dirty_open_document_is_one_undo_step()
    {
        _h.Write("a.txt", "disk");
        var doc = Open("a.txt", "foo\nfoo foo\nxx foo");
        var versionBefore = doc.Version;
        var plan = await Plan(new SearchQuery("foo"), "bar");
        var result = await _replace.ApplyAsync(plan, _nav, "Replace in Files", ct: Ct);
        Assert.Equal(1, doc.ApplyEditsCalls);               // ONE ApplyEdits call = ONE undo unit (Core contract)
        Assert.Equal(versionBefore + 1, doc.Version);
        Assert.Equal("bar\nbar bar\nxx bar", doc.GetText());
        Assert.Equal((1, 4, false), (result.FilesChanged, result.Replacements, result.Cancelled));
        Assert.Equal([_h.UriOf("a.txt")], result.ChangedFiles);
    }

    [Fact]
    public async Task Replace_on_a_closed_file_edits_it_on_disk_and_preserves_crlf_and_the_bom()
    {
        var path = _h.Dir.Combine("bom.txt");
        File.WriteAllBytes(path, [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("one foo\r\ntwo foo\r\n")]);
        var plan = await Plan(new SearchQuery("foo"), "bar");
        await _replace.ApplyAsync(plan, _nav, ct: Ct);
        Assert.Equal([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("one bar\r\ntwo bar\r\n")], File.ReadAllBytes(path));
    }

    [Fact]
    public async Task A_multi_line_regex_replace_collapses_lines()
    {
        _h.Write("a.txt", "x\r\nfoo\r\nbar\r\ny");
        var plan = await Plan(new SearchQuery(@"foo\r?\nbar", IsRegex: true), "joined");
        await _replace.ApplyAsync(plan, _nav, ct: Ct);
        Assert.Equal("x\r\njoined\r\ny", File.ReadAllText(_h.Dir.Combine("a.txt")));
    }

    [Fact]
    public async Task Only_the_chosen_matches_are_replaced_when_ranges_are_given()
    {
        _h.Write("a.txt", "foo foo foo");
        var plan = await Plan(new SearchQuery("foo"), "bar", false, new ReplaceTarget(_h.UriOf("a.txt"), [TextRange.FromLines(0, 4, 0, 7)]));
        await _replace.ApplyAsync(plan, _nav, ct: Ct);
        Assert.Equal("foo bar foo", File.ReadAllText(_h.Dir.Combine("a.txt")));
    }

    [Fact]
    public async Task A_match_that_moved_since_the_search_is_skipped_not_replaced_blindly()
    {
        _h.Write("a.txt", "foo");
        var stale = TextRange.FromLines(0, 10, 0, 13); // pretend the search saw a match here, but the file has changed
        var plan = await Plan(new SearchQuery("foo"), "bar", false, new ReplaceTarget(_h.UriOf("a.txt"), [stale]));
        Assert.Empty(plan.Files);
        Assert.Contains("changed since the search", plan.Skipped.Single().Reason);
    }

    [Fact]
    public async Task A_file_that_changed_on_disk_is_re_searched_so_new_matches_are_not_missed_and_old_ones_not_hit()
    {
        _h.Write("a.txt", "foo one\nfoo two");
        var before = await _h.RunAsync(new SearchQuery("foo"));
        File.WriteAllText(_h.Dir.Combine("a.txt"), "\n\n\n\nfoo one\nfoo two");   // everything moved down four lines
        var q = new SearchQuery("foo");
        var plan = await _replace.PlanAsync(q, [new ReplaceTarget(before.Files[0].Uri, before.Files[0].Matches.Select(m => m.Range).ToArray())], new ReplaceOptions("bar"), Ct);
        Assert.Empty(plan.Files); // none of the remembered ranges exist any more: nothing is touched
        Assert.Equal("\n\n\n\nfoo one\nfoo two", File.ReadAllText(_h.Dir.Combine("a.txt")));
    }

    [Fact]
    public async Task A_dirty_document_that_changed_after_planning_is_skipped_at_apply_time()
    {
        _h.Write("a.txt", "x");
        var doc = Open("a.txt", "foo");
        var plan = await Plan(new SearchQuery("foo"), "bar");
        doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 0), "typed ")]);   // the user kept typing
        var result = await _replace.ApplyAsync(plan, _nav, ct: Ct);
        Assert.Equal(0, result.FilesChanged);
        Assert.Contains("changed since the search", Assert.Single(result.Problems));
        Assert.Equal("typed foo", doc.GetText());
    }

    [Fact]
    public async Task A_closed_file_that_changed_on_disk_after_planning_is_skipped_at_apply_time()
    {
        var path = _h.Write("a.txt", "x\nfoo\n");
        var plan = await Plan(new SearchQuery("foo"), "bar");
        File.WriteAllText(path, "inserted\nx\nfoo\n");                    // e.g. a git pull while the confirmation was showing
        var result = await _replace.ApplyAsync(plan, _nav, ct: Ct);
        Assert.Equal(0, result.FilesChanged);
        Assert.Contains("changed", Assert.Single(result.Problems));
        Assert.Equal("inserted\nx\nfoo\n", File.ReadAllText(path));        // the old ranges were not applied to the new text
        Assert.Empty(_nav.Edits);
    }

    [Fact]
    public async Task A_document_closed_with_its_changes_discarded_after_planning_is_skipped()
    {
        var path = _h.Write("a.txt", "on disk");
        var reg = _h.Documents.Register(new TestEditorDocument(_h.UriOf("a.txt"), "foo in memory", dirty: true));
        var plan = await Plan(new SearchQuery("foo"), "bar");
        Assert.NotNull(plan.Files.Single().Version);
        reg.Dispose();                                                     // closed, "Don't save"
        var result = await _replace.ApplyAsync(plan, _nav, ct: Ct);
        Assert.Equal(0, result.FilesChanged);
        Assert.Equal("on disk", File.ReadAllText(path));                   // the buffer's ranges never reach the disk copy
    }

    [Fact]
    public async Task A_closed_file_that_was_opened_unchanged_after_planning_is_still_replaced()
    {
        _h.Write("a.txt", "one foo\ntwo foo");
        var plan = await Plan(new SearchQuery("foo"), "bar");
        var doc = Open("a.txt", "one foo\ntwo foo", dirty: false);        // the user clicked a result: a preview tab, nothing edited
        var result = await _replace.ApplyAsync(plan, _nav, ct: Ct);
        Assert.Equal(1, result.FilesChanged);
        Assert.Equal("one bar\ntwo bar", doc.GetText());
    }

    [Fact]
    public async Task Opening_a_crlf_file_in_an_editor_that_normalises_line_endings_is_not_a_change()
    {
        _h.Write("a.txt", "one foo\r\ntwo foo\r\n");
        var plan = await Plan(new SearchQuery("foo"), "bar");
        var doc = Open("a.txt", "one foo\ntwo foo\n", dirty: false);      // same lines and columns, other line ending style
        var result = await _replace.ApplyAsync(plan, _nav, ct: Ct);
        Assert.Equal(1, result.FilesChanged);
        Assert.Equal("one bar\ntwo bar\n", doc.GetText());
    }

    [Fact]
    public async Task A_closed_file_that_was_opened_and_edited_after_planning_is_skipped()
    {
        _h.Write("a.txt", "foo");
        var plan = await Plan(new SearchQuery("foo"), "bar");
        var doc = Open("a.txt", "foo");
        doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 0), "typed ")]);
        var result = await _replace.ApplyAsync(plan, _nav, ct: Ct);
        Assert.Equal(0, result.FilesChanged);
        Assert.Equal("typed foo", doc.GetText());
    }

    [Fact]
    public async Task A_regex_that_times_out_in_one_file_skips_that_file_instead_of_failing_the_plan()
    {
        using var replace = new ReplaceService(_h.Workspace, _h.Documents, new SearchLimits { RegexTimeout = TimeSpan.FromMilliseconds(50) });
        _h.Write("evil.txt", new string('a', 40) + "c");
        _h.Write("fine.txt", "aaa");
        var plan = await replace.PlanAsync(new SearchQuery("(?=a)(a+)+$", IsRegex: true), [new ReplaceTarget(_h.UriOf("evil.txt")), new ReplaceTarget(_h.UriOf("fine.txt"))],
            new ReplaceOptions("b"), Ct);
        Assert.Equal(["fine.txt"], plan.Files.Select(f => Path.GetFileName(f.Uri.LocalPath)));
        Assert.Contains("timed out", Assert.Single(plan.Skipped).Reason);
    }

    [Fact]
    public async Task The_replace_service_built_for_the_panel_honours_the_search_size_limit_setting()
    {
        var config = new TestConfigurationService();
        config.Set(SearchLimits.MaxFileSizeKey, 8L * 1024 * 1024);            // raised above the 5 MiB default
        using var sp = new ServiceCollection().AddSingleton<IWorkspaceService>(_h.Workspace).AddSingleton<IConfigurationService>(config).BuildServiceProvider();
        var s = CsCode.Panels.Search.ViewModels.SearchServices.From(sp);
        try
        {
            _h.Write("big.txt", "needle\n" + new string('x', 6 * 1024 * 1024));
            var plan = await s.Replace.PlanAsync(new SearchQuery("needle"), [new ReplaceTarget(_h.UriOf("big.txt"))], new ReplaceOptions("pin"), Ct);
            Assert.Empty(plan.Skipped);                                       // the search shows the match, so replace must be able to replace it
            Assert.Equal(1, plan.ReplacementCount);
        }
        finally
        {
            s.Engine.Dispose();
            s.Replace.Dispose();
        }
    }

    [Fact]
    public async Task Files_that_are_not_utf8_or_utf16_are_skipped_with_a_reason()
    {
        File.WriteAllBytes(_h.Dir.Combine("latin.txt"), [.. "caf"u8.ToArray(), 0xE9, .. " foo"u8.ToArray()]);
        var plan = await _replace.PlanAsync(new SearchQuery("foo"), [new ReplaceTarget(_h.UriOf("latin.txt"))], new ReplaceOptions("bar"), Ct);
        Assert.Empty(plan.Files);
        Assert.Contains("UTF-8", plan.Skipped.Single().Reason);
    }

    [Fact]
    public async Task One_bad_file_does_not_stop_the_others()
    {
        _h.Write("good.txt", "foo");
        var plan = await _replace.PlanAsync(new SearchQuery("foo"), [new ReplaceTarget(_h.UriOf("gone.txt")), new ReplaceTarget(_h.UriOf("good.txt"))], new ReplaceOptions("bar"), Ct);
        Assert.Equal(["good.txt"], plan.Files.Select(f => Path.GetFileName(f.Uri.LocalPath)));
        Assert.Single(plan.Skipped);
    }

    [Fact]
    public async Task Each_file_is_applied_as_its_own_single_file_edit_so_the_shell_never_prompts_again()
    {
        _h.Write("a.txt", "foo"); _h.Write("b.txt", "foo"); _h.Write("c.txt", "foo");
        var plan = await Plan(new SearchQuery("foo"), "bar");
        await _replace.ApplyAsync(plan, _nav, ct: Ct);
        Assert.Equal(3, _nav.Edits.Count);
        Assert.All(_nav.Edits, e => Assert.Single(e.Changes));
    }

    [Fact]
    public async Task Apply_stops_when_cancelled_and_reports_it()
    {
        _h.Write("a.txt", "foo"); _h.Write("b.txt", "foo");
        var plan = await Plan(new SearchQuery("foo"), "bar");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var result = await _replace.ApplyAsync(plan, _nav, ct: cts.Token);
        Assert.True(result.Cancelled);
        Assert.Equal(0, result.FilesChanged);
    }

    [Fact]
    public async Task Planning_does_not_block_the_calling_thread()
    {
        for (var i = 0; i < 100; i++) _h.Write($"f{i}.txt", string.Join("\n", Enumerable.Repeat("foo bar foo", 500)));
        var targets = Enumerable.Range(0, 100).Select(i => new ReplaceTarget(_h.UriOf($"f{i}.txt"))).ToArray();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var task = _replace.PlanAsync(new SearchQuery("foo"), targets, new ReplaceOptions("x"), Ct);
        var sync = sw.Elapsed;
        var plan = await task;
        Assert.True(sync < TimeSpan.FromMilliseconds(10), $"PlanAsync spent {sync.TotalMilliseconds:F2} ms before returning");
        Assert.Equal(100_000, plan.ReplacementCount);
    }

    [Theory]
    [InlineData("foo", false, "bar", "bar")]
    [InlineData("(\\w+)@(\\w+)", true, "$2:$1", "example:user")]
    [InlineData("user@example", true, "\\t$&", "\tuser@example")]
    public void Preview_replacement_for_the_results_tree(string pattern, bool regex, string with, string expected) =>
        Assert.Equal(expected, ReplaceService.PreviewReplacement(new SearchQuery(pattern, IsRegex: regex), regex ? "user@example" : "foo", new ReplaceOptions(with)));

    [Fact]
    public void Escapes_are_expanded_only_for_n_t_and_backslash() =>
        Assert.Equal("a\nb\tc\\d\\x", ReplaceService.ExpandEscapes(@"a\nb\tc\\d\x"));
}
