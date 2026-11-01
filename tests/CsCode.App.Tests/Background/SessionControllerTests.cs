using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using CsCode.App.Background;
using CsCode.App.Notifications;
using CsCode.App.Startup;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Sessions;
using CsCode.Sessions.Ipc;
using CsCode.Sessions.Launcher;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace CsCode.App.Tests.Background;

/// <summary>
/// A REAL session process's window side, run in the test process (headless): the <see cref="BackgroundHost"/> with a private runtime directory and
/// store, the real app and its modules, and the IPC requests the launcher sends. Save prompts are answered by <see cref="FakeModalDialogs"/>.
/// </summary>
internal sealed class InProcessSession : IAsyncDisposable
{
    private readonly BackgroundHost? _previous;

    private InProcessSession(RealAppHome env, RuntimeDirectory runtime, SessionStore store, BackgroundHost host, FakeModalDialogs dialogs, RealAppRun run, BackgroundHost? previous)
    {
        Env = env;
        Runtime = runtime;
        Store = store;
        Host = host;
        Dialogs = dialogs;
        Run = run;
        _previous = previous;
    }

    public RealAppHome Env { get; }
    public RuntimeDirectory Runtime { get; }
    public SessionStore Store { get; }
    public BackgroundHost Host { get; }
    public FakeModalDialogs Dialogs { get; }
    public RealAppRun Run { get; }

    /// <param name="settings">Extra user settings (JSON members, comma-separated).</param>
    /// <param name="seed">Writes the session's saved state before the process starts.</param>
    /// <param name="runtimeReady">Called with the runtime directory before the process starts (other "processes" answering there).</param>
    public static async Task<InProcessSession> StartAsync(string session, string? settings = null, Action<SessionStore>? seed = null, Action<IServiceCollection>? configure = null,
        Action<RuntimeDirectory>? runtimeReady = null)
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("Unix sockets");
        var env = new RealAppHome();
        env.WriteUserSettings($$"""{ "workbench.startupEditor": "none", "cscode.background.showTrayIcon": false{{(settings is null ? "" : ", " + settings)}} }""");
        var runtime = RuntimeDirectory.Resolve(name => name == "CSCODE_HOME" ? env.Home.Path : null);
        var store = new SessionStore(env.Home.Combine("data"));
        seed?.Invoke(store);
        runtimeReady?.Invoke(runtime);
        var host = BackgroundHost.Start(CommandLineOptions.Parse(["--host", "--session", session, "--headless-test"]), new CrashHandler(env.CrashLogs.Path), runtime, store, out var refused);
        Assert.Null(refused);
        Assert.NotNull(host);
        var previous = App.Background;
        App.Background = host;
        var dialogs = new FakeModalDialogs();
        var run = new RealAppRun(env, s =>
        {
            s.RemoveAll<IModalDialogs>();
            s.AddSingleton<IModalDialogs>(dialogs);
            configure?.Invoke(s);
        });
        var started = new InProcessSession(env, runtime, store, host, dialogs, run, previous);
        await run.MainWindowAsync();
        Assert.True(await DevHost.WaitFor(() => host.State == ProcessStates.Shown, 60_000), "not shown: " + host.State);
        return started;
    }

    public string Endpoint => Runtime.EndpointOf(Environment.ProcessId);

    /// <summary>A launcher's request, sent from a pool thread (the test runs on the UI thread, which serves it).</summary>
    public Task<IpcMessage> RequestAsync(IpcMessage request, int replySeconds = 30) =>
        Task.Run(() => IpcClient.Request(Endpoint, request, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(replySeconds)));

    public async Task MustAsync(IpcMessage request)
    {
        var reply = await RequestAsync(request);
        Assert.True(reply.Ok == true, $"{request.Type}: {reply.Error}");
    }

    public async Task<InspectedState> InspectAsync(bool withText = false)
    {
        var reply = await RequestAsync(new IpcMessage { Type = IpcVerbs.Inspect, Text = withText ? "text" : null });
        Assert.True(reply.Ok == true, reply.Error);
        return reply.Data!.Value.Deserialize<InspectedState>()!;
    }

    public string WriteFile(string name, string text)
    {
        var path = Path.Combine(Env.Workspace.Path, name);
        File.WriteAllText(path, text);
        return path;
    }

    public async ValueTask DisposeAsync()
    {
        try { await Run.DisposeAsync(); }
        finally
        {
            App.Background = _previous;
            Host.Dispose();
            Env.Dispose();
        }
    }
}

public class SessionControllerTests
{
    /// <summary>Polls an asynchronous condition (an IPC inspect: never block the UI thread that answers it).</summary>
    private static async Task<bool> UntilAsync(Func<Task<bool>> condition, int timeoutMs = 20_000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!await condition())
        {
            if (clock.ElapsedMilliseconds > timeoutMs) return false;
            await Task.Delay(50);
        }
        return true;
    }

    private static IpcMessage Show(params string[] paths) => new() { Type = IpcVerbs.Show, Paths = paths.Select(p => new IpcPath(p)).ToArray() };

    private static IpcMessage Edit(string path, string text) => new() { Type = IpcVerbs.TestEdit, Uri = new Uri(path).ToString(), Line = 0, Column = 0, Text = text };

    [AvaloniaFact(Timeout = 180_000)]
    public async Task A_session_process_registers_New_Window_and_offers_it_in_the_File_menu()
    {
        await using var s = await InProcessSession.StartAsync("new-window");
        Assert.True(s.Run.Services.GetRequiredService<ICommandService>().Has(SessionCommandIds.NewWindow));
        var menus = s.Run.Services.GetRequiredService<ShellController>().Menus;
        menus.Refresh();
        Assert.True(menus.Roots.Single(r => r.Header == "_File").Children.Single(c => c.Header == "New Win_dow").IsEnabled);
    }

    [AvaloniaFact(Timeout = 180_000)]
    public async Task Quitting_with_save_prompts_leaves_no_backup_to_bring_discarded_changes_back()
    {
        // sessions-2: with hot exit off the autosave still backs up (crash recovery, as VS Code does); after "Don't Save" those backups were left
        // behind and the next start restored the discarded text.
        await using var s = await InProcessSession.StartAsync("s", "\"files.hotExit\": \"off\"");
        var file = s.WriteFile("a.txt", "original\n");
        await s.MustAsync(Show(file));
        await s.MustAsync(Edit(file, "xyz "));
        Assert.True(await DevHost.WaitFor(() => s.Store.Backups.List("s").Count == 1, 15_000), "the autosave wrote no backup");

        s.Dialogs.Answers.Enqueue("Don't Save");
        var quit = await s.RequestAsync(new IpcMessage { Type = IpcVerbs.Quit, Force = false }, 60);
        Assert.True(quit.Ok == true, quit.Error);
        Assert.Contains(s.Dialogs.Shown, d => d.Buttons.Contains("Don't Save"));
        await s.Run.Session.Ended.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Empty(s.Store.Backups.List("s"));
        Assert.Equal("original\n", File.ReadAllText(file));
        Assert.Contains(s.Store.Load("s").Session!.Editors, e => e.Uri == new Uri(file).ToString() && e.BackupId is null);
    }

    /// <summary>Save prompts that wait for the test's answer.</summary>
    private sealed class WaitingDialogs : IModalDialogs
    {
        public TaskCompletionSource Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string?> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string?> ShowAsync(string title, string message, IReadOnlyList<string> buttons, int defaultIndex = 0)
        {
            Asked.TrySetResult();
            return Answer.Task;
        }
    }

    [AvaloniaFact(Timeout = 180_000)]
    public async Task A_quit_backs_up_the_latest_unsaved_changes_before_its_save_prompts()
    {
        // The autosave writes nothing while a quit is under way, and the quit's write before its prompts had no backups: text typed in the last
        // moments before Quit Session was on disk nowhere while a Save prompt waited, so SIGTERM or a crash then lost it.
        var dialogs = new WaitingDialogs();
        await using var s = await InProcessSession.StartAsync("b", "\"files.hotExit\": \"off\"", configure: c =>
        {
            c.RemoveAll<IModalDialogs>();
            c.AddSingleton<IModalDialogs>(dialogs);
        });
        var file = s.WriteFile("a.txt", "original\n");
        await s.MustAsync(Show(file));
        await s.MustAsync(Edit(file, "first "));
        Assert.True(await DevHost.WaitFor(() => s.Store.Backups.List("b") is [var b] && s.Store.Backups.ReadText("b", b.Id) == "first original\n", 15_000),
            "the autosave wrote no backup");

        await s.MustAsync(Edit(file, "last ")); // and Quit Session at once, before the autosave's delay
        var quit = s.RequestAsync(new IpcMessage { Type = IpcVerbs.Quit, Force = false }, 60);
        await dialogs.Asked.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var backup = Assert.Single(s.Store.Backups.List("b"));
        Assert.Equal("last first original\n", s.Store.Backups.ReadText("b", backup.Id));
        // The session file lists the tab, with no backup: the prompt decides the unsaved changes, and a quit that goes ahead leaves none.
        Assert.Contains(s.Store.Load("b").Session!.Editors, e => e.Uri == new Uri(file).ToString() && e.BackupId is null);

        dialogs.Answer.SetResult("Don't Save");
        Assert.True((await quit).Ok == true);
        await s.Run.Session.Ended.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Empty(s.Store.Backups.List("b"));
        Assert.Equal("original\n", File.ReadAllText(file));
    }

    [AvaloniaFact(Timeout = 180_000)]
    public async Task A_session_written_by_a_newer_version_asks_before_its_unsaved_changes_go_and_refuses_a_forced_quit()
    {
        // sessions-1: such a session must not be written (backups included), so its unsaved changes were silently dropped on quit and eviction.
        const string Newer = "{\"version\": 3, \"name\": \"x\", \"someNewField\": 1}";
        await using var s = await InProcessSession.StartAsync("x", seed: store =>
        {
            Directory.CreateDirectory(store.SessionsDir);
            File.WriteAllText(store.PathOf("x"), Newer);
        });
        var file = s.WriteFile("f.txt", "original\n");
        await s.MustAsync(Show(file));
        await s.MustAsync(Edit(file, "EDIT "));
        await Task.Delay(2500); // past the autosave delay: nothing may be written for this session
        Assert.Empty(s.Store.Backups.List("x"));

        // An eviction or `cscode --quit x` (forced) is refused: nobody could be asked about the changes.
        var evict = await s.RequestAsync(new IpcMessage { Type = IpcVerbs.Quit, Force = false, Evict = true });
        Assert.NotEqual(true, evict.Ok);
        var forced = await s.RequestAsync(new IpcMessage { Type = IpcVerbs.Quit, Force = true });
        Assert.NotEqual(true, forced.Ok);
        Assert.Contains("newer CsCode", forced.Error);
        Assert.Equal(ProcessStates.Shown, s.Host.State);
        Assert.True((await s.InspectAsync()).Visible);

        // Quit Session asks; "Save" writes the file. The newer version's session file stays exactly as it was.
        s.Dialogs.Answers.Enqueue("Save");
        var quit = await s.RequestAsync(new IpcMessage { Type = IpcVerbs.Quit, Force = false }, 60);
        Assert.True(quit.Ok == true, quit.Error);
        Assert.Contains(s.Dialogs.Shown, d => d.Buttons.Contains("Don't Save"));
        await s.Run.Session.Ended.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("EDIT original\n", File.ReadAllText(file));
        Assert.Equal(Newer, File.ReadAllText(s.Store.PathOf("x")));
    }

    [AvaloniaFact(Timeout = 180_000)]
    public async Task Renaming_a_newer_versions_session_keeps_it_and_goes_on_in_a_session_of_this_version()
    {
        // sessions-1: Rename deleted the newer CsCode's session file and its backups (and wrote nothing under the new name).
        const string Newer = "{\"version\": 3, \"name\": \"x\", \"someNewField\": 1}";
        var windows = new CsCode.TestDoubles.RecordingWindowService();
        await using var s = await InProcessSession.StartAsync("x", seed: store =>
        {
            Directory.CreateDirectory(store.SessionsDir);
            File.WriteAllText(store.PathOf("x"), Newer);
        }, configure: services =>
        {
            services.RemoveAll<IWindowService>();
            services.AddSingleton<IWindowService>(windows);
        });
        var file = s.WriteFile("f.txt", "original\n");
        await s.MustAsync(Show(file));
        await s.MustAsync(Edit(file, "EDIT "));

        windows.InputResponses.Enqueue("y");
        await s.Run.Services.GetRequiredService<ICommandService>().ExecuteAsync(SessionCommandIds.RenameSession);

        Assert.Equal(Newer, File.ReadAllText(s.Store.PathOf("x")));
        Assert.Equal(SessionLoadStatus.Loaded, s.Store.Load("y").Status);
        var backup = Assert.Single(s.Store.Backups.List("y"));
        Assert.Equal("EDIT original\n", s.Store.Backups.ReadText("y", backup.Id));
        Assert.True(FileLock.IsHeldElsewhere(s.Store.LockPathOf("y")));
        Assert.False(FileLock.IsHeldElsewhere(s.Store.LockPathOf("x")));
        Assert.Contains(windows.Messages, m => m.Message.Contains("kept as it is", StringComparison.Ordinal));
        Assert.Equal("y", s.Host.SessionName);
    }

    [AvaloniaFact(Timeout = 180_000)]
    public async Task Wait_returns_when_every_file_is_closed_or_the_window_is_closed()
    {
        // sessions-4: `cscode --wait a b` returned at once (only one file was waited for), and closing the window (hidden) never returned.
        await using var s = await InProcessSession.StartAsync("w");
        var a = s.WriteFile("a.txt", "a\n");
        var b = s.WriteFile("b.txt", "b\n");

        var both = s.RequestAsync(Show(a, b) with { Wait = true }, 120);
        Assert.True(await UntilAsync(async () => (await s.InspectAsync()).Tabs.Count == 2), "the files did not open");
        await s.MustAsync(new IpcMessage { Type = IpcVerbs.TestCloseTab, Uri = new Uri(a).ToString() });
        await Task.Delay(500);
        Assert.False(both.IsCompleted, "--wait returned while b.txt was still open");
        await s.MustAsync(new IpcMessage { Type = IpcVerbs.TestCloseTab, Uri = new Uri(b).ToString() });
        Assert.True((await both.WaitAsync(TimeSpan.FromSeconds(10))).Ok);

        // The git editor case: the user closes the window (it is hidden; the tab stays in the session). The launcher gets its answer.
        var message = s.WriteFile("COMMIT_EDITMSG", "message\n");
        var commit = s.RequestAsync(Show(message) with { Wait = true }, 120);
        Assert.True(await UntilAsync(async () => (await s.InspectAsync()).Tabs.Any(t => t.Uri == new Uri(message).ToString())));
        await s.MustAsync(new IpcMessage { Type = IpcVerbs.TestCloseWindow });
        var reply = await commit.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(reply.Ok == true, reply.Error);

        // And Quit Session answers a waiting launcher with OK (it got "closed the connection without replying" before).
        var again = s.RequestAsync(Show(message) with { Wait = true }, 120);
        Assert.True(await DevHost.WaitFor(() => s.Host.State == ProcessStates.Shown, 20_000));
        await Task.Delay(300);
        var quit = await s.RequestAsync(new IpcMessage { Type = IpcVerbs.Quit, Force = true });
        Assert.True(quit.Ok == true, quit.Error);
        Assert.True((await again.WaitAsync(TimeSpan.FromSeconds(10))).Ok);
    }

    [AvaloniaFact(Timeout = 180_000)]
    public async Task A_hidden_session_that_would_prompt_says_so_and_refuses_an_eviction_without_showing_its_window()
    {
        // sessions-10: an eviction sent a plain quit, which popped the hidden window up with save prompts (hot exit off).
        await using var s = await InProcessSession.StartAsync("e", "\"files.hotExit\": \"off\"");
        var file = s.WriteFile("a.txt", "a\n");
        await s.MustAsync(Show(file));
        await s.MustAsync(Edit(file, "UNSAVED "));
        await s.MustAsync(new IpcMessage { Type = IpcVerbs.Hide });

        var hello = ResidentDiscovery.Probe(s.Runtime, Environment.ProcessId, TimeSpan.FromSeconds(2));
        Assert.NotNull(hello);
        Assert.Equal(ProcessStates.Hidden, hello.State);
        Assert.True(hello.NeedsPrompt);

        var evict = await s.RequestAsync(new IpcMessage { Type = IpcVerbs.Quit, Force = false, Evict = true });
        Assert.NotEqual(true, evict.Ok);
        Assert.Contains("not evicted", evict.Error);
        Assert.Empty(s.Dialogs.Shown);
        var state = await s.InspectAsync();
        Assert.False(state.Visible);
        Assert.Equal(ProcessStates.Hidden, state.State);
        Assert.False(s.Run.Session.Ended.IsCompleted);
    }

    [AvaloniaFact(Timeout = 180_000)]
    public async Task A_committed_quit_lets_go_of_the_session_at_once_and_answers_a_late_show_with_quitting()
    {
        // sessions-3: the lock was held until the process exited (seconds, with a slow language server), so a launch meanwhile took the
        // quitting process for a wedged owner and opened a copy; a show to it replied OK and showed nothing.
        await using var s = await InProcessSession.StartAsync("q");
        var file = s.WriteFile("a.txt", "a\n");
        await s.MustAsync(Show(file));
        await s.MustAsync(Edit(file, "UNSAVED "));
        Assert.True(FileLock.IsHeldElsewhere(s.Store.LockPathOf("q")));

        var quit = await s.RequestAsync(new IpcMessage { Type = IpcVerbs.Quit, Force = false });
        Assert.True(quit.Ok == true, quit.Error);
        // The process has not ended yet (its services shut down after this), but the session is free and fully written.
        Assert.False(FileLock.IsHeldElsewhere(s.Store.LockPathOf("q")));
        Assert.Single(s.Store.Backups.List("q"));
        var show = await s.RequestAsync(Show(file));
        Assert.Equal(IpcMessage.QuittingError, show.Error);
        await s.Run.Session.Ended.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [AvaloniaFact(Timeout = 180_000)]
    public async Task The_autosave_capture_builds_no_document_text_on_the_ui_thread()
    {
        // sessions-11: every autosave materialised each changed dirty document's whole text on the UI thread (tens of ms and an LOH string of
        // twice its size for a big file, every 1.5 s while typing). The capture now takes the document's snapshot; the lane builds the text.
        await using var s = await InProcessSession.StartAsync("big");
        var text = new StringBuilder();
        for (var i = 0; text.Length < 8_000_000; i++) text.Append("2026-10-30 00:00:00.000 INFO some log line number ").Append(i).Append('\n');
        var file = s.WriteFile("big.log", text.ToString());
        await s.MustAsync(Show(file));

        // A keystroke and the capture in one UI-thread turn: no autosave can back the new version up in between.
        var doc = s.Run.Services.GetRequiredService<CsCode.App.Docking.DocumentWell>().Find(new Uri(file))!.Doc;
        doc.ApplyEdits([new CsCode.Core.Text.TextEdit(CsCode.Core.Text.TextRange.At(0, 0), "x")]);
        var controller = s.Host.Controller!;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var captured = controller.Capture(includeBackups: true);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        var changed = Assert.Single(captured.Changed);
        Assert.NotNull(changed.Snapshot);
        Assert.True(allocated < 1_000_000, $"the capture allocated {allocated / 1024} KB on the UI thread for an 8 MB document");
        Assert.Equal(text.Length + 1, changed.ToEntry().Text.Length); // and the lane gets the whole text
    }

    /// <summary>A remote file system for a test scheme whose reads wait until <see cref="Connect"/> (the connection coming up).</summary>
    private sealed class SlowRemote : IFileSystemProvider
    {
        private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public string Scheme => "memrem";
        public void Connect() => _connected.TrySetResult();

        public async ValueTask<FileStat?> StatAsync(Uri uri, CancellationToken ct = default)
        {
            await _connected.Task.WaitAsync(ct);
            return Files.TryGetValue(uri.AbsolutePath, out var b) ? new FileStat(FileType.File, b.Length, DateTimeOffset.UnixEpoch) : null;
        }

        public async ValueTask<byte[]> ReadFileAsync(Uri uri, CancellationToken ct = default)
        {
            await _connected.Task.WaitAsync(ct);
            return Files.TryGetValue(uri.AbsolutePath, out var b) ? b : throw new FileNotFoundException(uri.ToString());
        }

        public ValueTask<IReadOnlyList<DirectoryEntry>> ReadDirectoryAsync(Uri uri, CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<DirectoryEntry>>([]);
        public ValueTask WriteFileAsync(Uri uri, ReadOnlyMemory<byte> content, bool createParents = true, CancellationToken ct = default) { Files[uri.AbsolutePath] = content.ToArray(); return ValueTask.CompletedTask; }
        public ValueTask CreateDirectoryAsync(Uri uri, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask DeleteAsync(Uri uri, bool recursive = false, bool useTrash = true, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask RenameAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask CopyAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    [AvaloniaFact(Timeout = 180_000)]
    public async Task Unsaved_changes_of_remote_files_go_back_into_the_remote_documents_once_they_can_be_read()
    {
        // sessions-12: they came back as an anonymous plain-text Untitled buffer, and the link to the remote file was lost for good.
        const string Remote = "memrem://box/dir/a.cs", Gone = "memrem://box/dir/gone.cs";
        var remote = new SlowRemote();
        remote.Files["/dir/a.cs"] = Encoding.UTF8.GetBytes("class A {}\n");
        await using var s = await InProcessSession.StartAsync("r", seed: store =>
        {
            store.Save(new SessionDocument { Name = "r" });
            store.Backups.Write("r", [new BackupEntry(HotExitBackupStore.IdFor(Remote), Remote, "class A { int unsaved; }\n", "utf-8", "\n", "csharp", 3),
                new BackupEntry(HotExitBackupStore.IdFor(Gone), Gone, "deleted remotely\n", "utf-8", "\n", "csharp", 2)], [], long.MaxValue);
        }, configure: services => services.AddSingleton<IFileSystemProvider>(remote));

        // Not connected yet: nothing is restored, and the autosaves keep both backups.
        await Task.Delay(3000);
        Assert.Equal(2, s.Store.Backups.List("r").Count);
        Assert.DoesNotContain((await s.InspectAsync()).Tabs, t => t.Uri.StartsWith("untitled:", StringComparison.Ordinal));

        remote.Connect();
        InspectedState state = null!;
        Assert.True(await UntilAsync(async () => (state = await s.InspectAsync(withText: true)).Tabs.Count == 2), "the remote backups were not restored");
        var restored = Assert.Single(state.Tabs, t => t.Uri == Remote);
        Assert.True(restored.Dirty);
        Assert.Equal("class A { int unsaved; }\n", restored.Text);
        // The file that cannot be read any more: its text in an untitled buffer of its language (the user is told which file it was).
        var orphan = Assert.Single(state.Tabs, t => t.Uri.StartsWith("untitled:", StringComparison.Ordinal));
        Assert.Equal("deleted remotely\n", orphan.Text);
        var languages = s.Run.Services.GetRequiredService<CsCode.App.Docking.DocumentWell>().EditorTabs.Select(t => t.Doc.LanguageId).ToArray();
        Assert.Equal(["csharp", "csharp"], languages);
    }

    /// <summary>Save prompts the test answers when it wants to (the prompt stays up meanwhile, as a real one does).</summary>
    private sealed class GatedDialogs : IModalDialogs
    {
        private TaskCompletionSource<string?> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _asked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when the next prompt is up.</summary>
        public Task Asked => _asked.Task;

        public Task<string?> ShowAsync(string title, string message, IReadOnlyList<string> buttons, int defaultIndex = 0)
        {
            _asked.TrySetResult();
            return _answer.Task;
        }

        /// <summary>Answers the prompt that is up (UI thread); the next one waits again.</summary>
        public void Answer(string? button)
        {
            var answer = _answer;
            _answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            answer.TrySetResult(button);
        }
    }

    /// <summary>A session process the launcher "starts": answers hello for its session and shows (no process is started).</summary>
    private sealed class StandInHost : IIpcHandler, IDisposable
    {
        private readonly IpcServer _server;

        public StandInHost(RuntimeDirectory runtime, int pid, string session)
        {
            Pid = pid;
            Session = session;
            _server = new IpcServer(runtime.EndpointOf(pid), this);
            _server.Start();
        }

        public int Pid { get; }
        public string Session { get; }

        public Task<IpcMessage> HandleAsync(IpcMessage request, CancellationToken ct) => Task.FromResult(request.Type switch
        {
            IpcVerbs.Hello => new IpcMessage { Type = IpcVerbs.Hello, Ok = true, Pid = Pid, Session = Session, State = ProcessStates.Shown, ProtocolVersion = IpcMessage.ProtocolVersionCurrent },
            IpcVerbs.Show => IpcMessage.Reply(),
            _ => IpcMessage.Failure("unexpected " + request.Type),
        });

        public void Dispose() => _server.Dispose();
    }

    private sealed class StandInSpawner(RuntimeDirectory runtime) : IHostSpawner, IDisposable
    {
        private readonly List<StandInHost> _hosts = new();
        public List<IReadOnlyList<string>> Spawned { get; } = new();

        public ISpawnedHost Spawn(IReadOnlyList<string> args)
        {
            lock (_hosts)
            {
                Spawned.Add(args);
                var pid = 4_170_000 + Spawned.Count; // made up: only its endpoint in the test's runtime directory exists
                _hosts.Add(new StandInHost(runtime, pid, args[args.ToList().IndexOf("--session") + 1]));
                return new Running(pid);
            }
        }

        public void Dispose()
        {
            lock (_hosts) foreach (var h in _hosts) h.Dispose();
        }

        private sealed record Running(int Pid) : ISpawnedHost
        {
            public bool HasExited => false;
            public int? ExitCode => null;
        }
    }

    private static readonly LauncherTimeouts Timeouts = new()
    {
        Handshake = TimeSpan.FromSeconds(2), ShowReply = TimeSpan.FromSeconds(20), StartHello = TimeSpan.FromSeconds(10), QuitWait = TimeSpan.FromSeconds(5),
    };

    [AvaloniaFact(Timeout = 180_000)]
    public async Task A_relaunch_during_the_shutdown_after_save_prompts_starts_the_session_afresh()
    {
        // sessions-3: a quit that went through the save prompts showed its window (hello: shown) and hello kept saying so through the shutdown.
        // A launch meanwhile was sent to the closing window, heard "quitting" twice and failed with exit 1.
        await using var s = await InProcessSession.StartAsync("q", "\"files.hotExit\": \"off\"");
        var file = s.WriteFile("a.txt", "a\n");
        await s.MustAsync(Show(file));
        await s.MustAsync(Edit(file, "UNSAVED "));
        s.Dialogs.Answers.Enqueue("Don't Save");
        var quit = await s.RequestAsync(new IpcMessage { Type = IpcVerbs.Quit, Force = false }, 60);
        Assert.True(quit.Ok == true, quit.Error);
        Assert.Contains(s.Dialogs.Shown, d => d.Buttons.Contains("Don't Save"));

        // The process is still shutting down (its endpoint answers): it says it is quitting, and it has let go of the session.
        var hello = await Task.Run(() => ResidentDiscovery.Probe(s.Runtime, Environment.ProcessId, TimeSpan.FromSeconds(2)));
        Assert.NotNull(hello);
        Assert.Equal(ProcessStates.Quitting, hello.State);
        Assert.False(FileLock.IsHeldElsewhere(s.Store.LockPathOf("q")));

        using var spawner = new StandInSpawner(s.Runtime);
        var errors = new StringWriter();
        var launcher = new SessionLauncher(s.Runtime, s.Store, new LaunchSettings(), spawner, TextWriter.Null, errors, Timeouts);
        var outcome = await Task.Run(() => launcher.Run(new LaunchRequest(LaunchKind.Open) { Session = "q" }));
        Assert.True(outcome.ExitCode == 0, errors.ToString());
        Assert.Equal(LaunchAction.Start, outcome.Plan.Action);
        Assert.Equal(["--host", "--session", "q"], Assert.Single(spawner.Spawned)); // the session itself, afresh: not a copy
        await s.Run.Session.Ended.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [AvaloniaFact(Timeout = 180_000)]
    public async Task A_show_during_the_save_prompts_goes_ahead_and_one_during_the_last_write_waits_for_the_quit()
    {
        // sessions-3: a show that arrived while the quit was still writing replied OK and opened its file into the window that was about to close.
        var dialogs = new GatedDialogs();
        await using var s = await InProcessSession.StartAsync("p", "\"files.hotExit\": \"off\"", configure: services =>
        {
            services.RemoveAll<IModalDialogs>();
            services.AddSingleton<IModalDialogs>(dialogs);
        });
        var a = s.WriteFile("a.txt", "a\n");
        var b = s.WriteFile("b.txt", "b\n");
        var c = s.WriteFile("c.txt", "c\n");
        await s.MustAsync(Show(a));
        await s.MustAsync(Edit(a, "UNSAVED "));

        // "Save changes?" is up: nothing is decided and the window is shown, so a launch opens its file there (as VS Code does).
        var asked = dialogs.Asked;
        var quit = s.RequestAsync(new IpcMessage { Type = IpcVerbs.Quit, Force = false }, 120);
        await asked.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(ProcessStates.Shown, s.Host.State);
        Assert.True(FileLock.IsHeldElsewhere(s.Store.LockPathOf("p")));
        await s.MustAsync(Show(b));
        dialogs.Answer("Cancel");
        var cancelled = await quit.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("the quit was cancelled", cancelled.Error);
        var state = await s.InspectAsync();
        Assert.Contains(state.Tabs, t => t.Uri == new Uri(b).ToString());
        Assert.Equal(ProcessStates.Shown, state.State);

        // `cscode --quit p` is writing the backups (the lane is held up here): a show that arrives now waits for it, then hears that the session
        // is quitting (the launcher then opens the session afresh), instead of opening c into the window that is about to close.
        var lane = new ManualResetEventSlim();
        try
        {
            _ = s.Host.Lane.RunAsync(() => lane.Wait(TimeSpan.FromSeconds(60)));
            var forced = s.RequestAsync(new IpcMessage { Type = IpcVerbs.Quit, Force = true }, 120);
            Assert.True(await DevHost.WaitFor(() => s.Host.State == ProcessStates.Quitting, 20_000), "the quit did not begin");
            var late = s.RequestAsync(Show(c));
            await Task.Delay(500);
            Assert.False(late.IsCompleted, "the show was answered while the quit was still writing");
            lane.Set();
            var done = await forced.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(done.Ok == true, done.Error);
            Assert.Equal(IpcMessage.QuittingError, (await late.WaitAsync(TimeSpan.FromSeconds(30))).Error);
        }
        finally { lane.Set(); }
        await s.Run.Session.Ended.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [AvaloniaFact(Timeout = 180_000)]
    public async Task A_quit_that_is_cancelled_or_refused_leaves_a_wait_waiting()
    {
        // sessions-4: every quit request answered the pending `cscode --wait` at once, also one the user then cancelled at the save prompt, or one
        // that was refused: git went on with the message as it was on disk while the window and the unsaved message stayed open.
        await using var s = await InProcessSession.StartAsync("g", "\"files.hotExit\": \"off\"");
        var message = s.WriteFile("COMMIT_EDITMSG", "\n# Please enter the commit message\n");
        var commit = s.RequestAsync(Show(message) with { Wait = true }, 120);
        Assert.True(await UntilAsync(async () => (await s.InspectAsync()).Tabs.Any(t => t.Uri == new Uri(message).ToString())), "the message did not open");
        await s.MustAsync(Edit(message, "Fix the frobnicator"));

        // An eviction is refused (quitting would have to ask), and Quit Session is cancelled at "Save changes?".
        var evict = await s.RequestAsync(new IpcMessage { Type = IpcVerbs.Quit, Force = false, Evict = true });
        Assert.NotEqual(true, evict.Ok);
        s.Dialogs.Answers.Enqueue("Cancel");
        var cancelled = await s.RequestAsync(new IpcMessage { Type = IpcVerbs.Quit, Force = false }, 60);
        Assert.Equal("the quit was cancelled", cancelled.Error);
        await Task.Delay(500);
        Assert.False(commit.IsCompleted, "--wait returned while the message was still open and unsaved");

        // Quit Session again, Save: git gets its answer, with the message on disk.
        s.Dialogs.Answers.Enqueue("Save");
        var quit = await s.RequestAsync(new IpcMessage { Type = IpcVerbs.Quit, Force = false }, 60);
        Assert.True(quit.Ok == true, quit.Error);
        Assert.True((await commit.WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        Assert.StartsWith("Fix the frobnicator\n", File.ReadAllText(message));
        await s.Run.Session.Ended.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [AvaloniaFact(Timeout = 180_000)]
    public async Task A_quit_after_save_prompts_keeps_the_backups_of_remote_files_still_waiting_for_their_connection()
    {
        // sessions-12 and sessions-2: discarding the backups after the save prompts deleted the session's whole backup directory, with the unsaved
        // changes of remote files whose connection was not up yet (no tab, so nobody was asked about them).
        const string Remote = "memrem://box/dir/a.cs";
        var remote = new SlowRemote(); // never connects
        await using var s = await InProcessSession.StartAsync("rq", "\"files.hotExit\": \"off\"", seed: store =>
        {
            store.Save(new SessionDocument { Name = "rq" });
            store.Backups.Write("rq", [new BackupEntry(HotExitBackupStore.IdFor(Remote), Remote, "class A { int unsaved; }\n", "utf-8", "\n", "csharp", 3)], [], long.MaxValue);
        }, configure: services => services.AddSingleton<IFileSystemProvider>(remote));
        var file = s.WriteFile("b.txt", "b\n");
        await s.MustAsync(Show(file));
        await s.MustAsync(Edit(file, "discard me "));
        Assert.True(await DevHost.WaitFor(() => s.Store.Backups.List("rq").Count == 2, 15_000), "the autosave did not back up the local edit");

        s.Dialogs.Answers.Enqueue("Don't Save");
        var quit = await s.RequestAsync(new IpcMessage { Type = IpcVerbs.Quit, Force = false }, 60);
        Assert.True(quit.Ok == true, quit.Error);
        await s.Run.Session.Ended.WaitAsync(TimeSpan.FromSeconds(30));

        var kept = Assert.Single(s.Store.Backups.List("rq"));
        Assert.Equal(Remote, kept.Uri);
        Assert.Equal("class A { int unsaved; }\n", s.Store.Backups.ReadText("rq", kept.Id));
        Assert.Equal("b\n", File.ReadAllText(file));
    }

    [AvaloniaFact(Timeout = 180_000)]
    public async Task Renaming_a_session_takes_the_backups_of_remote_files_still_waiting_for_their_connection_along()
    {
        // sessions-12: Rename deleted the old session's directory, and the backups of remote files waiting for their connection had never been
        // written under the new name: a quit or a crash before the connection came up lost them for good.
        const string Remote = "memrem://box/dir/a.cs";
        var remote = new SlowRemote();
        remote.Files["/dir/a.cs"] = Encoding.UTF8.GetBytes("class A {}\n");
        var windows = new CsCode.TestDoubles.RecordingWindowService();
        await using var s = await InProcessSession.StartAsync("ra", seed: store =>
        {
            store.Save(new SessionDocument { Name = "ra" });
            store.Backups.Write("ra", [new BackupEntry(HotExitBackupStore.IdFor(Remote), Remote, "class A { int unsaved; }\n", "utf-8", "\n", "csharp", 3)], [], long.MaxValue);
        }, configure: services =>
        {
            services.AddSingleton<IFileSystemProvider>(remote);
            services.RemoveAll<IWindowService>();
            services.AddSingleton<IWindowService>(windows);
        });

        windows.InputResponses.Enqueue("rb");
        await s.Run.Services.GetRequiredService<ICommandService>().ExecuteAsync(SessionCommandIds.RenameSession);

        Assert.Equal("rb", s.Host.SessionName);
        Assert.False(s.Store.Exists("ra"));
        var moved = Assert.Single(s.Store.Backups.List("rb"));
        Assert.Equal(Remote, moved.Uri);
        Assert.Equal("class A { int unsaved; }\n", s.Store.Backups.ReadText("rb", moved.Id));

        // Later autosaves keep it there, and once the connection is up the text goes back into the remote document.
        await Task.Delay(2500);
        Assert.Single(s.Store.Backups.List("rb"));
        remote.Connect();
        Assert.True(await UntilAsync(async () => (await s.InspectAsync(withText: true)).Tabs.Any(t => t.Uri == Remote && t.Dirty && t.Text == "class A { int unsaved; }\n")),
            "the remote backup was not restored");
    }
}
