using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Platform;
using CsCode.Macros;
using Xunit;

namespace CsCode.App.Tests.RealApp.Macros;

/// <summary>
/// General macros in the REAL composition (docs/MACROS.md): recorded from real key events in a real editor view, replayed through the same
/// pipeline, saved to macros.json, bound to keys, and the Vim interplay. <c>CSCODE_HOME</c> is a temp directory (<see cref="RealAppHost"/>).
/// </summary>
public class MacroRealAppTests(ITestOutputHelper output)
{
    private static KeyStep K(string key) => new(KeyStrokeParser.ParseStroke(key));

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Typing_keys_and_a_key_chord_command_are_recorded_and_replay_n_times_in_the_real_editor()
    {
        await using var app = await MacroApp.StartAsync("a();\nb();\nc();\nd();", "code.cs");

        app.RecordKey();
        Assert.True(app.Service.IsRecording);
        Assert.Equal("$(record) Recording macro…", app.StatusText(MacroCommands.RecordingStatusId));
        app.Press(Key.K, MacroApp.Ctrl);   // Ctrl+K, Ctrl+C: Visual Studio's Comment Selection (a command)
        app.Press(Key.C, MacroApp.Ctrl);
        await app.SettleAsync();
        app.Press(Key.End);
        app.Type("!");
        app.Press(Key.Down);
        app.RecordKey();
        await app.SettleAsync();

        Assert.False(app.Service.IsRecording);
        Assert.Null(app.StatusText(MacroCommands.RecordingStatusId));
        var steps = app.Service.Last!.Steps;
        Assert.Equal([new CommandStep("editor.action.addCommentLine"), K("end"), new TextStep("!"), K("down")], steps);
        var first = app.Text.Split('\n')[0];
        Assert.EndsWith("a();!", first);
        Assert.StartsWith("//", first);

        await app.Host.RunAsync(MacroCommands.PlayLast, 2);
        await app.SettleAsync();

        var lines = app.Text.Split('\n');
        Assert.Equal(first.Replace("a();", "b();"), lines[1]);
        Assert.Equal(first.Replace("a();", "c();"), lines[2]);
        Assert.Equal("d();", lines[3]);
        Assert.Equal(new Core.Text.TextPosition(3, 4), app.Caret);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Play_until_end_of_file_and_on_selected_lines_in_the_real_editor()
    {
        await using var app = await MacroApp.StartAsync("one\ntwo\nthree\nfour\nfive");
        app.RecordKey();
        app.Press(Key.Home);
        app.Type("> ");
        app.Press(Key.Down);
        app.RecordKey();
        Assert.Equal([K("home"), new TextStep("> "), K("down")], app.Service.Last!.Steps);

        // caret on line 1 after recording: every line from there to the end
        await app.Host.RunAsync(MacroCommands.Play, "untilEndOfFile");
        Assert.Equal("> one\n> two\n> three\n> four\n> five", app.Text);

        // on selected lines: a selection from line 1 to the start of line 3 covers lines 1 and 2
        app.View.Modal.Controller.SetSelection(new Core.Text.TextRange(new(1, 3), new(3, 0)));
        await app.Host.RunAsync(MacroCommands.Play, "onSelectedLines");
        Assert.Equal("> one\n> > two\n> > three\n> four\n> five", app.Text);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Esc_stops_a_long_playback_recorded_with_pauses_and_keys_typed_meanwhile_do_not_reach_the_editor()
    {
        await using var app = await MacroApp.StartAsync("start", settings: $"\"{MacroSettings.RecordPauses}\": true");
        app.RecordKey();
        app.Type("x");
        await Task.Delay(400);     // recorded as a pause (macros.recordPauses)
        app.Press(Key.Back);
        app.RecordKey();
        var steps = app.Service.Last!.Steps;
        Assert.Equal(3, steps.Count);
        Assert.Equal(new TextStep("x"), steps[0]);
        Assert.InRange(Assert.IsType<PauseStep>(steps[1]).Milliseconds, 350, 2000);
        Assert.Equal(K("backspace"), steps[2]);

        // (the headless dispatcher runs every queued job when a key is sent: keys get in while the macro waits in its pause)
        var playing = app.Host.Get<ICommandService>().ExecuteAsync(MacroCommands.PlayLast, 1_000).AsTask();
        Assert.True(await RealAppHost.WaitFor(() => app.Service.State == MacroState.Playing && app.Text == "xstart"));
        Assert.Equal("$(play) Playing macro… (Esc to stop)", app.StatusText(MacroCommands.PlayingStatusId));
        app.Type("Q");             // swallowed: it would interleave with the macro's typing
        app.Press(Key.Escape);     // stops it
        Assert.True(await RealAppHost.WaitFor(() => playing.IsCompleted), "Esc did not stop the playback");
        await playing;

        Assert.Equal(MacroState.Idle, app.Service.State);
        Assert.Equal("xstart", app.Text); // stopped in the pause, before its Backspace
        Assert.Null(app.StatusText(MacroCommands.PlayingStatusId));
        Assert.Contains("was stopped", app.StatusText(MacroCommands.MessageStatusId));
    }

    [AvaloniaFact(Timeout = 300_000)]
    public async Task Playing_ten_thousand_times_in_the_real_editor_stays_within_the_ui_budget()
    {
        await using var app = await MacroApp.StartAsync("start");
        app.RecordKey();
        app.Type("ab");
        app.Press(Key.Back);
        app.Press(Key.Back);
        app.RecordKey();

        // wall-clock on a shared machine: the best of three attempts counts (as in the shell's ResponsivenessTests)
        string? failure = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            app.Service.Player.ResetSliceDurations();
            var latencies = new List<double>();
            using var stop = new CancellationTokenSource();
            var heartbeat = new Thread(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    var stamp = Stopwatch.GetTimestamp();
                    Dispatcher.UIThread.Post(() => latencies.Add(Stopwatch.GetElapsedTime(stamp).TotalMilliseconds), DispatcherPriority.Normal);
                    stop.Token.WaitHandle.WaitOne(2);
                }
            }) { IsBackground = true, Name = "macro-heartbeat" };
            heartbeat.Start();
            var playing = app.Host.Get<ICommandService>().ExecuteAsync(MacroCommands.PlayLast, 10_000).AsTask();
            // pump like a real dispatcher loop: jobs as soon as they are queued (a Task.Delay between pumps would count as dispatcher latency)
            var sw = Stopwatch.StartNew();
            while (!playing.IsCompleted && sw.Elapsed < TimeSpan.FromMinutes(2))
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Yield();
            }
            stop.Cancel();
            heartbeat.Join();
            Dispatcher.UIThread.RunJobs();
            Assert.True(playing.IsCompleted, "the playback did not finish");
            Assert.Equal("start", app.Text);

            var slices = app.Service.Player.SliceDurations.OrderBy(x => x).ToArray();
            var beats = latencies.OrderBy(x => x).ToArray();
            double P(double[] a, double p) => a.Length == 0 ? 0 : a[Math.Min(a.Length - 1, (int)Math.Ceiling(p * a.Length) - 1)];
            var line = $"attempt {attempt}: {slices.Length} slices p50 {P(slices, 0.5):F2} p99 {P(slices, 0.99):F2} max {slices.LastOrDefault():F2} ms; " +
                $"heartbeat {beats.Length} p50 {P(beats, 0.5):F2} p99 {P(beats, 0.99):F2} max {beats.LastOrDefault():F2} ms";
            output.WriteLine(line);
            failure = P(slices, 0.99) > 4 || P(beats, 0.99) >= 16 || beats.LastOrDefault() >= 50 ? line : null;
            if (failure is null) break;
        }
        Assert.True(failure is null, failure);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task A_search_in_the_find_panel_is_recorded_as_a_search_step_and_plays_until_it_fails()
    {
        await using var app = await MacroApp.StartAsync("foo a foo b foo c");
        app.RecordKey();
        app.Press(Key.F, MacroApp.Ctrl);    // opens the find panel (not recorded: the search is)
        await app.SettleAsync();
        await FocusFindBoxAsync(app);
        app.Type("foo");                    // into the find box: the search step's text
        app.Press(Key.Enter);               // find next: recorded
        app.Press(Key.Escape);              // closes the panel, the editor has the focus again
        await app.SettleAsync();
        app.Type("X");                      // replaces the selected match
        app.RecordKey();

        var steps = app.Service.Last!.Steps;
        output.WriteLine(string.Join(" | ", steps));
        Assert.Equal(2, steps.Count);
        var find = Assert.IsType<CommandStep>(steps[0]);
        Assert.Equal(MacroCommands.Find, find.Command);
        Assert.Equal("foo", find.Args[0].GetProperty("text").GetString());
        Assert.Equal(new TextStep("X"), steps[1]);

        app.View.Document.ApplyEdits([new Core.Text.TextEdit(new(new(0, 0), new(0, app.View.Document.GetLineText(0).Length)), "foo a foo b foo c")]);
        app.SetCaret(0, 0);
        await app.Host.RunAsync(MacroCommands.Play, "untilSearchFails");

        Assert.Equal("X a X b X c", app.Text);
        Assert.Contains("played 3 times", app.StatusText(MacroCommands.MessageStatusId));
    }

    /// <summary>
    /// The find panel focuses its text box when it opens; in the headless platform its template may not be applied by then (no layout pass yet),
    /// so the test puts the focus where a click would.
    /// </summary>
    private async Task FocusFindBoxAsync(MacroApp app)
    {
        static bool InFindPanel(object? e) => (e as Avalonia.Visual)?.FindAncestorOfType<AvaloniaEdit.Search.SearchPanel>() is not null;
        if (await RealAppHost.WaitFor(() => InFindPanel(app.Host.Window.FocusManager?.GetFocusedElement()), 2000)) return;
        output.WriteLine("the find panel did not focus its text box by itself (headless): focusing it");
        var box = app.View.GetVisualDescendants().OfType<AvaloniaEdit.Search.SearchPanel>().SelectMany(p => p.GetVisualDescendants().OfType<Avalonia.Controls.TextBox>()).FirstOrDefault()
            ?? throw new InvalidOperationException("the find panel did not open");
        box.Focus();
        Assert.True(await RealAppHost.WaitFor(() => InFindPanel(app.Host.Window.FocusManager?.GetFocusedElement())));
    }

    [AvaloniaFact(Timeout = 180_000)]
    public async Task Saved_and_last_macros_survive_a_restart_with_the_same_home_and_key_bindings_run_them()
    {
        string home;
        await using (var first = await MacroApp.StartAsync("text"))
        {
            first.RecordKey();
            first.Type("hi ");
            first.RecordKey();
            await first.Host.RunAsync(MacroCommands.Save, "greet");
            Assert.True(await RealAppHost.WaitFor(() => first.Host.Get<ICommandService>().Has("macro.run.greet")));
            await first.Host.Get<IStateStorage>().FlushAsync();
            home = first.Host.Home.Path;
            var macros = Path.Combine(first.Host.Get<IAppPaths>().UserDataDir, MacroCommands.FileName);
            Assert.Contains("\"name\": \"greet\"", await File.ReadAllTextAsync(macros));
            // the next start uses a copy of this home (RealAppHost owns a fresh temp dir per run)
            CopyDirectory(home, home + "-copy");
        }
        try
        {
            await using var second = await MacroApp.StartAsync("text", seed: h =>
            {
                CopyDirectory(home + "-copy", h.Home.Path, overwriteSettings: false);
                var macros = h.Home.Combine("data", MacroCommands.FileName);
                File.WriteAllText(macros, File.ReadAllText(macros).Replace("\"name\": \"greet\",", "\"name\": \"greet\",\n      \"key\": \"ctrl+alt+8\","));
                h.Home.WriteFile("config/keybindings.json", """[ { "key": "ctrl+alt+9", "command": "macro.run.greet" } ]""");
            });
            Assert.True(await RealAppHost.WaitFor(() => second.Service.Last is not null && second.Service.Named.Count == 1), "the macros were not loaded");
            Assert.Equal([new TextStep("hi ")], second.Service.Last!.Steps);
            Assert.Equal("ctrl+alt+8", second.Service.Named[0].Key);
            await second.FocusAsync();

            second.Press(Key.D9, MacroApp.Ctrl | RawInputModifiers.Alt);   // keybindings.json
            await second.SettleAsync();
            second.Press(Key.D8, MacroApp.Ctrl | RawInputModifiers.Alt);   // "key" in macros.json
            await second.SettleAsync();
            Assert.Equal("hi hi text", second.Text);
            var palette = second.Host.Get<ICommandService>().All.Single(c => c.Id == "macro.run.greet");
            Assert.Equal(("Run greet", "Macros"), (palette.Title, palette.Category));
        }
        finally
        {
            try { Directory.Delete(home + "-copy", recursive: true); } catch (IOException) { }
        }
    }

    private static void CopyDirectory(string from, string to, bool overwriteSettings = true)
    {
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(dir.Replace(from, to));
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = file.Replace(from, to);
            if (!overwriteSettings && Path.GetFileName(file) == "settings.json") continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task With_vim_on_recording_is_refused_with_the_hint_and_only_command_macros_play()
    {
        await using var app = await MacroApp.StartAsync("line one\nline two", vim: true, seed: h => h.Home.WriteFile("data/macros.json", """
            { "macros": [
                { "name": "select", "steps": [ { "command": "editor.action.selectAll" } ] },
                { "name": "typing", "steps": [ { "text": "zzz" } ] }
            ] }
            """));
        Assert.True(await RealAppHost.WaitFor(() => app.Host.Get<ICommandService>().Has("macro.run.typing")));
        Assert.True(app.Host.Shell.Vim!.Enabled);

        app.RecordKey();
        await app.SettleAsync();
        Assert.False(app.Service.IsRecording);
        Assert.Contains(app.Notifications, n => n.StartsWith("Vim emulation is on: record a macro with q"));

        await app.Host.RunAsync("macro.run.typing");
        Assert.Equal("line one\nline two", app.Text);
        Assert.Contains(app.Notifications, n => n.Contains("types into the editor and cannot play") && n.Contains("Vim emulation is on"));

        await app.Host.RunAsync("macro.run.select");
        Assert.Equal([new Core.Text.TextRange(new(0, 0), new(1, 8))], app.View.Modal.Controller.Selections);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Switching_vim_on_stops_recording_and_keys_vim_takes_are_never_recorded()
    {
        await using var app = await MacroApp.StartAsync("abc\ndef");
        app.RecordKey();
        app.Type("12");
        await app.Host.RunAsync("vim.toggle");   // Vim on: recording stops
        await app.SettleAsync();
        Assert.True(app.Host.Shell.Vim!.Enabled);
        Assert.False(app.Service.IsRecording);
        app.Type("dd");                          // Vim deletes the line; nothing records it
        await app.SettleAsync();

        Assert.Equal("def", app.Text);
        Assert.Equal([new TextStep("12")], app.Service.Last!.Steps); // the stop comes inside vim.toggle: the toggle itself is not part of the macro
        Assert.Contains(app.Notifications, n => n.StartsWith("Recording stopped: Vim emulation was switched on."));
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task A_macro_never_records_its_own_playback()
    {
        await using var app = await MacroApp.StartAsync("abc");
        app.RecordKey();
        app.Type("1");
        app.RecordKey();
        app.RecordKey();                         // a second recording...
        app.Type("2");
        app.Press(Key.M, MacroApp.Ctrl);         // ...tries to play the last macro (Ctrl+M, Ctrl+Enter): refused, not recorded
        app.Press(Key.Enter, MacroApp.Ctrl);
        await app.SettleAsync();
        app.RecordKey();

        Assert.Equal([new TextStep("2")], app.Service.Last!.Steps);
        Assert.Equal("12abc", app.Text);
        Assert.Contains(app.Notifications, n => n.Contains("stop recording first"));
    }

    private const string StopMacros = """
        { "macros": [
            { "name": "vim", "steps": [ { "text": "a" }, { "command": "vim.toggle" }, { "text": "b" } ] },
            { "name": "close", "steps": [ { "command": "workbench.action.files.save" }, { "command": "workbench.action.closeActiveEditor" }, { "text": "z" } ] }
        ] }
        """;

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Closing_the_editor_or_switching_vim_on_during_playback_stops_it_with_the_reason()
    {
        await using var app = await MacroApp.StartAsync("abc", seed: h => h.Home.WriteFile("data/macros.json", StopMacros));
        Assert.True(await RealAppHost.WaitFor(() => app.Host.Get<ICommandService>().Has("macro.run.close")));

        await app.Host.RunAsync("macro.run.vim");
        Assert.True(app.Host.Shell.Vim!.Enabled);
        Assert.Equal("aabc", app.Text);
        Assert.Contains(app.Notifications, n => n.StartsWith("Macro 'vim' stopped at step 3 of 3") && n.Contains("Vim emulation is on"));

        await app.Host.RunAsync("vim.toggle");
        await app.FocusAsync();
        await app.Host.RunAsync("macro.run.close");
        Assert.True(await RealAppHost.WaitFor(() => app.Well.ActiveEditor is null));
        Assert.Contains(app.Notifications, n => n.StartsWith("Macro 'close' stopped at step 3 of 3") && n.Contains("No editor is open"));
        Assert.Equal("aabc", await File.ReadAllTextAsync(app.Host.Files.Combine("test.txt")));
    }

    [AvaloniaFact(Timeout = 180_000)]
    public async Task Recording_and_playing_in_an_editor_that_is_then_closed_leaves_nothing_behind()
    {
        await using var app = await MacroApp.StartAsync("anchor\n");
        var anchor = app.Tab;
        var refs = await RecordPlayCloseAsync(app);
        app.Well.Activate(anchor);
        await app.FocusAsync();                  // focus leaves the closed view (AvaloniaEdit keeps the last focused one)
        for (var round = 0; round < 10 && refs.Any(w => w.IsAlive); round++)
        {
            await DevScene.SettleAsync(5);
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        var alive = refs.Where(w => w.IsAlive).Select(w => w.Target?.GetType().Name).ToArray();
        Assert.True(alive.Length == 0, $"still reachable: {string.Join(", ", alive)}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference[]> RecordPlayCloseAsync(MacroApp app)
    {
        app.Host.Files.WriteFile("b.txt", "b\n");
        var tab = await app.Well.OpenAsync(new Uri(app.Host.Files.Combine("b.txt")));
        await app.FocusAsync();
        app.RecordKey();
        app.Type("xy");
        app.Press(Key.Left);
        app.RecordKey();
        await app.Host.RunAsync(MacroCommands.PlayLast, 3);
        Assert.Equal("xxxxyyyyb\n", app.Text.Replace("\r\n", "\n"));
        var refs = new WeakReference[] { new(app.View), new(app.View.Document), new(tab) };
        await app.Well.CloseAsync(tab, prompt: false);
        Dispatcher.UIThread.RunJobs();
        return refs;
    }
}
