using System.Globalization;
using System.Text;
using CsCode.Core.Diagnostics;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Panels.Org.Engine;
using CsCode.Panels.Org.Language;

namespace CsCode.Panels.Org;

/// <summary>The clock as the host keeps it: the entry it runs on (as last read) and since when.</summary>
public sealed record OrgRunningClock(Uri Uri, OrgHeading Heading, DateTime Start)
{
    public string FileName => Path.GetFileName(Uri.LocalPath);
}

// The host's later features: the clock (and its status bar item), refile and archive, export, code blocks and capture templates.
public sealed partial class OrgHost
{
    public const string ClockIn = "org.clockIn";
    public const string ClockOut = "org.clockOut";
    public const string ClockCancel = "org.clockCancel";
    public const string ClockGoto = "org.clockGoto";
    public const string ClockReport = "org.clockReport";
    public const string ClockMenu = "org.clockMenu";
    public const string Refile = "org.refile";
    public const string ArchiveSubtree = "org.archiveSubtree";
    public const string ToggleArchiveTag = "org.toggleArchiveTag";
    public const string Export = "org.export";
    public const string ExportHtml = "org.exportHtml";
    public const string ExportMarkdown = "org.exportMarkdown";
    public const string BabelExecute = "org.babelExecute";
    public const string BabelExecuteBuffer = "org.babelExecuteBuffer";
    public const string BabelOutputChannel = "Org Babel";

    private readonly IFileSystem? _fs;
    private readonly IOutputService? _output;
    private readonly IMemoryMonitor? _memory;
    private readonly Emitter<OrgRunningClock?> _clockChanged = new();
    private OrgRunningClock? _clock;
    private IDisposable? _clockItem;
    private Timer? _clockTimer;

    private void RegisterFeatureCommands(Action<string, string, Func<CommandInvocation, Task>> reg)
    {
        reg(ClockIn, "Clock In", async _ => { if (ActiveEntry() is { } at) await ClockInAsync(at.Parsed, at.Heading).ConfigureAwait(false); });
        reg(ClockOut, "Clock Out", _ => ClockOutAsync());
        reg(ClockCancel, "Cancel Clock", _ => CancelClockAsync());
        reg(ClockGoto, "Go to Clocked Entry", _ => GotoClockAsync());
        reg(ClockReport, "Insert or Update Clock Table", _ => OnEntryAsync((doc, _, line) => OrgClock.Table(doc, line, Now()), needsHeading: false));
        reg(ClockMenu, "Clock...", _ => ClockMenuAsync());
        reg(Refile, "Refile...", async _ => { if (ActiveEntry() is { } at) await RefileAsync(at.Parsed, at.Heading).ConfigureAwait(false); });
        reg(ArchiveSubtree, "Archive Subtree", async _ => { if (ActiveEntry() is { } at) await ArchiveAsync(at.Parsed, at.Heading).ConfigureAwait(false); });
        reg(ToggleArchiveTag, "Toggle ARCHIVE Tag", _ => OnEntryAsync((doc, h, _) =>
            OrgEdits.SetTags(doc, h, h.Tags.Contains("ARCHIVE") ? [.. h.Tags.Where(t => t != "ARCHIVE")] : [.. h.Tags, "ARCHIVE"])));
        reg(Export, "Export...", _ => ExportAsync(null, open: null));
        reg(ExportHtml, "Export to HTML", _ => ExportAsync(OrgExportFormat.Html, open: false));
        reg(ExportMarkdown, "Export to Markdown", _ => ExportAsync(OrgExportFormat.Markdown, open: false));
        reg(BabelExecute, "Run Code Block", async _ =>
        {
            if (ActiveOrgEditor() is not { } at) return;
            if (OrgBabel.At(at.Parsed, at.Caret.Line) is { } block) await RunBlocksAsync(at.Document, [block]).ConfigureAwait(false);
            else Status("The caret is not in a code block (#+BEGIN_SRC ... #+END_SRC)");
        });
        reg(BabelExecuteBuffer, "Run All Code Blocks in File", async _ =>
        {
            if (ActiveOrgEditor() is not { } at) return;
            var blocks = OrgBabel.All(at.Parsed);
            if (blocks.Count == 0) Status("This file has no code blocks");
            else await RunBlocksAsync(at.Document, blocks).ConfigureAwait(false);
        });
    }

    /// <summary>The active org editor with its text parsed and the caret (no entry needed).</summary>
    private (IEditorDocument Document, OrgDocument Parsed, TextPosition Caret)? ActiveOrgEditor()
    {
        if (Navigation?.ActiveDocument is not { LanguageId: OrgLanguage.LanguageId } doc || Navigation.ActiveCaret is not { } caret) return null;
        return (doc, OrgDocument.Parse(doc.GetText(), _keywords, uri: doc.Uri), caret);
    }

    /// <summary>
    /// Org's C-c C-c: in a code block (or on its results) runs it; in a clock table brings it up to date; on a check box toggles it; on a heading
    /// with a statistics cookie updates the cookie.
    /// </summary>
    private async Task CtrlCCtrlCAsync()
    {
        if (ActiveOrgEditor() is not { } at) return;
        if (OrgBabel.At(at.Parsed, at.Caret.Line) is { } block) { await RunBlocksAsync(at.Document, [block]).ConfigureAwait(false); return; }
        if (OrgClock.TableAt(at.Parsed, at.Caret.Line) is not null)
        {
            await OnEntryAsync((doc, _, line) => OrgClock.Table(doc, line, Now()), needsHeading: false).ConfigureAwait(false);
            return;
        }
        await OnEntryAsync((doc, _, line) => OrgEdits.ToggleCheckbox(doc, line), needsHeading: false).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------------------------------------ the clock

    /// <summary>The running clock (one at a time, as in org), or null.</summary>
    public OrgRunningClock? RunningClock => _clock;

    /// <summary>The clock started or stopped (UI thread).</summary>
    public IEventSource<OrgRunningClock?> ClockChanged => _clockChanged;

    private void SetClock(OrgRunningClock? clock)
    {
        if (_clock == clock) return;
        _clock = clock;
        UpdateClockItem();
        _clockChanged.Fire(clock);
    }

    /// <summary>A clock left running in an agenda file (CsCode restarted, Emacs clocked in) is the running clock; one the files no longer have is not.</summary>
    private void UpdateClockFromStore()
    {
        if (Store is null) return;
        if (OrgClock.FindRunning(Store.Documents) is { } r && r.Document.Uri is { } uri) SetClock(new OrgRunningClock(uri, r.Heading, r.Clock.Start));
        else if (_clock is { } c && Store.IsTracked(c.Uri)) SetClock(null);
    }

    private void UpdateClockItem()
    {
        _clockItem?.Dispose();
        _clockItem = null;
        if (_clock is not { } c)
        {
            _clockTimer?.Dispose();
            _clockTimer = null;
            return;
        }
        _clockTimer ??= new Timer(_ => _ui.Post(UpdateClockItem), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        if (StatusBar is null || Config?.Get(OrgSettings.StatusBar, true) == false) return;
        var title = c.Heading.Title.Length > 30 ? c.Heading.Title[..29] + "…" : c.Heading.Title;
        var elapsed = OrgClock.Format(Now() - c.Start);
        _clockItem = StatusBar.Set(new StatusBarItemModel("org.clock", $"$(watch) {elapsed} {title}",
            $"Org clock: \"{c.Heading.Title}\" ({c.FileName}) since {c.Start:HH:mm}. Clock Out, Go to Entry, Cancel", ClockMenu, StatusBarAlignment.Left, 49));
    }

    private void DisposeClock()
    {
        _clockTimer?.Dispose();
        _clockItem?.Dispose();
        _clockChanged.Clear();
    }

    /// <summary>Starts the clock on an entry, stopping the one that runs first (org's C-c C-x C-i).</summary>
    public async Task ClockInAsync(OrgDocument snapshot, OrgHeading heading)
    {
        if (snapshot.Uri is not { } uri) return;
        if (_clock is { } running)
        {
            if (running.Uri == uri && running.Heading.Title == heading.Title && running.Heading.Level == heading.Level) { Status($"The clock already runs on \"{heading.Title}\""); return; }
            await ClockOutAsync().ConfigureAwait(false);
        }
        var r = await EditFileAsync(uri, doc => Resolve(doc, heading) is { } h ? OrgClock.ClockIn(doc, h, Now()) : null).ConfigureAwait(false);
        if (r is null) { Status("That entry is no longer in the file"); return; }
        var now = Now();
        await _ui.InvokeAsync(() => SetClock(new OrgRunningClock(uri, heading, new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0)))).ConfigureAwait(false);
        if (r.Message is { } m) Status(m);
    }

    /// <summary>Stops the running clock (org's C-c C-x C-o), wherever it runs.</summary>
    public async Task ClockOutAsync()
    {
        if (_clock is not { } c) { Status("No clock is running"); return; }
        var r = await EditFileAsync(c.Uri, doc => Resolve(doc, c.Heading) is { } h ? OrgClock.ClockOut(doc, h, Now()) : null).ConfigureAwait(false);
        await _ui.InvokeAsync(() => SetClock(null)).ConfigureAwait(false);
        Status(r?.Message ?? "No running clock was found in " + c.FileName);
    }

    private async Task CancelClockAsync()
    {
        if (_clock is not { } c) { Status("No clock is running"); return; }
        var r = await EditFileAsync(c.Uri, doc => Resolve(doc, c.Heading) is { } h ? OrgClock.Cancel(doc, h) : null).ConfigureAwait(false);
        await _ui.InvokeAsync(() => SetClock(null)).ConfigureAwait(false);
        if (r?.Message is { } m) Status(m);
    }

    private async Task GotoClockAsync()
    {
        if (_clock is not { } c) { Status("No clock is running"); return; }
        await OpenEntryAsync(OrgDocument.Parse("", _keywords, uri: c.Uri), c.Heading).ConfigureAwait(false);
    }

    private async Task ClockMenuAsync()
    {
        if (Window is null || _clock is not { } c) return;
        var pick = await Window.ShowQuickPickAsync(
        [
            new QuickPickItem("Clock Out", $"stop the clock on \"{c.Heading.Title}\" after {OrgClock.Format(Now() - c.Start)}", Tag: ClockOut),
            new QuickPickItem("Go to Clocked Entry", c.FileName, Tag: ClockGoto),
            new QuickPickItem("Cancel Clock", "remove the running clock line", Tag: ClockCancel),
        ], new QuickPickOptions(Title: "Org Clock")).ConfigureAwait(false);
        if (pick?.Tag is string command && Commands is { } commands) await commands.ExecuteAsync(command).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------------------------------------ refile, archive

    /// <summary>
    /// Org's C-c C-w: asks where the entry goes (a heading down to <c>org.refile.maxLevel</c>, or the top level of a file, in the agenda's files
    /// and the entry's own) and moves its subtree there as the last child. Across files the target gets the subtree first, then the source loses it.
    /// </summary>
    public async Task RefileAsync(OrgDocument snapshot, OrgHeading heading)
    {
        if (Window is null || Store is null || snapshot.Uri is not { } uri) return;
        var docs = Store.Documents.Where(d => d.Uri != uri).ToList();
        docs.Insert(0, await Store.ReadAsync(uri).ConfigureAwait(false) ?? snapshot);
        var source = docs[0];
        var h = Resolve(source, heading);
        if (h is null) { Status("That entry is no longer in the file"); return; }
        var targets = OrgRefile.Targets(docs, Math.Max(1, Config?.Get(OrgSettings.RefileMaxLevel, 3) ?? 3))
            .Where(t => !(t.Document.Uri == uri && t.Heading is { } x && x.Line >= h.Line && x.Line < h.SubtreeEnd)).ToList();
        var pick = await Window.ShowQuickPickAsync([.. targets.Select(t => new QuickPickItem(t.Label, t.Heading?.Keyword, Tag: t))],
            new QuickPickOptions(Title: $"Refile \"{h.Title}\" to", Placeholder: "Type to find a heading")).ConfigureAwait(false);
        if (pick?.Tag is not OrgRefileTarget target || target.Document.Uri is not { } targetUri) return;

        if (targetUri == uri)
        {
            var moved = await EditFileAsync(uri, doc =>
                Resolve(doc, h) is { } x ? OrgRefile.MoveWithin(doc, x, target.Heading is { } t ? Resolve(doc, t) : null) : null).ConfigureAwait(false);
            Status(moved is null ? "Could not refile there" : $"Refiled to {target.Label}");
            return;
        }
        var lines = OrgRefile.SubtreeLines(source, h);
        var inserted = await EditFileAsync(targetUri, doc =>
            target.Heading is null ? OrgRefile.InsertUnder(doc, null, lines, h.Level)
            : Resolve(doc, target.Heading) is { } t ? OrgRefile.InsertUnder(doc, t, lines, h.Level) : null).ConfigureAwait(false);
        if (inserted is null) { await ShowErrorAsync($"Could not refile to {target.Label}: the heading is no longer there").ConfigureAwait(false); return; }
        await EditFileAsync(uri, doc => Resolve(doc, h) is { } x ? OrgRefile.Remove(doc, x) : null).ConfigureAwait(false);
        Status($"Refiled to {target.Label}");
    }

    /// <summary>
    /// Org's C-c C-x C-s: moves the subtree to the archive (<c>org.archiveLocation</c>, by default <c>notes.org_archive</c> next to the file,
    /// created when missing), with the <c>ARCHIVE_*</c> properties org records.
    /// </summary>
    public async Task ArchiveAsync(OrgDocument snapshot, OrgHeading heading)
    {
        if (Store is null || snapshot.Uri is not { IsFile: true } uri) return;
        var sourcePath = uri.LocalPath;
        var (file, archiveHeading) = OrgRefile.ArchiveLocation(Config?.Get<string>(OrgSettings.ArchiveLocation) is { Length: > 0 } l ? l : "%s_archive::", sourcePath);
        var source = await Store.ReadAsync(uri).ConfigureAwait(false);
        if (source is null || Resolve(source, heading) is not { } h) { Status("That entry is no longer in the file"); return; }
        var archived = OrgRefile.ArchivedSubtree(source, h, Now(), sourcePath);
        var archiveUri = new Uri(file);
        if (archiveUri == uri)
        {
            // within the file: remove, then insert into what is left, as one edit of the whole text
            await EditFileAsync(uri, doc =>
            {
                if (Resolve(doc, h) is not { } x) return null;
                var text = OrgText.Apply(string.Join('\n', doc.Lines), OrgRefile.Remove(doc, x).Edits);
                var rest = OrgDocument.Parse(text, _keywords, uri: uri);
                var final = OrgText.Apply(text, OrgRefile.InsertIntoArchive(rest, archiveHeading, archived, x.Level).Edits);
                return new OrgEditResult([OrgEdits.ReplaceLines(doc, 0, doc.LineCount, OrgDocument.SplitLines(final))]);
            }).ConfigureAwait(false);
        }
        else
        {
            try { await Store.EnsureFileAsync(archiveUri, OrgRefile.ArchiveFileHeader(sourcePath)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await ShowErrorAsync($"Could not create {file}: {ex.Message}").ConfigureAwait(false);
                return;
            }
            if (await EditFileAsync(archiveUri, doc => OrgRefile.InsertIntoArchive(doc, archiveHeading, archived, h.Level)).ConfigureAwait(false) is null)
            {
                await ShowErrorAsync($"Could not write the archive {file}").ConfigureAwait(false);
                return;
            }
            await EditFileAsync(uri, doc => Resolve(doc, h) is { } x ? OrgRefile.Remove(doc, x) : null).ConfigureAwait(false);
        }
        Status($"Archived \"{h.Title}\" to {Path.GetFileName(file)}");
    }

    // ------------------------------------------------------------------------------------------------ export

    /// <summary>
    /// Org's C-c C-e: exports the active org file to <c>name.html</c> or <c>name.md</c> next to it; asked which (and whether to open it) when
    /// <paramref name="format"/> is null. HTML opens in the browser, Markdown in the editor.
    /// </summary>
    public async Task ExportAsync(OrgExportFormat? format, bool? open)
    {
        if (ActiveOrgEditor() is not { } at || !at.Document.Uri.IsFile) { Status("Open an org file to export it"); return; }
        if (format is null)
        {
            if (Window is null) return;
            var pick = await Window.ShowQuickPickAsync(
            [
                new QuickPickItem("HTML and open it", "a web page next to the file, opened in the browser", Tag: (OrgExportFormat.Html, true)),
                new QuickPickItem("HTML", "a web page next to the file", Tag: (OrgExportFormat.Html, false)),
                new QuickPickItem("Markdown and open it", "a .md file next to the file, opened in the editor", Tag: (OrgExportFormat.Markdown, true)),
                new QuickPickItem("Markdown", "a .md file next to the file", Tag: (OrgExportFormat.Markdown, false)),
            ], new QuickPickOptions(Title: "Export " + Path.GetFileName(at.Document.Uri.LocalPath))).ConfigureAwait(false);
            if (pick?.Tag is not (OrgExportFormat f, bool o)) return;
            (format, open) = (f, o);
        }
        var target = Path.ChangeExtension(at.Document.Uri.LocalPath, format == OrgExportFormat.Html ? ".html" : ".md");
        var targetUri = new Uri(target);
        try
        {
            if (_fs is null) return;
            await _fs.WriteFileAsync(targetUri, Encoding.UTF8.GetBytes(OrgExport.Export(at.Parsed, format.Value))).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ShowErrorAsync($"Could not write {target}: {ex.Message}").ConfigureAwait(false);
            return;
        }
        Status("Exported to " + Path.GetFileName(target));
        if (open != true) return;
        if (format == OrgExportFormat.Html && Window is not null) await Window.OpenExternalAsync(targetUri).ConfigureAwait(false);
        else if (Navigation is not null) await Navigation.OpenAsync(targetUri).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------------------------------------ code blocks

    /// <summary>
    /// Runs code blocks of an org editor one after another (org-babel), after asking (<c>org.babel.confirmEvaluate</c>): each as a program
    /// (<c>org.babel.languages</c>) in its <c>:dir</c> or the file's folder, under a cancellable progress notification, stopped after
    /// <c>org.babel.timeout</c>; its output becomes its <c>#+RESULTS:</c> (not with <c>:results silent</c>); its errors go to the "Org Babel"
    /// output channel.
    /// </summary>
    private async Task RunBlocksAsync(IEditorDocument doc, IReadOnlyList<OrgSrcBlock> blocks)
    {
        if (Window is null) return;
        var unknown = blocks.Select(b => b.Language).Where(l => OrgSettings.BabelProgram(Config, l) is null).Distinct().ToList();
        if (unknown.Count > 0)
        {
            await Window.ShowMessageAsync(MessageSeverity.Warning, $"No program runs {string.Join(", ", unknown.Select(l => l.Length == 0 ? "blocks without a language" : l + " blocks"))}. Name one in org.babel.languages (for example \"{(unknown[0].Length == 0 ? "sh" : unknown[0])}\": \"...\").").ConfigureAwait(false);
            return;
        }
        if (Config?.Get(OrgSettings.BabelConfirm, true) != false)
        {
            var languages = string.Join(", ", blocks.Select(b => b.Language).Distinct());
            var what = blocks.Count == 1 ? $"this {languages} code block" : $"{blocks.Count} code blocks ({languages})";
            var answer = await Window.ShowMessageAsync(MessageSeverity.Warning, $"Run {what} of {Path.GetFileName(doc.Uri.LocalPath)} on this computer?",
                [new MessageAction("Run"), new MessageAction("Always Run"), new MessageAction("Cancel", IsCloseAffordance: true)]).ConfigureAwait(false);
            if (answer == "Always Run" && Config is not null) await Config.UpdateAsync(OrgSettings.BabelConfirm, false).ConfigureAwait(false);
            else if (answer != "Run") return;
        }
        await LoginShellEnvironment.WaitAsync().ConfigureAwait(false);
        var channel = _output?.GetOrCreate(BabelOutputChannel);
        var timeout = TimeSpan.FromSeconds(Math.Max(1, Config?.Get(OrgSettings.BabelTimeout, 60) ?? 60));
        var all = OrgBabel.All(OrgDocument.Parse(doc.GetText(), _keywords, uri: doc.Uri));
        foreach (var block in blocks)
        {
            var index = all.ToList().FindIndex(b => b.Begin == block.Begin);
            var (program, extension) = OrgSettings.BabelProgram(Config, block.Language)!.Value;
            var dir = await WorkingDirectoryAsync(doc.Uri, block.Header(":dir")).ConfigureAwait(false);
            channel?.AppendLine($"[{DateTime.Now:HH:mm:ss}] {program} ({block.Language} block at line {block.Begin + 1} of {Path.GetFileName(doc.Uri.LocalPath)}) in {dir}");
            IDisposable? tracked = null;
            var run = await Window.WithProgressAsync($"Running the {block.Language} block", cancellable: true, (_, ct) =>
                OrgBabelRunner.RunAsync(program, extension, OrgBabel.WithVariables(block), dir, timeout, pid => tracked = _memory?.TrackChildProcess("org-babel " + block.Language, pid), ct)).ConfigureAwait(false);
            tracked?.Dispose();
            if (run.Error.Length > 0) channel?.Append(run.Error.EndsWith('\n') ? run.Error : run.Error + "\n");
            channel?.AppendLine(run.TimedOut ? $"stopped after {timeout.TotalSeconds:0} s" : run.ExitCode is { } code ? $"exit code {code}" : "canceled");
            if (run.ExitCode is null && !run.TimedOut && run.Error.Length == 0) return; // canceled: the rest too
            if (!block.Silent)
            {
                await _ui.InvokeAsync(() =>
                {
                    var current = OrgDocument.Parse(doc.GetText(), _keywords, uri: doc.Uri);
                    var now = OrgBabel.All(current);
                    if (index < 0 || index >= now.Count || now[index].Code != block.Code) return; // the block changed meanwhile
                    doc.ApplyEdits(OrgText.WithEol(OrgBabel.Results(current, now[index], run.Output).Edits, doc.EndOfLine));
                }).ConfigureAwait(false);
            }
            else if (run.Output.Length > 0) channel?.Append(run.Output);
            if (run.TimedOut || run.ExitCode is not 0)
            {
                var message = run.TimedOut ? $"The {block.Language} block was stopped after {timeout.TotalSeconds:0} s (org.babel.timeout)"
                    : run.ExitCode is { } exit ? $"The {block.Language} block failed (exit code {exit})" : FirstLine(run.Error);
                var action = await Window.ShowMessageAsync(MessageSeverity.Warning, message, [new MessageAction("Show Output")]).ConfigureAwait(false);
                if (action == "Show Output" && Views is not null) await Views.ShowViewAsync("output").ConfigureAwait(false);
                return;
            }
        }
        Status(blocks.Count == 1 ? $"Ran the {blocks[0].Language} block" : $"Ran {blocks.Count} code blocks");

        static string FirstLine(string s) => s.Split('\n', 2)[0];
    }

    private async Task<string> WorkingDirectoryAsync(Uri file, string? dirHeader)
    {
        var folder = file.IsFile ? Path.GetDirectoryName(file.LocalPath) : null;
        var dir = dirHeader is { Length: > 0 } d ? OrgSettings.ExpandHome(d) : folder;
        if (dir is not null && !Path.IsPathRooted(dir) && folder is not null) dir = Path.Combine(folder, dir);
        if (dir is not null && _fs is not null && await _fs.StatAsync(new Uri(Path.GetFullPath(dir))).ConfigureAwait(false) is { Type: FileType.Directory }) return Path.GetFullPath(dir);
        return Path.GetTempPath();
    }

    // ------------------------------------------------------------------------------------------------ capture

    /// <summary>The capture templates (<c>org.capture.templates</c>, else Task, Note, Event and Journal).</summary>
    public IReadOnlyList<OrgCaptureTemplate> CaptureTemplatesList => OrgSettings.Templates(Config);

    /// <summary>
    /// Org: Capture: picks a template (or uses the one with <paramref name="key"/>), expands it (asking what its <c>%^</c> escapes ask for) and
    /// files the entry in the template's target, created when missing. With <c>%?</c> the file opens with the caret there. Captured from the
    /// agenda's day, a to-do entry without a date of its own is scheduled on that day.
    /// </summary>
    public async Task CaptureAsync(DateOnly? day = null, string? key = null)
    {
        if (Window is null || Store is null) return;
        var templates = CaptureTemplatesList;
        var template = key is null ? null : templates.FirstOrDefault(t => t.Key == key);
        if (template is null)
        {
            var pick = await Window.ShowQuickPickAsync([.. templates.Select(t => new QuickPickItem($"[{t.Key}] {t.Description}", TargetLabel(t), Tag: t))],
                new QuickPickOptions(Title: "Capture", Placeholder: day is { } d ? "Capture for " + d.ToString("ddd d MMM", CultureInfo.InvariantCulture) : "Pick a template")).ConfigureAwait(false);
            if (pick?.Tag is not OrgCaptureTemplate t) return;
            template = t;
        }
        var active = Navigation?.ActiveDocument;
        var context = new OrgCaptureContext(Now(), day, active?.Uri is { IsFile: true } au ? au.LocalPath : null, Navigation?.ActiveCaret?.Line);
        var entry = await OrgCapture.ExpandAsync(template.Template, context, new CapturePrompts(this)).ConfigureAwait(false);
        if (entry is null) return;
        if (day is { } sd) entry = ScheduleOn(entry, sd);

        var file = TemplateFile(template);
        var uri = new Uri(file);
        try
        {
            await Store.EnsureFileAsync(uri, $"#+TITLE: {CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Path.GetFileNameWithoutExtension(file))}\n\n").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ShowErrorAsync($"Could not create {file}: {ex.Message}").ConfigureAwait(false);
            return;
        }
        var result = await EditFileAsync(uri, doc => OrgCapture.Insert(doc, template, entry, day ?? Today)).ConfigureAwait(false);
        if (result is null) { await ShowErrorAsync($"Could not write {file}").ConfigureAwait(false); return; }
        Status($"Captured to {Path.GetFileName(file)}");
        if (entry.Cursor is not null && result.Caret is { } caret && Navigation is not null)
            await Navigation.OpenAsync(uri, new TextRange(caret, caret)).ConfigureAwait(false);
        await Store.RefreshAsync().ConfigureAwait(false);
    }

    private string TemplateFile(OrgCaptureTemplate template)
    {
        if (template.File is not { Length: > 0 } f) return OrgSettings.NotesFile(Config);
        var path = OrgSettings.ExpandHome(f);
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(OrgSettings.OrgDirectory(Config), path));
    }

    private string TargetLabel(OrgCaptureTemplate t)
    {
        var file = Path.GetFileName(TemplateFile(t));
        return t.DateTree ? file + ", by date" : t.Headline is { } h ? $"{file} › {h}" : file;
    }

    // a to-do heading captured for a day of the agenda, with no date of its own: scheduled on that day
    private OrgCaptureEntry ScheduleOn(OrgCaptureEntry entry, DateOnly day)
    {
        var lines = entry.Text.Split('\n');
        var first = OrgDocument.Parse(lines[0], _keywords);
        if (first.Headings.Count == 0 || !first.Headings[0].IsTodo || entry.Text.Contains("SCHEDULED:", StringComparison.Ordinal)
            || entry.Text.Contains("DEADLINE:", StringComparison.Ordinal) || OrgTimestamp.FindAll(entry.Text).Any(t => t.Timestamp.Active))
            return entry;
        var insert = "SCHEDULED: " + new OrgTimestamp(true, day) + "\n";
        var at = lines[0].Length + 1;
        return new OrgCaptureEntry(entry.Text[..Math.Min(at, entry.Text.Length)] + insert + (at < entry.Text.Length ? entry.Text[at..] : ""),
            entry.Cursor is { } c && c >= at ? c + insert.Length : entry.Cursor);
    }

    private sealed class CapturePrompts(OrgHost host) : IOrgCapturePrompts
    {
        public async Task<string?> TextAsync(string prompt, string? defaultValue, IReadOnlyList<string> options)
        {
            if (host.Window is null) return null;
            if (options.Count > 1)
            {
                var pick = await host.Window.ShowQuickPickAsync([.. options.Select(o => new QuickPickItem(o))], new QuickPickOptions(Title: prompt)).ConfigureAwait(false);
                return pick?.Label;
            }
            return await host.Window.ShowInputBoxAsync(new InputBoxOptions(Title: prompt, Value: defaultValue)).ConfigureAwait(false);
        }

        public async Task<OrgTimestamp?> DateAsync(string prompt, bool withTime)
        {
            if (host.Window is null) return null;
            var text = await host.Window.ShowInputBoxAsync(new InputBoxOptions(Title: prompt,
                Prompt: withTime ? "Date and time: tomorrow 10:00, fri 2pm-3pm, nov 20 9am (empty: today)" : "Date: tomorrow, fri, +3d, nov 20 (empty: today)",
                Validate: s => string.IsNullOrWhiteSpace(s) || OrgDateInput.Parse(s, host.Today, out _) is not null ? null : host.Error(s))).ConfigureAwait(false);
            if (text is null) return null;
            return OrgDateInput.Parse(text, host.Today, out _) ?? new OrgTimestamp(true, host.Today);
        }

        public Task<IReadOnlyList<string>?> TagsAsync() => host.PromptTagsAsync([]);
    }
}
