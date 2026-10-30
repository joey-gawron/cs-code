using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using CsCode.App.Background;
using CsCode.App.Notifications;
using CsCode.App.Tests.Hosting;
using CsCode.Core.Platform;
using CsCode.Core.Terminal;
using CsCode.Panels.Terminal;
using CsCode.Panels.Terminal.Views;
using CsCode.Sessions;
using CsCode.Sessions.Ipc;
using CsCode.Terminal.Pty;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests.Background;

/// <summary>
/// A session process's quit whose window close the terminal holds (<c>terminal.integrated.confirmOnExit</c>: the question comes after the save
/// prompts, and the terminal closes the window again when the user agrees). Over the real composition (background host, shell, terminal module)
/// with idle fake PTYs: the quit stays pending while the question is up, commits when the window really closes (the "Don't Save" backups gone,
/// the session file as it was before the prompts) and is undone only when the user declines and works on.
/// </summary>
public class HeldQuitTests
{
    /// <summary>A terminal program that runs until the test ends it (never a real shell).</summary>
    private sealed class IdlePty : IPtyHost
    {
        private IPtyListener? _listener;

        /// <summary>The terminal's foreground process: the shell it started with, or a program it runs (confirmOnExit "hasChildProcesses").</summary>
        public volatile string Foreground = "sh";

        public int ProcessId => 4343;
        public bool HasExited { get; private set; }
        public int? ExitCode { get; private set; }
        public long PendingWriteBytes => 0;
        public long MaxPendingWriteBytes => 1 << 20;
        public void Start(PtyStartInfo info, IPtyListener listener) => _listener = listener;
        public bool Write(ReadOnlySpan<byte> data, bool urgent = false) => true;
        public void Resize(int columns, int rows) { }
        public void Kill(TimeSpan? grace = null) { }
        public string? GetForegroundProcessName() => Foreground;

        public void Exit()
        {
            if (HasExited) return;
            HasExited = true;
            ExitCode = 0;
            _listener?.OnExited(0);
        }

        public void Dispose() => _listener = null;
    }

    /// <summary>The PTYs the session's terminals got.</summary>
    private sealed class Ptys
    {
        private readonly List<IdlePty> _all = new();

        public int Count { get { lock (_all) return _all.Count; } }

        public IPtyHost Create()
        {
            var pty = new IdlePty();
            lock (_all) _all.Add(pty);
            return pty;
        }

        public void ExitAll()
        {
            lock (_all) foreach (var p in _all) p.Exit();
        }

        public void SetForeground(string program)
        {
            lock (_all) foreach (var p in _all) p.Foreground = program;
        }
    }

    /// <summary>Ends the session's terminals (disposed before the session: its teardown then closes the window without a question).</summary>
    private sealed class EndTerminals(InProcessSession s, Ptys ptys) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            ptys.ExitAll();
            if (s.Run.Session.Ended.IsCompleted) return;
            var terminals = s.Run.Services.GetRequiredService<ITerminalService>();
            await DevHost.WaitFor(() => terminals.Terminals.All(t => t.HasExited), 10_000); // marked on the terminal's own lane
        }
    }

    private const string ConfirmAlways = "\"terminal.integrated.confirmOnExit\": \"always\"";
    private const string ConfirmWithChildProcesses = "\"terminal.integrated.confirmOnExit\": \"hasChildProcesses\"";

    private static Task<InProcessSession> StartAsync(string session, Ptys ptys, string? settings = null, string confirm = ConfirmAlways) =>
        InProcessSession.StartAsync(session, settings is null ? confirm : confirm + ", " + settings,
            configure: s => s.AddSingleton(sp => CsCode.Terminal.ServiceCollectionExtensions.CreateService(sp, ptys.Create)));

    private static IpcMessage Show(params string[] paths) => new() { Type = IpcVerbs.Show, Paths = paths.Select(p => new IpcPath(p)).ToArray() };

    private static IpcMessage Edit(string path, string text) => new() { Type = IpcVerbs.TestEdit, Uri = new Uri(path).ToString(), Line = 0, Column = 0, Text = text };

    private static readonly IpcMessage Quit = new() { Type = IpcVerbs.Quit, Force = false };

    /// <summary>Opens a terminal in the session's window (the Terminal tool window shows it: the window's close is watched from then on).</summary>
    private static async Task OpenTerminalAsync(InProcessSession s, Ptys ptys)
    {
        await s.Run.Services.GetRequiredService<ICommandService>().ExecuteAsync(TerminalCommandSet.New);
        Assert.True(await DevHost.WaitFor(() => ptys.Count == 1 && s.Run.Session.Window!.GetVisualDescendants().OfType<TerminalView>().Any(), 20_000),
            "the terminal did not open in the window");
        await Task.Delay(100);
    }

    private static NotificationItem? Question(InProcessSession s) =>
        s.Run.Services.GetRequiredService<NotificationCenter>().Toasts.FirstOrDefault(t => t.Message.Contains("terminate the active terminal session", StringComparison.Ordinal)); // the newest

    private static async Task<NotificationItem> QuestionAsync(InProcessSession s)
    {
        Assert.True(await DevHost.WaitFor(() => Question(s) is not null, 20_000), "the terminal did not ask before it was ended");
        return Question(s)!;
    }

    private static void Answer(NotificationItem question, string action) => question.InvokeCommand.Execute(question.Actions.First(a => a.Title == action));

    /// <summary>The terminal's questions that wait for an answer.</summary>
    private static int OpenQuestions(InProcessSession s) =>
        s.Run.Services.GetRequiredService<NotificationCenter>().History.Count(n => !n.IsCompleted && n.Message.Contains("terminate the active terminal session", StringComparison.Ordinal));

    /// <summary>The user closes the window: the platform's close (its title bar's X), not a programmatic <c>Close()</c>.</summary>
    private static void UserCloses(InProcessSession s)
    {
        var impl = (object)s.Run.Session.Window!.PlatformImpl!;
        var closing = impl.GetType().GetInterfaces().Prepend(impl.GetType())
            .Select(t => t.GetProperty("Closing", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
            .FirstOrDefault(p => p?.PropertyType == typeof(Func<Avalonia.Controls.WindowCloseReason, bool>))
            ?? throw new InvalidOperationException("the headless window has no Closing callback");
        var cancelled = ((Func<Avalonia.Controls.WindowCloseReason, bool>)closing.GetValue(impl)!).Invoke(Avalonia.Controls.WindowCloseReason.WindowClosing);
        Assert.True(cancelled, "the user's close went ahead");
    }

    /// <summary>How many questions the terminal asks from now on (counted also once the session has ended).</summary>
    private static Func<int> CountQuestions(InProcessSession s)
    {
        var count = 0;
        s.Run.Services.GetRequiredService<NotificationCenter>().History.CollectionChanged += (_, e) =>
            count += e.NewItems?.OfType<NotificationItem>().Count(n => n.Message.Contains("terminate the active terminal session", StringComparison.Ordinal)) ?? 0;
        return () => count;
    }

    /// <summary>Whether the window was ever shown from now on (a hidden session's quit must not flash it).</summary>
    private static Func<bool> WatchShown(InProcessSession s)
    {
        var shown = false;
        s.Run.Session.Window!.PropertyChanged += (_, e) =>
        {
            if (e.Property == Avalonia.Visual.IsVisibleProperty && e.NewValue is true) shown = true;
        };
        return () => shown;
    }

    private static string[] SavedEditors(InProcessSession s, string session) =>
        s.Store.Load(session).Session!.Editors.Select(e => Path.GetFileName(new Uri(e.Uri).LocalPath)).Order(StringComparer.Ordinal).ToArray();

    /// <summary>Waits while the UI keeps running (the test runs on the UI thread).</summary>
    private static async Task PastTheAutosaveDelayAsync() => await Task.Delay(2500);

    [AvaloniaFact(Timeout = 180_000)]
    public async Task Dont_save_then_terminate_quits_with_the_session_as_it_was_before_the_prompts()
    {
        // The finding: RequestExitAsync said false for the held close, the quit ran Undo ("the quit was cancelled"), the autosave then wrote the
        // session with no tabs, and Terminate closed the window without CommitQuitAsync (and marked the session resident).
        var ptys = new Ptys();
        await using var s = await StartAsync("h", ptys, "\"files.hotExit\": \"off\"");
        await using var endTerminals = new EndTerminals(s, ptys);
        var a = s.WriteFile("a.txt", "original\n");
        var b = s.WriteFile("b.txt", "beta\n");
        await s.MustAsync(Show(a, b));
        await s.MustAsync(Edit(a, "discard me "));
        Assert.True(await DevHost.WaitFor(() => s.Store.Backups.List("h").Count == 1, 15_000), "the autosave wrote no backup");
        await OpenTerminalAsync(s, ptys);

        s.Dialogs.Answers.Enqueue("Don't Save");
        var quit = await s.RequestAsync(Quit, 60);
        var question = await QuestionAsync(s);
        Assert.Equal(SessionWindowController.QuitHeldMessage, quit.Error); // not done, not cancelled: the window's close waits for the answer
        Assert.Contains(s.Dialogs.Shown, d => d.Buttons.Contains("Don't Save"));
        var hello = await Task.Run(() => ResidentDiscovery.Probe(s.Runtime, Environment.ProcessId, TimeSpan.FromSeconds(2)));
        Assert.Equal(ProcessStates.Shown, hello!.State);
        await s.MustAsync(Show()); // a show goes ahead into the window that asks

        // The user takes a while to answer: nothing is written over the session the quit saved before its prompts, and the discarded text has no
        // backup left (whatever the answer: the tab is closed).
        await PastTheAutosaveDelayAsync();
        Assert.Empty(s.Store.Backups.List("h"));
        Assert.Equal(["a.txt", "b.txt"], SavedEditors(s, "h"));
        Assert.False(s.Store.Load("h").Session!.WasResident);
        Assert.True(FileLock.IsHeldElsewhere(s.Store.LockPathOf("h")), "the quit committed before the window closed");
        Assert.False(s.Run.Session.Ended.IsCompleted);

        // Closing the window meanwhile asks again; the quit is still pending.
        await s.MustAsync(new IpcMessage { Type = IpcVerbs.TestCloseWindow });
        Assert.True(await DevHost.WaitFor(() => Question(s) is { } q && !ReferenceEquals(q, question), 20_000), "the window's close did not ask again");
        Assert.True((await s.InspectAsync()).Visible);
        Assert.Equal(["a.txt", "b.txt"], SavedEditors(s, "h"));
        question = Question(s)!;

        Answer(question, "Terminate");
        await s.Run.Session.Ended.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Empty(s.Store.Backups.List("h"));
        Assert.Equal(["a.txt", "b.txt"], SavedEditors(s, "h"));
        Assert.All(s.Store.Load("h").Session!.Editors, e => Assert.Null(e.BackupId));
        Assert.False(s.Store.Load("h").Session!.WasResident, "a session the user quit is not restored at the next login");
        Assert.False(FileLock.IsHeldElsewhere(s.Store.LockPathOf("h")));
        Assert.Equal("original\n", File.ReadAllText(a));
    }

    [AvaloniaFact(Timeout = 180_000)]
    public async Task Dont_save_then_an_immediate_terminate_leaves_no_backup_to_bring_the_discarded_text_back()
    {
        var ptys = new Ptys();
        await using var s = await StartAsync("i", ptys, "\"files.hotExit\": \"off\"");
        await using var endTerminals = new EndTerminals(s, ptys);
        var a = s.WriteFile("a.txt", "original\n");
        await s.MustAsync(Show(a));
        await s.MustAsync(Edit(a, "discard me "));
        Assert.True(await DevHost.WaitFor(() => s.Store.Backups.List("i").Count == 1, 15_000), "the autosave wrote no backup");
        await OpenTerminalAsync(s, ptys);

        s.Dialogs.Answers.Enqueue("Don't Save");
        _ = await s.RequestAsync(Quit, 60);
        Answer(await QuestionAsync(s), "Terminate");
        await s.Run.Session.Ended.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Empty(s.Store.Backups.List("i"));
        Assert.Equal(["a.txt"], SavedEditors(s, "i"));
        Assert.False(FileLock.IsHeldElsewhere(s.Store.LockPathOf("i")));
    }

    [AvaloniaFact(Timeout = 180_000)]
    public async Task Save_then_terminate_writes_the_file_and_quits()
    {
        var ptys = new Ptys();
        await using var s = await StartAsync("v", ptys, "\"files.hotExit\": \"off\"");
        await using var endTerminals = new EndTerminals(s, ptys);
        var a = s.WriteFile("a.txt", "original\n");
        await s.MustAsync(Show(a));
        await s.MustAsync(Edit(a, "keep me "));
        await OpenTerminalAsync(s, ptys);

        s.Dialogs.Answers.Enqueue("Save");
        var quit = await s.RequestAsync(Quit, 60);
        Assert.Equal(SessionWindowController.QuitHeldMessage, quit.Error);
        Assert.Equal("keep me original\n", File.ReadAllText(a));
        Answer(await QuestionAsync(s), "Terminate");
        await s.Run.Session.Ended.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Empty(s.Store.Backups.List("v"));
        Assert.Equal(["a.txt"], SavedEditors(s, "v"));
        Assert.False(s.Store.Load("v").Session!.WasResident);
        Assert.False(FileLock.IsHeldElsewhere(s.Store.LockPathOf("v")));
    }

    [AvaloniaFact(Timeout = 180_000)]
    public async Task Dont_save_then_cancel_keeps_the_session_working_and_the_next_quit_asks_again()
    {
        var ptys = new Ptys();
        await using var s = await StartAsync("c", ptys, "\"files.hotExit\": \"off\"");
        await using var endTerminals = new EndTerminals(s, ptys);
        var a = s.WriteFile("a.txt", "original\n");
        var b = s.WriteFile("b.txt", "beta\n");
        await s.MustAsync(Show(a));
        await s.MustAsync(Edit(a, "discard me "));
        await OpenTerminalAsync(s, ptys);

        s.Dialogs.Answers.Enqueue("Don't Save");
        _ = await s.RequestAsync(Quit, 60);
        var first = await QuestionAsync(s);
        Answer(first, "Cancel");
        await Task.Delay(300);
        Assert.False(s.Run.Session.Ended.IsCompleted);
        Assert.True(FileLock.IsHeldElsewhere(s.Store.LockPathOf("c")));
        var state = await s.InspectAsync();
        Assert.True(state.Visible);
        Assert.Equal(ProcessStates.Shown, state.State);

        // The user works on: the session is saved as it is now (hot-exit backups for crash recovery included), and it counts as resident again.
        var shell = s.Run.Services.GetRequiredService<ShellController>();
        await shell.Well.OpenAsync(new Uri(b));
        await s.MustAsync(Edit(b, "unsaved "));
        Assert.True(await DevHost.WaitFor(() => s.Store.Backups.List("c").Count == 1 && SavedEditors(s, "c").SequenceEqual(["b.txt"]), 15_000),
            "the autosave did not resume after the question was declined");
        Assert.True(s.Store.Load("c").Session!.WasResident);

        // Background mode's close hides the window again (the declined close is not taken up again by the next close).
        await s.MustAsync(new IpcMessage { Type = IpcVerbs.TestCloseWindow });
        Assert.True(await DevHost.WaitFor(() => s.Host.State == ProcessStates.Hidden, 10_000), "the window's close did not hide it: " + s.Host.State);
        Assert.False((await s.InspectAsync()).Visible);
        Assert.False(s.Run.Session.Ended.IsCompleted);
        await s.MustAsync(Show());

        // The next quit asks again: the save prompt, then the terminal.
        var prompts = s.Dialogs.Shown.Count;
        s.Dialogs.Answers.Enqueue("Don't Save");
        var again = await s.RequestAsync(Quit, 60);
        Assert.Equal(SessionWindowController.QuitHeldMessage, again.Error);
        Assert.Equal(prompts + 1, s.Dialogs.Shown.Count);
        Assert.True(await DevHost.WaitFor(() => Question(s) is { } q && !ReferenceEquals(q, first), 20_000), "the terminal did not ask again");
        Answer(Question(s)!, "Terminate");
        await s.Run.Session.Ended.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Empty(s.Store.Backups.List("c"));
        Assert.Equal(["b.txt"], SavedEditors(s, "c"));
        Assert.Equal("beta\n", File.ReadAllText(b));
        Assert.False(FileLock.IsHeldElsewhere(s.Store.LockPathOf("c")));
    }

    [AvaloniaFact(Timeout = 180_000)]
    public async Task A_hot_exit_quit_the_terminal_holds_lets_go_of_the_session_only_when_the_window_closes()
    {
        // The other quit paths committed (sealed the session, released its lock, said "quitting") before the window's close, which the terminal
        // then held: a declined question left a working window over a session nothing was written for any more, and that a launch opened again.
        var ptys = new Ptys();
        await using var s = await StartAsync("x", ptys);
        await using var endTerminals = new EndTerminals(s, ptys);
        var a = s.WriteFile("a.txt", "original\n");
        await s.MustAsync(Show(a));
        await s.MustAsync(Edit(a, "unsaved "));
        await OpenTerminalAsync(s, ptys);

        var quit = await s.RequestAsync(Quit, 60);
        var question = await QuestionAsync(s);
        Assert.Equal(SessionWindowController.QuitHeldMessage, quit.Error);
        Assert.True(FileLock.IsHeldElsewhere(s.Store.LockPathOf("x")), "the quit let go of the session while its window was still open");
        var hello = await Task.Run(() => ResidentDiscovery.Probe(s.Runtime, Environment.ProcessId, TimeSpan.FromSeconds(2)));
        Assert.Equal(ProcessStates.Shown, hello!.State);
        Assert.Single(s.Store.Backups.List("x")); // hot exit: written before the window's close

        Answer(question, "Terminate");
        await s.Run.Session.Ended.WaitAsync(TimeSpan.FromSeconds(30));
        var backup = Assert.Single(s.Store.Backups.List("x"));
        Assert.Equal("unsaved original\n", s.Store.Backups.ReadText("x", backup.Id));
        Assert.Equal(["a.txt"], SavedEditors(s, "x"));
        Assert.False(s.Store.Load("x").Session!.WasResident);
        Assert.False(FileLock.IsHeldElsewhere(s.Store.LockPathOf("x")));
    }

    [AvaloniaFact(Timeout = 180_000)]
    public async Task An_eviction_or_a_forced_quit_the_terminal_would_hold_is_refused_and_the_hidden_session_keeps_running()
    {
        // An eviction committed, then the terminal held the hidden window's close with a question nobody could see: the process stayed, hidden,
        // over a session it no longer wrote or owned.
        var ptys = new Ptys();
        await using var s = await StartAsync("e", ptys);
        await using var endTerminals = new EndTerminals(s, ptys);
        var a = s.WriteFile("a.txt", "a\n");
        await s.MustAsync(Show(a));
        await s.MustAsync(Edit(a, "unsaved "));
        await OpenTerminalAsync(s, ptys);
        await s.MustAsync(new IpcMessage { Type = IpcVerbs.Hide });

        var evict = await s.RequestAsync(new IpcMessage { Type = IpcVerbs.Quit, Force = false, Evict = true });
        Assert.NotEqual(true, evict.Ok);
        Assert.Contains("not evicted", evict.Error);
        var forced = await s.RequestAsync(new IpcMessage { Type = IpcVerbs.Quit, Force = true });
        Assert.NotEqual(true, forced.Ok);
        Assert.Equal(0, OpenQuestions(s)); // withdrawn: nobody could see it, and an answer to it later would have hidden or closed the window

        await Task.Delay(300);
        Assert.False(s.Run.Session.Ended.IsCompleted);
        Assert.True(FileLock.IsHeldElsewhere(s.Store.LockPathOf("e")), "a refused quit let go of the session");
        var state = await s.InspectAsync();
        Assert.False(state.Visible);
        Assert.Equal(ProcessStates.Hidden, state.State);
        var hello = await Task.Run(() => ResidentDiscovery.Probe(s.Runtime, Environment.ProcessId, TimeSpan.FromSeconds(2)));
        Assert.True(hello!.NeedsPrompt, "the next eviction would ask again");

        // It still works: shown, edited, saved by the autosave.
        await s.MustAsync(Show(a));
        await s.MustAsync(Edit(a, "more "));
        Assert.True(await DevHost.WaitFor(() => s.Store.Backups.List("e") is [var only] && s.Store.Backups.ReadText("e", only.Id) == "more unsaved a\n", 15_000),
            "the autosave did not go on after the refused quits");
    }

    [AvaloniaFact(Timeout = 180_000)]
    public async Task A_sigterm_quit_the_terminal_holds_is_saved_and_left_to_the_watchdog()
    {
        // SIGTERM: everything is backed up before the window's close; when the terminal holds it, the quit is not reported as committed, so the
        // host's watchdog writes what is left and ends the process (it used to trust an orderly shutdown that the question held up for good).
        var ptys = new Ptys();
        await using var s = await StartAsync("t", ptys);
        await using var endTerminals = new EndTerminals(s, ptys);
        var a = s.WriteFile("a.txt", "a\n");
        await s.MustAsync(Show(a));
        await s.MustAsync(Edit(a, "unsaved "));
        await OpenTerminalAsync(s, ptys);
        var controller = s.Host.Controller!;

        await Task.Run(controller.TerminateAsync);
        var question = await QuestionAsync(s);
        Assert.False(controller.QuitCommitted, "the watchdog would wait for a shutdown the question holds up");
        var backup = Assert.Single(s.Store.Backups.List("t"));
        Assert.Equal("unsaved a\n", s.Store.Backups.ReadText("t", backup.Id));
        Assert.True(s.Store.Load("t").Session!.WasResident, "a session ended by SIGTERM (logout) is restored at the next login");

        Answer(question, "Terminate");
        await s.Run.Session.Ended.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(controller.QuitCommitted);
        Assert.Single(s.Store.Backups.List("t"));
        Assert.True(s.Store.Load("t").Session!.WasResident);
    }

    // ------------------------------------------------------------------ "hasChildProcesses": a held close is not always a question

    /// <summary>
    /// A session with confirmOnExit "hasChildProcesses", a dirty document and an idle terminal (only the shell runs), hidden or not: the terminal
    /// holds the window's close while it looks for programs, then closes again without a word.
    /// </summary>
    private static async Task<(InProcessSession S, Ptys Ptys, string File)> StartWithChildProcessesAsync(string session, bool hidden)
    {
        var ptys = new Ptys();
        var s = await StartAsync(session, ptys, confirm: ConfirmWithChildProcesses);
        try
        {
            var a = s.WriteFile("a.txt", "a\n");
            await s.MustAsync(Show(a));
            await s.MustAsync(Edit(a, "unsaved "));
            await OpenTerminalAsync(s, ptys);
            if (hidden) await s.MustAsync(new IpcMessage { Type = IpcVerbs.Hide });
            return (s, ptys, a);
        }
        catch
        {
            await new EndTerminals(s, ptys).DisposeAsync();
            await s.DisposeAsync();
            throw;
        }
    }

    private static async Task AssertQuitWithHotExitAsync(InProcessSession s, string session, IpcMessage reply, Func<int> asked)
    {
        Assert.True(reply.Ok == true, reply.Error);
        await s.Run.Session.Ended.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, asked());
        var backup = Assert.Single(s.Store.Backups.List(session));
        Assert.Equal("unsaved a\n", s.Store.Backups.ReadText(session, backup.Id));
        Assert.Equal(["a.txt"], SavedEditors(s, session));
        Assert.False(FileLock.IsHeldElsewhere(s.Store.LockPathOf(session)));
    }

    [AvaloniaFact(Timeout = 180_000)]
    public async Task With_only_shells_running_a_forced_quit_of_a_shown_session_quits()
    {
        // The held close was taken for a question: the forced quit was refused ("did not quit"), and the terminal's close that followed hid the
        // window and left the terminal believing the user had agreed (confirmOnExit off for the rest of the process).
        var (s, ptys, _) = await StartWithChildProcessesAsync("f", hidden: false);
        await using var _ = s;
        await using var endTerminals = new EndTerminals(s, ptys);
        var asked = CountQuestions(s);
        var forced = await s.RequestAsync(new IpcMessage { Type = IpcVerbs.Quit, Force = true });
        await AssertQuitWithHotExitAsync(s, "f", forced, asked);
        Assert.False(s.Store.Load("f").Session!.WasResident);
    }

    [AvaloniaFact(Timeout = 180_000)]
    public async Task With_only_shells_running_an_eviction_quits_the_hidden_session()
    {
        // Refused before, and the session said needsPrompt from then on: never evicted again, maxResidentSessions no longer enforced.
        var (s, ptys, _) = await StartWithChildProcessesAsync("g", hidden: true);
        await using var _ = s;
        await using var endTerminals = new EndTerminals(s, ptys);
        var shown = WatchShown(s);
        var asked = CountQuestions(s);
        var hello = await Task.Run(() => ResidentDiscovery.Probe(s.Runtime, Environment.ProcessId, TimeSpan.FromSeconds(2)));
        Assert.False(hello!.NeedsPrompt);
        var evict = await s.RequestAsync(new IpcMessage { Type = IpcVerbs.Quit, Force = false, Evict = true });
        await AssertQuitWithHotExitAsync(s, "g", evict, asked);
        Assert.False(shown(), "the hidden window was shown");
    }

    [AvaloniaFact(Timeout = 180_000)]
    public async Task With_only_shells_running_a_quit_of_a_hidden_session_quits_without_showing_its_window()
    {
        // Exit All's quit: the hidden window was shown for a question that never came, then closed, and the session was listed as not quit.
        var (s, ptys, _) = await StartWithChildProcessesAsync("k", hidden: true);
        await using var _ = s;
        await using var endTerminals = new EndTerminals(s, ptys);
        var shown = WatchShown(s);
        var asked = CountQuestions(s);
        var quit = await s.RequestAsync(Quit, 60);
        await AssertQuitWithHotExitAsync(s, "k", quit, asked);
        Assert.False(shown(), "the hidden window was shown");
        Assert.False(s.Store.Load("k").Session!.WasResident);
    }

    [AvaloniaFact(Timeout = 180_000)]
    public async Task With_a_program_running_in_a_terminal_a_forced_quit_is_refused_and_its_question_withdrawn()
    {
        var (s, ptys, _) = await StartWithChildProcessesAsync("p", hidden: false);
        await using var _ = s;
        await using var endTerminals = new EndTerminals(s, ptys);
        ptys.SetForeground("vim");

        var forced = await s.RequestAsync(new IpcMessage { Type = IpcVerbs.Quit, Force = true });
        Assert.NotEqual(true, forced.Ok);
        Assert.Contains("not quitting", forced.Error);
        // Nobody may be asked: the question the close raised is gone (an answer to it later hid the window and switched confirmOnExit off).
        Assert.Equal(0, OpenQuestions(s));
        await Task.Delay(300);
        Assert.False(s.Run.Session.Ended.IsCompleted);
        var state = await s.InspectAsync();
        Assert.True(state.Visible);
        Assert.Equal(ProcessStates.Shown, state.State);
        Assert.True(FileLock.IsHeldElsewhere(s.Store.LockPathOf("p")));
        var hello = await Task.Run(() => ResidentDiscovery.Probe(s.Runtime, Environment.ProcessId, TimeSpan.FromSeconds(2)));
        Assert.True(hello!.NeedsPrompt, "the next eviction would ask");

        // The program ends: the next quit asks nothing and goes ahead (the withdrawn question left the terminal's confirmOnExit as it was).
        ptys.SetForeground("sh");
        var asked = CountQuestions(s);
        await AssertQuitWithHotExitAsync(s, "p", await s.RequestAsync(Quit, 60), asked);
    }

    [AvaloniaFact(Timeout = 180_000)]
    public async Task A_quit_of_a_hidden_session_whose_terminal_asks_shows_the_window_with_the_question()
    {
        var ptys = new Ptys();
        await using var s = await StartAsync("w", ptys);
        await using var endTerminals = new EndTerminals(s, ptys);
        var a = s.WriteFile("a.txt", "a\n");
        await s.MustAsync(Show(a));
        await s.MustAsync(Edit(a, "unsaved "));
        await OpenTerminalAsync(s, ptys);
        await s.MustAsync(new IpcMessage { Type = IpcVerbs.Hide });

        var quit = await s.RequestAsync(Quit, 60);
        Assert.Equal(SessionWindowController.QuitHeldMessage, quit.Error);
        var question = await QuestionAsync(s);
        await Task.Delay(300);
        var state = await s.InspectAsync();
        Assert.True(state.Visible, "the question is in a hidden window");
        Assert.Equal(ProcessStates.Shown, state.State);
        Assert.Equal(1, OpenQuestions(s)); // showing the window did not count as working on (which withdraws the question)

        Answer(question, "Terminate");
        await s.Run.Session.Ended.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Single(s.Store.Backups.List("w"));
        Assert.False(FileLock.IsHeldElsewhere(s.Store.LockPathOf("w")));
    }

    [AvaloniaFact(Timeout = 180_000)]
    public async Task A_held_hot_exit_quit_asks_again_without_save_prompts_when_the_user_closes_the_window_or_quits_again()
    {
        // The window's close or a new Quit Session while the question was up showed the Save prompts for every dirty document, which a hot-exit
        // quit never asks: after "Don't Save" and Terminate the quit kept the hot-exit backups, and the next start brought the discarded text back.
        // As in VS Code, a hot-exit close asked again is a hot-exit close again: the backups stay, and so does the text.
        var ptys = new Ptys();
        await using var s = await StartAsync("r", ptys);
        await using var endTerminals = new EndTerminals(s, ptys);
        var a = s.WriteFile("a.txt", "original\n");
        await s.MustAsync(Show(a));
        await s.MustAsync(Edit(a, "unsaved "));
        await OpenTerminalAsync(s, ptys);

        var quit = await s.RequestAsync(Quit, 60);
        Assert.Equal(SessionWindowController.QuitHeldMessage, quit.Error);
        var first = await QuestionAsync(s);

        UserCloses(s);
        Assert.True(await DevHost.WaitFor(() => Question(s) is { } q && !ReferenceEquals(q, first), 20_000), "the window's close did not ask again");
        Assert.True(first.IsCompleted, "the first question stayed up");
        Assert.Equal(1, OpenQuestions(s));
        var second = Question(s)!;

        s.Dialogs.Answers.Enqueue("Don't Save"); // would be taken by a Save prompt
        var again = await s.RequestAsync(Quit, 60);
        Assert.Equal(SessionWindowController.QuitHeldMessage, again.Error);
        Assert.True(await DevHost.WaitFor(() => Question(s) is { } q && !ReferenceEquals(q, second), 20_000), "Quit Session did not ask again");
        Assert.Equal(1, OpenQuestions(s));
        Assert.Empty(s.Dialogs.Shown); // no Save prompt: hot exit
        Assert.True((await s.InspectAsync()).Tabs.Single().Dirty);
        Assert.True(FileLock.IsHeldElsewhere(s.Store.LockPathOf("r")));

        Answer(Question(s)!, "Terminate");
        await s.Run.Session.Ended.WaitAsync(TimeSpan.FromSeconds(30));
        var backup = Assert.Single(s.Store.Backups.List("r"));
        Assert.Equal("unsaved original\n", s.Store.Backups.ReadText("r", backup.Id));
        Assert.Equal(backup.Id, Assert.Single(s.Store.Load("r").Session!.Editors).BackupId);
        Assert.False(s.Store.Load("r").Session!.WasResident);
        Assert.False(FileLock.IsHeldElsewhere(s.Store.LockPathOf("r")));
        Assert.Equal("original\n", File.ReadAllText(a));
    }

    [AvaloniaFact(Timeout = 180_000)]
    public async Task The_users_close_while_a_quits_question_is_up_asks_again_and_the_save_prompts_answers_still_hold()
    {
        // After "Don't Save", the user's close (the window's X) while the question is up starts the close over through the quit, which keeps its
        // decision: Terminate then leaves no backup of the discarded text.
        var ptys = new Ptys();
        await using var s = await StartAsync("u", ptys, "\"files.hotExit\": \"off\"");
        await using var endTerminals = new EndTerminals(s, ptys);
        var a = s.WriteFile("a.txt", "original\n");
        await s.MustAsync(Show(a));
        await s.MustAsync(Edit(a, "discard me "));
        await OpenTerminalAsync(s, ptys);

        s.Dialogs.Answers.Enqueue("Don't Save");
        _ = await s.RequestAsync(Quit, 60);
        var first = await QuestionAsync(s);
        UserCloses(s);
        Assert.True(await DevHost.WaitFor(() => Question(s) is { } q && !ReferenceEquals(q, first), 20_000), "the window's close did not ask again");
        Assert.Equal(1, OpenQuestions(s));
        Assert.Single(s.Dialogs.Shown); // the first quit's Save prompt only: nothing is dirty since

        Answer(Question(s)!, "Terminate");
        await s.Run.Session.Ended.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Empty(s.Store.Backups.List("u"));
        Assert.Equal(["a.txt"], SavedEditors(s, "u"));
        Assert.All(s.Store.Load("u").Session!.Editors, e => Assert.Null(e.BackupId));
        Assert.Equal("original\n", File.ReadAllText(a));
    }
}
