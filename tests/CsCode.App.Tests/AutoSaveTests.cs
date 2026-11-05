using CsCode.App.Docking;
using CsCode.App.Services;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>
/// <c>files.autoSave</c> (<see cref="AutoSave"/>) over the document well with document doubles and a clock the test advances: each mode, the delay
/// starting again on every edit, untitled and read-only documents left alone, the setting read live and per language, closing, failures,
/// and a closed tab's document released. The real save path (no format on save after a delay, the carets' lines, the "changed on disk"
/// question) is <see cref="AutoSaveRealAppTests"/>.
/// </summary>
public class AutoSaveTests
{
    private sealed class Rig : IDisposable
    {
        public WellRig Well { get; } = new();
        public TestConfigurationService Config { get; } = new();
        public SteppingTimeProvider Time { get; } = new();
        public RecordingWindowService Windows { get; } = new();
        public AutoSave AutoSave { get; }

        public Rig(string mode, int? delayMs = null)
        {
            Config.Set(SettingKeys.FilesAutoSave, mode);
            if (delayMs is { } d) Config.Set(SettingKeys.FilesAutoSaveDelay, d);
            AutoSave = new AutoSave(Well.Well, Config, new ImmediateUiDispatcher(), Time, Windows);
        }

        public async Task<(EditorTab Tab, FakeEditorDocument Doc)> Open(string name)
        {
            var tab = await Well.Open(name);
            return (tab, Well.Editors.Doc(tab.Uri));
        }

        public void Advance(int ms) => Time.Advance(TimeSpan.FromMilliseconds(ms));

        public void Dispose()
        {
            AutoSave.Dispose();
            Well.Dispose();
        }
    }

    private static void Edit(IEditorDocument doc) => doc.ApplyEdits([new TextEdit(TextRange.At(0, 0), "x")]);

    [Fact]
    public async Task After_delay_saves_once_the_delay_passed_after_the_last_edit_and_each_edit_starts_it_again()
    {
        using var rig = new Rig("afterDelay");
        var (_, doc) = await rig.Open("a.cs");

        Edit(doc);
        rig.Advance(600);
        Assert.Equal(0, doc.SaveCount);
        Edit(doc); // the wait starts again
        rig.Advance(600);
        Assert.Equal(0, doc.SaveCount);
        rig.Advance(400);
        Assert.Equal(1, doc.SaveCount);
        Assert.Equal([DocumentSaveReason.AfterDelay], doc.SaveReasons);
        Assert.False(doc.IsDirty);

        rig.Advance(5000); // nothing more without an edit
        Assert.Equal(1, doc.SaveCount);
    }

    [Fact]
    public async Task After_delay_follows_files_autoSaveDelay_live()
    {
        using var rig = new Rig("afterDelay", delayMs: 200);
        var (_, doc) = await rig.Open("a.cs");
        Edit(doc);
        rig.Advance(200);
        Assert.Equal(1, doc.SaveCount);

        rig.Config.Set(SettingKeys.FilesAutoSaveDelay, 3000);
        Edit(doc);
        rig.Advance(2000);
        Assert.Equal(1, doc.SaveCount);
        rig.Advance(1000);
        Assert.Equal(2, doc.SaveCount);
    }

    [Fact]
    public async Task Untitled_and_read_only_documents_are_never_auto_saved()
    {
        using var rig = new Rig("afterDelay");
        var untitled = rig.Well.Well.OpenUntitled();
        var readOnly = await rig.Open("ro.cs");
        readOnly.Doc.IsReadOnly = true;

        Edit(untitled.Doc);
        Edit(readOnly.Doc);
        rig.Advance(2000);
        rig.AutoSave.WindowDeactivated();
        rig.Config.Set(SettingKeys.FilesAutoSave, "onFocusChange"); // turning it on saves the dirty documents: not these
        rig.AutoSave.FocusMoved(untitled);
        rig.AutoSave.FocusMoved(null);

        Assert.Equal(0, rig.Well.Editors.Doc(untitled.Uri).SaveCount);
        Assert.Null(rig.Well.Editors.Doc(untitled.Uri).SavedAs); // no Save As dialog either
        Assert.Equal(0, readOnly.Doc.SaveCount);
        Assert.True(untitled.Doc.IsDirty);
    }

    [Fact]
    public async Task On_focus_change_saves_the_editor_that_lost_the_keyboard_or_stopped_being_the_active_one()
    {
        using var rig = new Rig("onFocusChange");
        var (a, docA) = await rig.Open("a.cs");
        var (b, docB) = await rig.Open("b.cs"); // b is active now

        Edit(docB);
        rig.Advance(5000);
        Assert.Equal(0, docB.SaveCount); // no delay in this mode
        rig.AutoSave.FocusMoved(b);
        rig.AutoSave.FocusMoved(b); // the keyboard moving inside the editor is not a change
        Assert.Equal(0, docB.SaveCount);
        rig.AutoSave.FocusMoved(null); // to a tool window, the palette...
        Assert.Equal([DocumentSaveReason.FocusOut], docB.SaveReasons);

        Edit(docB);
        rig.Well.Well.Activate(a); // another editor becomes the active one
        Assert.Equal(2, docB.SaveCount);
        Assert.Equal(0, docA.SaveCount); // clean: nothing to save

        Edit(docA);
        rig.AutoSave.WindowDeactivated(); // and every dirty document when the window loses activation
        Assert.Equal(1, docA.SaveCount);
    }

    [Fact]
    public async Task On_window_change_saves_every_dirty_document_when_a_window_loses_activation_and_nothing_else()
    {
        using var rig = new Rig("onWindowChange");
        var (a, docA) = await rig.Open("a.cs");
        var (_, docB) = await rig.Open("b.cs");
        var (_, clean) = await rig.Open("c.cs");
        Edit(docA);
        Edit(docB);

        rig.Advance(5000);
        rig.AutoSave.FocusMoved(a);
        rig.AutoSave.FocusMoved(null);
        rig.Well.Well.Activate(a);
        Assert.Equal(0, docA.SaveCount + docB.SaveCount);

        rig.AutoSave.WindowDeactivated();
        Assert.Equal([DocumentSaveReason.FocusOut], docA.SaveReasons);
        Assert.Equal([DocumentSaveReason.FocusOut], docB.SaveReasons);
        Assert.Equal(0, clean.SaveCount);
    }

    [Fact]
    public async Task Turning_auto_save_on_saves_the_dirty_documents_and_turning_it_off_stops_a_pending_save()
    {
        using var rig = new Rig("off");
        var (_, doc) = await rig.Open("a.cs");
        Edit(doc);
        rig.Advance(5000);
        rig.AutoSave.WindowDeactivated();
        Assert.Equal(0, doc.SaveCount);

        rig.Config.Set(SettingKeys.FilesAutoSave, "afterDelay"); // VS Code saves at once when auto save is turned on
        Assert.Equal(1, doc.SaveCount);

        Edit(doc);
        rig.Advance(500);
        rig.Config.Set(SettingKeys.FilesAutoSave, "off");
        rig.Advance(5000);
        Assert.Equal(1, doc.SaveCount);
        Assert.True(doc.IsDirty);
    }

    [Fact]
    public async Task A_language_section_sets_auto_save_for_its_documents_only()
    {
        using var rig = new Rig("off");
        rig.Config.Set("[python]", new Dictionary<string, object> { ["files.autoSave"] = "afterDelay" });
        var (_, cs) = await rig.Open("a.cs");
        var (pyTab, py) = await rig.Open("b.py");
        py.SetLanguageId("python"); // (the double has no language detection: the mode follows the language when it changes)

        Edit(cs);
        Edit(py);
        rig.Advance(1000);
        Assert.Equal(0, cs.SaveCount);
        Assert.Equal(1, py.SaveCount);
        Assert.Equal(AutoSave.Mode.AfterDelay, rig.AutoSave.ModeOf(pyTab.Doc));
    }

    [Fact]
    public async Task Closing_a_dirty_tab_saves_it_instead_of_asking_when_it_auto_saves_on_focus_change()
    {
        using var rig = new Rig("onFocusChange");
        var (tab, doc) = await rig.Open("a.cs");
        Edit(doc);
        Assert.True(await rig.Well.Well.CloseAsync(tab));
        Assert.Empty(rig.Well.Dialogs.Shown);
        Assert.Equal(1, doc.SaveCount);

        rig.Config.Set(SettingKeys.FilesAutoSave, "onWindowChange"); // this one asks, as VS Code does (its question does not leave the window)
        var (other, otherDoc) = await rig.Open("b.cs");
        Edit(otherDoc);
        rig.Well.Dialogs.Answers.Enqueue("Don't Save");
        Assert.True(await rig.Well.Well.CloseAsync(other));
        Assert.Single(rig.Well.Dialogs.Shown);
        Assert.Equal(0, otherDoc.SaveCount);
    }

    [Fact]
    public async Task A_failing_auto_save_is_reported_once_until_a_save_succeeds()
    {
        using var rig = new Rig("afterDelay");
        var (_, doc) = await rig.Open("a.cs");
        doc.SaveFailure = new IOException("disk full");
        for (var i = 0; i < 3; i++)
        {
            Edit(doc);
            rig.Advance(1000);
        }
        Assert.Single(rig.Windows.Messages);
        Assert.Contains("disk full", rig.Windows.Messages[0].Message);
        Assert.True(doc.IsDirty);

        doc.SaveFailure = null;
        Edit(doc);
        rig.Advance(1000);
        Assert.False(doc.IsDirty);
    }

    [Fact]
    public async Task A_closed_tab_releases_its_document_and_its_timer()
    {
        using var rig = new Rig("afterDelay");
        await LeakAssert.NotRetainedAsync(async () =>
        {
            var (tab, doc) = await rig.Open("a.cs");
            Edit(doc); // a timer now waits for it
            rig.Well.Dialogs.Answers.Enqueue("Don't Save");
            rig.Config.Set(SettingKeys.FilesAutoSave, "afterDelay");
            doc.SaveFailure = new IOException("not now"); // (closing saves with a short delay: let it fail and ask)
            Assert.True(await rig.Well.Well.CloseAsync(tab));
            return [doc, tab];
        });
        Assert.Equal(0, rig.AutoSave.TrackedCount);
        Assert.Equal(0, rig.Time.LiveTimers);
    }
}

/// <summary>A clock for timers that fire only when the test advances it; <see cref="ITimer.Change"/> re-arms them (the auto-save delay restarts).</summary>
internal sealed class SteppingTimeProvider : TimeProvider
{
    private readonly List<SteppingTimer> _timers = new();
    private TimeSpan _now;

    public int LiveTimers => _timers.Count;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new SteppingTimer(this, callback, state);
        _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        _now += by;
        foreach (var t in _timers.ToArray())
            if (t.Due is { } due && due <= _now)
            {
                t.Due = null;
                t.Callback(t.State);
            }
    }

    private sealed class SteppingTimer(SteppingTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback => callback;
        public object? State => state;
        public TimeSpan? Due { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            return true;
        }

        public void Dispose() => owner._timers.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
