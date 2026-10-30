using System.Text.Json;
using CsCode.Core.Contributions;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Macros.Tests.Support;
using CsCode.Platform.FileSystem;
using CsCode.Platform.Io;
using CsCode.Platform.State;
using CsCode.Platform.Watching;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Macros.Tests;

/// <summary>
/// The service with the real file system, watcher and state storage (temp directories) and the shared fakes: what recording records and what it
/// leaves out, the commands, macros.json (save, live reload, macro.run.&lt;name&gt; and its key), the last macro across restarts, the Vim gate.
/// </summary>
public sealed class MacroServiceTests : IAsyncDisposable
{
    private readonly TestUiThread _ui = new();
    private readonly TestAppPaths _paths = new();
    private readonly IoLane _lane = new("macros-test-io");
    private readonly List<IDisposable> _owned = new();

    private sealed class Rig
    {
        public required MacroService Service { get; init; }
        public required TestCommandService Commands { get; init; }
        public required RecordingWindowService Window { get; init; }
        public required InMemoryStatusBarService Status { get; init; }
        public required TestContextKeyService Keys { get; init; }
        public required ContributionRegistry Contributions { get; init; }
        public required RecordingNavigationService Navigation { get; init; }
        public required TestConfigurationService Config { get; init; }
        public required FakeEditorHost Host { get; init; }
        public required MacroStore Store { get; init; }
        public string? StatusText(string id) => Status.Items.FirstOrDefault(i => i.Id == id)?.Text;
    }

    private StateStorage? _state;

    private async Task<Rig> CreateAsync(string text = "", bool start = true)
    {
        var commands = new TestCommandService();
        var window = new RecordingWindowService();
        var status = new InMemoryStatusBarService();
        var keys = new TestContextKeyService();
        var contributions = new ContributionRegistry();
        var navigation = new RecordingNavigationService(new DocumentManager());
        var config = new TestConfigurationService();
        _state ??= Own(new StateStorage(_paths, _lane));
        var watcher = Own(new FileWatcher(_lane));
        var store = Own(new MacroStore(_paths, new LocalFileSystem(_lane), watcher));
        var service = await _ui.Run(() => new MacroService(commands, _ui, store, config, window, status, keys, _state, navigation, contributions));
        _owned.Add(service);
        var host = new FakeEditorHost(text) { Ui = _ui };
        await _ui.Run(() => _owned.Add(service.AttachHost(host)));
        if (start) await service.StartAsync();
        await _ui.DrainAsync();
        return new Rig
        {
            Service = service, Commands = commands, Window = window, Status = status, Keys = keys, Contributions = contributions, Navigation = navigation,
            Config = config, Host = host, Store = store,
        };
    }

    private T Own<T>(T item) where T : IDisposable
    {
        _owned.Add(item);
        return item;
    }

    public async ValueTask DisposeAsync()
    {
        await _ui.Run(() => { for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose(); });
        _lane.Dispose();
        _paths.Dispose();
        _ui.Dispose();
    }

    private static ValueTask<object?> Done() => ValueTask.FromResult<object?>(null);
    private static JsonElement J(object? value) => JsonSerializer.SerializeToElement(value);

    private Task Exec(Rig r, string id, params object?[] args) => _ui.RunAsync(async () => await r.Commands.ExecuteAsync(id, args));

    // ------------------------------------------------------------------------------------------------------------ recording

    [Fact]
    public async Task Recording_keeps_the_users_keys_text_and_commands_and_nothing_else()
    {
        var r = await CreateAsync();
        var ran = new List<string>();
        r.Commands.Register("user.cmd", inv => { ran.Add("user.cmd"); return Done(); });
        r.Commands.Register("inner.cmd", _ => { ran.Add("inner"); return Done(); });
        r.Commands.Register("outer.cmd", async _ => { await r.Commands.ExecuteAsync("inner.cmd"); return null; }); // inner is outer's work
        r.Commands.Register(KnownCommands.CommandPalette, async _ => { await r.Commands.ExecuteAsync("user.cmd", "from palette"); return null; });
        r.Commands.Register("completion.cmd", _ => Done());
        var states = new List<MacroState>();
        using var sub = r.Service.StateChanged.Subscribe(states.Add);

        await Exec(r, MacroCommands.Record);
        Assert.True(r.Service.IsRecording);
        Assert.Equal("$(record) Recording macro…", r.StatusText(MacroCommands.RecordingStatusId));
        Assert.Equal(true, r.Keys.Get(MacroCommands.RecordingContextKey));
        await _ui.Run(() =>
        {
            r.Service.RecordText("ab");
            r.Service.RecordText("c");
            r.Service.RecordKey(KeyStrokeParser.ParseStroke("shift+end"));
        });
        await Exec(r, "user.cmd", J(new { x = 1 }), new Uri("file:///tmp/a.cs"), 3, null);
        await Exec(r, "outer.cmd");
        await Exec(r, KnownCommands.CommandPalette);
        // a command the editor started while it handled a recorded key (a completion item's command) belongs to that key
        await _ui.RunAsync(async () =>
        {
            r.Service.BeginEditorInput();
            var pending = r.Commands.ExecuteAsync("completion.cmd"); // completes (and reports) at once
            r.Commands.Register("later.cmd", async _ => { await Task.Delay(20); return null; });
            var later = r.Commands.ExecuteAsync("later.cmd"); // started by the key, reported after it
            r.Service.EndEditorInput();
            await pending;
            await later;
            return 0;
        });
        await Exec(r, MacroCommands.PlayLast); // refused while recording, never recorded
        await Exec(r, MacroCommands.Record);   // the same key stops
        await _ui.DrainAsync();

        Assert.False(r.Service.IsRecording);
        Assert.Null(r.StatusText(MacroCommands.RecordingStatusId));
        Assert.Equal(false, r.Keys.Get(MacroCommands.RecordingContextKey));
        Assert.Equal(true, r.Keys.Get(MacroCommands.HasLastContextKey));
        var steps = r.Service.Last!.Steps;
        Assert.Equal(
        [
            new TextStep("abc"),
            new KeyStep(KeyStrokeParser.ParseStroke("shift+end")),
            new CommandStep("user.cmd", [J(new { x = 1 }), J("file:///tmp/a.cs"), J(3), J(null)]),
            new CommandStep("outer.cmd"),
            new CommandStep("user.cmd", [J("from palette")]),
        ], steps);
        Assert.Equal([MacroState.Recording, MacroState.Idle], states);
        Assert.Contains(r.Window.Messages, m => m.Message.Contains("stop recording first"));
    }

    [Fact]
    public async Task A_command_started_before_recording_or_with_arguments_that_cannot_be_stored_is_not_recorded()
    {
        var r = await CreateAsync();
        var gate = new TaskCompletionSource();
        r.Commands.Register("slow.cmd", async _ => { await gate.Task; return null; });
        r.Commands.Register("weird.cmd", _ => Done());
        var slow = _ui.RunAsync(async () => await r.Commands.ExecuteAsync("slow.cmd"));

        await Exec(r, MacroCommands.Record);
        gate.SetResult();
        await slow;
        await Exec(r, "weird.cmd", new Action(() => { })); // a delegate argument has no JSON form
        await _ui.DrainAsync();
        Assert.Contains("'weird.cmd' was not recorded", r.Service.StatusMessage);
        await _ui.Run(() => r.Service.RecordText("x"));
        await Exec(r, MacroCommands.Stop);

        Assert.Equal([new TextStep("x")], r.Service.Last!.Steps);
    }

    [Fact]
    public async Task Nothing_is_recorded_while_the_terminal_has_the_keyboard_keys_text_searches_or_commands()
    {
        var r = await CreateAsync();
        var gate = new TaskCompletionSource();
        r.Commands.Register("user.cmd", _ => Done());
        r.Commands.Register("terminal.paste", _ => Done());
        r.Commands.Register("terminal.slow", async _ => { await gate.Task; return null; });
        r.Commands.Register("toggle.terminal", _ => { r.Keys.Set(MacroCommands.RecordingPausedWhen, true); return Done(); });

        await Exec(r, MacroCommands.Record);
        await _ui.Run(() => r.Service.RecordText("a"));
        await Exec(r, "user.cmd");
        await Exec(r, "toggle.terminal");        // started in the editor, but the terminal has the keyboard when it completes
        Assert.True(r.Service.IsRecordingPaused);
        Assert.Equal("$(record) Recording macro… (paused in the terminal)", r.StatusText(MacroCommands.RecordingStatusId));
        await _ui.Run(() =>
        {
            r.Service.RecordText("ls");
            r.Service.RecordKey(KeyStrokeParser.ParseStroke("enter"));
            r.Service.RecordCommand(MacroCommands.Find, J(new { text = "x" }));
        });
        await Exec(r, "terminal.paste");
        var slow = _ui.RunAsync(async () => await r.Commands.ExecuteAsync("terminal.slow")); // started in the terminal...
        await _ui.Run(() => r.Keys.Set(MacroCommands.RecordingPausedWhen, false));
        gate.SetResult();                          // ...and reported after the keyboard went back to the editor
        await slow;
        await _ui.DrainAsync();
        Assert.False(r.Service.IsRecordingPaused);
        Assert.Equal("$(record) Recording macro…", r.StatusText(MacroCommands.RecordingStatusId));
        await _ui.Run(() => r.Service.RecordText("b"));
        await Exec(r, MacroCommands.Stop);

        Assert.Equal([new TextStep("a"), new CommandStep("user.cmd"), new TextStep("b")], r.Service.Last!.Steps);
    }

    [Fact]
    public async Task A_recording_started_in_the_terminal_waits_for_the_editor_and_time_in_the_terminal_is_no_pause()
    {
        var r = await CreateAsync();
        r.Config.Set(MacroSettings.RecordPauses, true);
        r.Keys.Set(MacroCommands.RecordingPausedWhen, true);
        r.Commands.Register("terminal.clear", _ => Done());

        await Exec(r, MacroCommands.Record);       // Ctrl+M, Ctrl+R pressed in the terminal (a chord: it reaches the key bindings)
        Assert.True(r.Service.IsRecordingPaused);
        await Exec(r, "terminal.clear");
        await _ui.Run(() => r.Service.RecordText("echo"));
        await _ui.Run(() => r.Keys.Set(MacroCommands.RecordingPausedWhen, false));
        await _ui.Run(() => r.Service.RecordText("x"));
        await _ui.Run(() => r.Keys.Set(MacroCommands.RecordingPausedWhen, true));
        await Task.Delay(400);                     // in the terminal: not a pause of the macro
        await _ui.Run(() => r.Keys.Set(MacroCommands.RecordingPausedWhen, false));
        await _ui.Run(() => r.Service.RecordText("y"));
        await Exec(r, MacroCommands.Stop);

        Assert.Equal([new TextStep("xy")], r.Service.Last!.Steps);
    }

    [Fact]
    public async Task An_empty_recording_keeps_the_previous_last_macro()
    {
        var r = await CreateAsync();
        await Exec(r, MacroCommands.Record);
        await _ui.Run(() => r.Service.RecordText("first"));
        await Exec(r, MacroCommands.Record);
        await Exec(r, MacroCommands.Record);
        await Exec(r, MacroCommands.Record);

        Assert.Equal([new TextStep("first")], r.Service.Last!.Steps);
        Assert.Contains("nothing was recorded", r.Service.StatusMessage);
    }

    [Fact]
    public async Task The_recording_limit_stops_recording_and_keeps_what_fit()
    {
        var r = await CreateAsync();
        r.Config.Set(MacroSettings.MaxRecordedSteps, 3);
        await Exec(r, MacroCommands.Record);
        await _ui.Run(() =>
        {
            foreach (var k in new[] { "left", "right", "up", "down", "home" }) r.Service.RecordKey(KeyStrokeParser.ParseStroke(k));
        });

        Assert.False(r.Service.IsRecording);
        Assert.Equal(3, r.Service.Last!.Steps.Count);
        Assert.Contains(r.Window.Messages, m => m.Message.Contains("recording limit (3 steps"));
    }

    [Fact]
    public async Task Recording_is_refused_with_the_hosts_hint_and_its_action_runs_on_request()
    {
        var r = await CreateAsync();
        r.Host.Block = new MacroBlock("Vim emulation is on: record with q{a-z}.", "Turn Off Vim", "vim.disable");
        var disabled = 0;
        r.Commands.Register("vim.disable", _ => { disabled++; r.Host.Block = null; return Done(); });
        r.Window.MessageActionResponses.Enqueue("Turn Off Vim");

        await Exec(r, MacroCommands.Record);
        await _ui.DrainAsync();

        Assert.False(r.Service.IsRecording);
        Assert.Contains(r.Window.Messages, m => m.Message.StartsWith("Vim emulation is on"));
        Assert.Equal(1, disabled);
        await Exec(r, MacroCommands.Record);
        Assert.True(r.Service.IsRecording);
    }

    [Fact]
    public async Task Stopping_with_a_reason_explains_and_keeps_the_steps()
    {
        var r = await CreateAsync();
        await Exec(r, MacroCommands.Record);
        await _ui.Run(() => r.Service.RecordText("abc"));
        await _ui.Run(() => r.Service.StopRecording("Recording stopped: Vim emulation was switched on."));

        Assert.Equal([new TextStep("abc")], r.Service.Last!.Steps);
        Assert.Contains(r.Window.Messages, m => m.Message.StartsWith("Recording stopped: Vim emulation was switched on. Macro recorded (1 step)"));
    }

    // ------------------------------------------------------------------------------------------------------------ playing

    [Fact]
    public async Task Play_last_plays_through_the_host_and_reports_runs_and_failures()
    {
        var r = await CreateAsync("x");
        await Exec(r, MacroCommands.PlayLast);
        Assert.Contains("No macro has been recorded yet", r.Service.StatusMessage);

        await Exec(r, MacroCommands.Record);
        await _ui.Run(() => { r.Service.RecordKey(KeyStrokeParser.ParseStroke("end")); r.Service.RecordText("!"); });
        await Exec(r, MacroCommands.Record);
        await Exec(r, MacroCommands.PlayLast);
        await Exec(r, MacroCommands.PlayLast, 3);
        Assert.Equal("x!!!!", r.Host.Text);
        Assert.Contains("played 3 times", r.Service.StatusMessage);
        Assert.Null(r.StatusText(MacroCommands.PlayingStatusId));
        Assert.Equal(false, r.Keys.Get(MacroCommands.PlayingContextKey));

        r.Host.ReadOnly = true;
        await Exec(r, MacroCommands.PlayLast);
        Assert.Contains(r.Window.Messages, m => m.Severity == MessageSeverity.Warning && m.Message.Contains("read-only"));
    }

    [Fact]
    public async Task Play_takes_the_macro_and_the_mode_from_its_argument_or_asks()
    {
        var r = await CreateAsync("a\nb\nc\nd");
        await _ui.Run(() =>
        {
            r.Service.StartRecording();
            r.Service.RecordKey(KeyStrokeParser.ParseStroke("home"));
            r.Service.RecordText("#");
            r.Service.RecordKey(KeyStrokeParser.ParseStroke("down"));
            r.Service.StopRecording();
        });

        await Exec(r, MacroCommands.Play, J(new { times = 2 }));
        Assert.Equal("#a\n#b\nc\nd", r.Host.Text);
        await Exec(r, MacroCommands.Play, "untilEndOfFile");
        Assert.Equal("#a\n#b\n#c\n#d", r.Host.Text);

        // no argument: which macro is clear (only the last), the mode is asked, then the count
        r.Host.MoveCaretToLineStart(0);
        r.Window.QuickPickResponses.Enqueue(1); // "Several times…"
        r.Window.InputResponses.Enqueue("2");
        await Exec(r, MacroCommands.Play);
        Assert.Equal("##a\n##b\n#c\n#d", r.Host.Text);

        r.Window.QuickPickResponses.Enqueue(null); // Esc
        await Exec(r, MacroCommands.Play);
        Assert.Equal("##a\n##b\n#c\n#d", r.Host.Text);
    }

    [Theory]
    [InlineData("times", null, MacroRepeat.Times, null)]
    [InlineData("untilEndOfFile", null, MacroRepeat.UntilEndOfFile, null)]
    [InlineData("untilSearchFails", null, MacroRepeat.UntilSearchFails, null)]
    [InlineData("onSelectedLines", null, MacroRepeat.OnSelectedLines, null)]
    [InlineData("once", null, MacroRepeat.Times, 1)]
    [InlineData("{\"name\":\"x\"}", "x", MacroRepeat.Times, 1)]
    [InlineData("{\"name\":\"x\",\"times\":4}", "x", MacroRepeat.Times, 4)]
    [InlineData("{\"until\":\"endOfFile\"}", null, MacroRepeat.UntilEndOfFile, null)]
    [InlineData("{\"until\":\"searchFails\",\"name\":\"\"}", "", MacroRepeat.UntilSearchFails, null)]
    [InlineData("{\"onSelectedLines\":true}", null, MacroRepeat.OnSelectedLines, null)]
    [InlineData("7", null, MacroRepeat.Times, 7)]
    public void The_play_argument_forms_parse(string json, string? name, MacroRepeat mode, int? times)
    {
        object? arg = json.StartsWith('{') || char.IsDigit(json[0]) ? JsonDocument.Parse(json).RootElement.Clone() : json;
        Assert.Equal((name, (MacroRepeat?)mode, times), MacroService.ParsePlayArgument(arg));
    }

    [Fact]
    public async Task Esc_equivalent_stop_cancels_a_long_playback_and_says_so()
    {
        var r = await CreateAsync();
        await _ui.Run(() =>
        {
            r.Service.StartRecording();
            r.Service.RecordText("x");
            r.Service.RecordKey(KeyStrokeParser.ParseStroke("backspace"));
            r.Service.StopRecording();
        });
        r.Host.StepCost = TimeSpan.FromMilliseconds(0.2);

        var playing = Exec(r, MacroCommands.PlayLast, 1_000_000);
        Assert.True(await RealWait(() => r.Service.State == MacroState.Playing));
        Assert.Equal("$(play) Playing macro… (Esc to stop)", r.StatusText(MacroCommands.PlayingStatusId));
        Assert.Equal(true, r.Keys.Get(MacroCommands.PlayingContextKey));
        await Exec(r, MacroCommands.Stop);
        await playing.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(MacroState.Idle, r.Service.State);
        Assert.Contains("was stopped", r.Service.StatusMessage);
        Assert.Null(r.StatusText(MacroCommands.PlayingStatusId));
        Assert.Contains(r.Host.Text, new[] { "", "x" }); // stopped between two steps: after a whole run, or after its text
    }

    [Fact]
    public async Task Recording_does_not_start_while_a_macro_plays()
    {
        var r = await CreateAsync();
        var gate = new TaskCompletionSource<object?>();
        r.Commands.Register("wait.cmd", _ => new ValueTask<object?>(gate.Task));
        var macro = new Macro("", [new CommandStep("wait.cmd")]);
        var playing = _ui.RunAsync(() => r.Service.PlayAsync(macro));
        Assert.True(await RealWait(() => r.Service.State == MacroState.Playing));

        Assert.False(await _ui.Run(r.Service.StartRecording));
        gate.SetResult(null);
        Assert.Equal(MacroPlayStatus.Completed, (await playing).Status);
    }

    private static async Task<bool> RealWait(Func<bool> condition, int timeoutMs = 10_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) return false;
            await Task.Delay(5);
        }
        return true;
    }

    // ------------------------------------------------------------------------------------------------------------ saved macros

    [Fact]
    public async Task Saving_writes_macros_json_and_registers_a_titled_run_command()
    {
        var r = await CreateAsync("abc");
        await _ui.Run(() =>
        {
            r.Service.StartRecording();
            r.Service.RecordKey(KeyStrokeParser.ParseStroke("end"));
            r.Service.RecordText(";");
            r.Service.StopRecording();
        });
        r.Window.InputResponses.Enqueue("bad name");
        await Exec(r, MacroCommands.Save);
        Assert.False(File.Exists(r.Store.FilePath)); // the input box's validation refuses it; the fake returns it anyway: not saved

        r.Window.InputResponses.Enqueue("add-semicolon");
        await Exec(r, MacroCommands.Save);
        await _ui.DrainAsync();

        var text = await File.ReadAllTextAsync(r.Store.FilePath);
        Assert.Contains("\"name\": \"add-semicolon\"", text);
        Assert.Contains("{ \"text\": \";\" }", text);
        Assert.True(r.Commands.Has("macro.run.add-semicolon"));
        var meta = r.Commands.All.Single(m => m.Id == "macro.run.add-semicolon");
        Assert.Equal(("Run add-semicolon", "Macros"), (meta.Title, meta.Category));
        await Exec(r, "macro.run.add-semicolon");
        Assert.Equal("abc;", r.Host.Text);
        Assert.Contains("Macro saved as 'add-semicolon'", r.Service.StatusMessage ?? r.Status.Items.First().Text);
    }

    [Fact]
    public async Task Editing_macros_json_by_hand_is_picked_up_live_with_its_keys_and_removals()
    {
        var r = await CreateAsync("abc");
        File.WriteAllText(r.Store.FilePath, """
            // hand written
            { "macros": [
                { "name": "one", "key": "ctrl+alt+1", "steps": [ { "text": "1" } ] },
                { "name": "two", "steps": [ { "command": "macro.run.one" }, { "text": "2" } ] }
            ] }
            """);
        Assert.True(await RealWait(() => r.Commands.Has("macro.run.two")), "the watcher did not pick up macros.json");
        await _ui.DrainAsync();
        var binding = Assert.Single(r.Contributions.Of<KeybindingContribution>().Items);
        Assert.Equal(("macro.run.one", "ctrl+alt+1", ContributionSources.User), (binding.Value.Command, binding.Value.Key, binding.SourceId));

        await Exec(r, "macro.run.two"); // a macro running a macro
        Assert.Equal("12abc", r.Host.Text);

        File.WriteAllText(r.Store.FilePath, """{ "macros": [ { "name": "two", "steps": [ { "text": "Z" } ] }, { "name": "oops", "steps": [ { "bad": 1 } ] } ] }""");
        Assert.True(await RealWait(() => !r.Commands.Has("macro.run.one")), "the removed macro's command stayed");
        await _ui.DrainAsync();
        Assert.Empty(r.Contributions.Of<KeybindingContribution>().Items);
        Assert.Contains(r.Window.Messages, m => m.Severity == MessageSeverity.Warning && m.Message.Contains("unknown property \"bad\""));
        await Exec(r, "macro.run.two");
        Assert.Equal("12Zabc", r.Host.Text);
        Assert.Equal(["two"], r.Service.Named.Select(m => m.Name));
    }

    [Fact]
    public async Task Edit_creates_macros_json_with_its_guide_and_opens_it()
    {
        var r = await CreateAsync();
        await Exec(r, MacroCommands.Edit);

        Assert.StartsWith("// CsCode macros", await File.ReadAllTextAsync(r.Store.FilePath));
        Assert.Equal(r.Store.FileUri, Assert.Single(r.Navigation.Opened).Uri);
    }

    [Fact]
    public async Task A_broken_macros_json_is_reported_and_never_overwritten_by_save()
    {
        var r = await CreateAsync();
        File.WriteAllText(r.Store.FilePath, "{ \"macros\": [ oops");
        await r.Store.ReloadAsync();
        await _ui.DrainAsync();
        Assert.Contains(r.Window.Messages, m => m.Message.Contains("not valid JSON"));

        await _ui.Run(() => { r.Service.StartRecording(); r.Service.RecordText("x"); r.Service.StopRecording(); });
        Assert.False(await _ui.RunAsync(() => r.Service.SaveLastAsync("x")));
        Assert.Equal("{ \"macros\": [ oops", await File.ReadAllTextAsync(r.Store.FilePath));
        Assert.Contains(r.Window.Messages, m => m.Severity == MessageSeverity.Error && m.Message.Contains("fix it first"));
    }

    [Fact]
    public async Task The_last_macro_and_the_saved_ones_survive_a_restart()
    {
        var first = await CreateAsync();
        await _ui.Run(() => { first.Service.StartRecording(); first.Service.RecordText("persisted"); first.Service.StopRecording(); });
        Assert.True(await _ui.RunAsync(() => first.Service.SaveLastAsync("kept")));
        Assert.True(await RealWait(() => _state!.Get<string>(MacroService.LastMacroStateKey) is not null));
        await _state!.FlushAsync();
        _state = null; // the next composition reads the state from disk, as after a restart

        var second = await CreateAsync();
        Assert.True(await RealWait(() => second.Service.Last is not null));
        Assert.Equal([new TextStep("persisted")], second.Service.Last!.Steps);
        Assert.Equal(["kept"], second.Service.Named.Select(m => m.Name));
        Assert.True(second.Commands.Has("macro.run.kept"));
    }

    [Fact]
    public async Task A_disposed_service_unregisters_its_commands_and_is_not_retained()
    {
        var commands = new TestCommandService();
        var store = Own(new MacroStore(_paths, new LocalFileSystem(_lane)));
        await LeakAssert.NotRetainedAsync(async () =>
        {
            var service = await _ui.Run(() => new MacroService(commands, _ui, store));
            await service.StartAsync();
            await _ui.Run(() => { service.StartRecording(); service.RecordText("x"); });
            await _ui.Run(service.Dispose);
            Assert.False(commands.Has(MacroCommands.Record));
            await _ui.DrainAsync();
            return [service];
        });
    }
}
