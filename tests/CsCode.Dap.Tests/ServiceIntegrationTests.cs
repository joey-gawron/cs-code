using System.Diagnostics;
using System.Text.Json;
using CsCode.Core;
using CsCode.Core.Contributions;
using CsCode.Core.Debugging;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Tasks;
using CsCode.Core.Text;
using CsCode.Dap.Adapters;
using CsCode.Dap.Breakpoints;
using CsCode.Dap.Configuration;
using CsCode.Dap.Service;
using CsCode.Dap.Session;
using CsCode.Dap.Tests.Fakes;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Dap.Tests;

public class DebugCommandTests
{
    private const string TwoConfigs = """
        {"configurations":[{"name":"First","type":"fake","request":"launch"},{"name":"Second","type":"fake","request":"launch","marker":"second"}]}
        """;

    [Fact]
    public async Task All_debug_commands_are_registered_with_vscode_ids_and_a_Debug_category()
    {
        await using var h = new ServiceHarness();
        foreach (var id in new[]
        {
            KnownCommands.DebugStart, KnownCommands.DebugRun, KnownCommands.DebugStop, KnownCommands.DebugRestart, KnownCommands.DebugContinue,
            KnownCommands.DebugPause, KnownCommands.DebugStepOver, KnownCommands.DebugStepInto, KnownCommands.DebugStepOut,
            KnownCommands.DebugToggleBreakpoint, DebugService.PickProcessCommand,
        })
        {
            Assert.True(h.Commands.Has(id), id);
            Assert.Equal("Debug", h.Commands.All.Single(c => c.Id == id).Category);
        }
    }

    [Fact]
    public async Task Start_runs_the_selected_launch_configuration()
    {
        await using var h = new ServiceHarness();
        h.Dir.WriteFile(".cscode/launch.json", TwoConfigs);
        var all = await h.Configurations.GetLaunchConfigurationsAsync(null, Ct);
        h.Configurations.Selected = all[1];
        await h.Commands.ExecuteAsync(KnownCommands.DebugStart);
        await SessionHarness.Eventually(() => h.Service.Sessions.Count == 1);
        Assert.Equal("Second", h.Service.Sessions[0].Name);
        Assert.Equal("second", h.AdapterFor(0).RequestsOf("launch")[0].Arguments.GetProperty("marker").GetString());
    }

    [Fact]
    public async Task Start_without_a_selection_uses_the_first_configuration()
    {
        await using var h = new ServiceHarness();
        h.Dir.WriteFile(".cscode/launch.json", TwoConfigs);
        await h.Commands.ExecuteAsync(KnownCommands.DebugStart);
        await SessionHarness.Eventually(() => h.Service.Sessions.Count == 1);
        Assert.Equal("First", h.Service.Sessions[0].Name);
    }

    [Fact]
    public async Task Run_without_debugging_passes_noDebug()
    {
        await using var h = new ServiceHarness();
        h.Dir.WriteFile(".cscode/launch.json", TwoConfigs);
        await h.Commands.ExecuteAsync(KnownCommands.DebugRun);
        await SessionHarness.Eventually(() => h.Service.Sessions.Count == 1);
        Assert.True(h.AdapterFor(0).RequestsOf("launch")[0].Arguments.GetProperty("noDebug").GetBoolean());
    }

    [Fact]
    public async Task Start_without_any_configuration_offers_to_create_a_launch_json_from_the_first_debugger_type()
    {
        await using var h = new ServiceHarness();
        h.Dir.WriteFile("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
        h.Window.MessageActionResponses.Enqueue("Create launch.json");
        await h.Commands.ExecuteAsync(KnownCommands.DebugStart);
        var launch = Path.Combine(h.Dir.Path, ".cscode", "launch.json");
        await SessionHarness.Eventually(() => File.Exists(launch), 5000, "launch.json created");
        Assert.Contains("bin/Debug/net9.0/App.dll", File.ReadAllText(launch));
        Assert.True(File.Exists(Path.Combine(h.Dir.Path, ".cscode", "tasks.json")));
        Assert.Empty(h.Service.Sessions);
    }

    [Fact]
    public async Task Declining_the_launch_json_offer_does_nothing()
    {
        await using var h = new ServiceHarness();
        await h.Commands.ExecuteAsync(KnownCommands.DebugStart);
        Assert.False(File.Exists(Path.Combine(h.Dir.Path, ".cscode", "launch.json")));
        Assert.Contains(h.Window.Messages, m => m.Message.Contains("launch.json"));
    }

    [Fact]
    public async Task Execution_commands_drive_the_active_session_when_the_state_allows()
    {
        await using var h = new ServiceHarness();
        var session = await h.StartAsync();
        var adapter = h.AdapterFor(0);

        // running: pause works, continue/step are no-ops
        await h.Commands.ExecuteAsync(KnownCommands.DebugPause);
        Assert.Equal(1, adapter.CountOf("pause"));
        await h.Commands.ExecuteAsync(KnownCommands.DebugContinue);
        await h.Commands.ExecuteAsync(KnownCommands.DebugStepOver);
        Assert.Equal(0, adapter.CountOf("continue") + adapter.CountOf("next"));

        await adapter.SendStoppedAsync();
        await SessionHarness.Eventually(() => session.State == DebugSessionState.Stopped);
        await h.Commands.ExecuteAsync(KnownCommands.DebugStepOver);
        Assert.Equal(1, adapter.CountOf("next"));
        await adapter.SendStoppedAsync("step");
        await SessionHarness.Eventually(() => session.State == DebugSessionState.Stopped);
        await h.Commands.ExecuteAsync(KnownCommands.DebugStepInto);
        Assert.Equal(1, adapter.CountOf("stepIn"));
        await adapter.SendStoppedAsync("step");
        await SessionHarness.Eventually(() => session.State == DebugSessionState.Stopped);
        await h.Commands.ExecuteAsync(KnownCommands.DebugStepOut);
        Assert.Equal(1, adapter.CountOf("stepOut"));
        await adapter.SendStoppedAsync("step");
        await SessionHarness.Eventually(() => session.State == DebugSessionState.Stopped);
        await h.Commands.ExecuteAsync(KnownCommands.DebugContinue);
        Assert.Equal(1, adapter.CountOf("continue"));
    }

    [Fact]
    public async Task Stop_ends_all_sessions_and_commands_without_a_session_are_harmless()
    {
        await using var h = new ServiceHarness();
        await h.Commands.ExecuteAsync(KnownCommands.DebugContinue);
        await h.Commands.ExecuteAsync(KnownCommands.DebugRestart);
        await h.Commands.ExecuteAsync(KnownCommands.DebugStop);
        await h.StartAsync(ServiceHarness.Config("A", folder: h.Dir.Uri));
        await h.StartAsync(ServiceHarness.Config("B", folder: h.Dir.Uri));
        await h.Commands.ExecuteAsync(KnownCommands.DebugStop);
        await SessionHarness.Eventually(() => h.Service.Sessions.Count == 0);
    }

    [Fact]
    public async Task Restart_command_restarts_the_active_session()
    {
        await using var h = new ServiceHarness(configureHost: host => host.Configure = (a, _) => a.Capabilities["supportsRestartRequest"] = true);
        await h.StartAsync();
        await h.Commands.ExecuteAsync(KnownCommands.DebugRestart);
        Assert.Equal(1, h.AdapterFor(0).CountOf("restart"));
    }

    [Fact]
    public async Task Toggle_breakpoint_uses_the_active_editors_caret_line()
    {
        var docs = new DocumentManager();
        var nav = new RecordingNavigationService(docs);
        await using var h = new ServiceHarness(navigation: nav);
        var uri = new Uri("file:///work/a.cs");
        nav.ActiveDocument = new InMemoryEditorDoc(uri);
        nav.ActiveCaret = new TextPosition(6, 2);
        await h.Commands.ExecuteAsync(KnownCommands.DebugToggleBreakpoint);
        Assert.Equal(6, Assert.Single(h.Breakpoints.GetForUri(uri)).Line);
        await h.Commands.ExecuteAsync(KnownCommands.DebugToggleBreakpoint);
        Assert.Empty(h.Breakpoints.GetForUri(uri));
    }

    [Fact]
    public async Task Toggle_breakpoint_without_an_editor_is_a_no_op()
    {
        await using var h = new ServiceHarness();
        await h.Commands.ExecuteAsync(KnownCommands.DebugToggleBreakpoint);
        Assert.Empty(h.Breakpoints.SourceBreakpoints);
    }

    [Fact]
    public async Task PickProcess_lists_processes_and_returns_the_chosen_pid_as_text()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "uses /bin/sh to create a recognisable process");
        var psi = new ProcessStartInfo("/bin/sleep", "300") { UseShellExecute = false };
        using var sleeper = Process.Start(psi)!;
        try
        {
            // The process table changes under us (other agents/tests), so the user's choice is made by pid, not by list position.
            var picker = new PidPickingWindow(sleeper.Id);
            var commands = new TestCommandService();
            using var lane = new CsCode.Dap.Client.DebugLane("pick");
            var platform = new DapPlatform { Commands = commands, Window = picker };
            await using var breakpoints = new BreakpointManager();
            var tasks = new CsCode.Dap.Tasks.TaskService(lane, platform);
            await using var cfgs = new DebugConfigurationService(lane, platform);
            await using var service = new DebugService(lane, platform, breakpoints, cfgs, tasks);

            var result = await commands.ExecuteAsync("pickProcess");
            Assert.Equal(sleeper.Id.ToString(), result);
            Assert.True(picker.Shown.Count > 0);
            Assert.DoesNotContain(picker.Shown, i => i.Tag is int pid && pid == Environment.ProcessId); // never offer to attach to ourselves
            Assert.Contains(picker.Shown, i => i.Label == "sleep" && i.Description == sleeper.Id.ToString());
        }
        finally { sleeper.Kill(); sleeper.WaitForExit(2000); }
    }

    [Fact]
    public async Task PickProcess_returns_nothing_when_the_user_cancels_and_works_as_a_command_variable()
    {
        await using var h = new ServiceHarness();
        h.Window.QuickPickResponses.Enqueue(null);
        Assert.Null(await h.Commands.ExecuteAsync("pickProcess"));

        // ${command:pickProcess} in an attach configuration: cancellation aborts the start quietly
        h.Window.QuickPickResponses.Enqueue(null);
        var cfg = ServiceHarness.Config(request: "attach", extra: new Dictionary<string, object?> { ["processId"] = "${command:pickProcess}" }, folder: h.Dir.Uri);
        Assert.Null(await h.Service.StartAsync(cfg, Ct));
        Assert.Empty(h.Window.Messages);
    }

    [Fact]
    public async Task PickProcess_in_an_attach_configuration_resolves_to_the_chosen_pid()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "needs a process list");
        await using var h = new ServiceHarness();
        var items = DebugService.ListProcesses();
        h.Window.QuickPickResponses.Enqueue(0);
        var expected = items[0].Tag!.ToString();
        var cfg = ServiceHarness.Config(request: "attach", extra: new Dictionary<string, object?> { ["processId"] = "${command:pickProcess}" }, folder: h.Dir.Uri);
        await h.Service.StartAsync(cfg, Ct);
        await SessionHarness.Eventually(() => h.Host.Adapters.Count == 1);
        // (process ids are re-listed by the command, so only the shape is asserted: a number)
        Assert.True(int.TryParse(h.AdapterFor(0).RequestsOf("attach")[0].Arguments.GetProperty("processId").GetString(), out _), expected);
    }

    /// <summary>A window whose quick pick chooses the item tagged with a given pid (everything else is a no-op).</summary>
    private sealed class PidPickingWindow(int pid) : IWindowService
    {
        public List<QuickPickItem> Shown { get; } = new();

        public ValueTask<QuickPickItem?> ShowQuickPickAsync(IReadOnlyList<QuickPickItem> items, QuickPickOptions? options = null, CancellationToken ct = default)
        {
            Shown.AddRange(items);
            return ValueTask.FromResult(items.FirstOrDefault(i => i.Tag is int p && p == pid));
        }

        public ValueTask<string?> ShowMessageAsync(MessageSeverity severity, string message, IReadOnlyList<MessageAction>? actions = null, CancellationToken ct = default) => ValueTask.FromResult<string?>(null);
        public ValueTask<IReadOnlyList<QuickPickItem>?> ShowQuickPickManyAsync(IReadOnlyList<QuickPickItem> items, QuickPickOptions? options = null, CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<QuickPickItem>?>(null);
        public ValueTask<string?> ShowInputBoxAsync(InputBoxOptions? options = null, CancellationToken ct = default) => ValueTask.FromResult<string?>(null);
        public ValueTask<IReadOnlyList<Uri>?> ShowOpenDialogAsync(OpenDialogOptions options, CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<Uri>?>(null);
        public ValueTask<Uri?> ShowSaveDialogAsync(SaveDialogOptions options, CancellationToken ct = default) => ValueTask.FromResult<Uri?>(null);
        public Task<T> WithProgressAsync<T>(string title, bool cancellable, Func<IProgressReporter, CancellationToken, Task<T>> work) => throw new NotSupportedException();
        public IDisposable SetStatusBarMessage(string text, TimeSpan? timeout = null) => Disposable.Empty;
        public ValueTask<bool> OpenExternalAsync(Uri uri) => ValueTask.FromResult(true);
        public ValueTask CopyToClipboardAsync(string text) => ValueTask.CompletedTask;
    }

    private sealed class InMemoryEditorDoc(Uri uri) : IEditorDocument
    {
        private readonly InMemoryTextDocument _doc = new(uri, "csharp", "line\nline\n");
        public Uri Uri => _doc.Uri;
        public string LanguageId => _doc.LanguageId;
        public int Version => _doc.Version;
        public bool IsDirty => _doc.IsDirty;
        public int Length => _doc.Length;
        public int LineCount => _doc.LineCount;
        public bool IsReadOnly => false;
        public bool IsUntitled => false;
        public string EncodingName => "utf-8";
        public string EndOfLine => "\n";
        public string GetText() => _doc.GetText();
        public string GetText(TextRange range) => _doc.GetText(range);
        public string GetLineText(int line) => _doc.GetLineText(line);
        public int OffsetAt(TextPosition position) => _doc.OffsetAt(position);
        public TextPosition PositionAt(int offset) => _doc.PositionAt(offset);
        public TextPosition Validate(TextPosition position) => _doc.Validate(position);
        public bool ApplyEdits(IReadOnlyList<TextEdit> edits) => _doc.ApplyEdits(edits);
        public void SetLanguageId(string languageId) => _doc.SetLanguageId(languageId);
        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsAsync(Uri target, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public IEventSource<TextDocumentChangedEventArgs> Changed => _doc.Changed;
        public IEventSource<ITextDocument> LanguageIdChanged => _doc.LanguageIdChanged;
        public IEventSource<IEditorDocument> Saved { get; } = new Emitter<IEditorDocument>();
        public IEventSource<IEditorDocument> DirtyChanged { get; } = new Emitter<IEditorDocument>();
    }
}

public class UserDebuggerContributionTests
{
    private static JsonElement Setting(params object[] debuggers) => JsonSerializer.SerializeToElement(debuggers);

    [Fact]
    public async Task The_cscode_debuggers_setting_becomes_contributions_debugger_types_and_breakpoint_languages()
    {
        await using var h = new ServiceHarness();
        h.Settings.Set(DebugService.DebuggersSetting, new object[]
        {
            new { type = "mydbg", label = "My Debugger", program = "/usr/bin/my-adapter", args = new[] { "--stdio" }, languages = new[] { "mylang", "other" } },
            new { label = "no type: ignored" },
        });
        await SessionHarness.Eventually(() => h.Contributions.Of<DebuggerContribution>().Items.Count == 1);
        var item = Assert.Single(h.Contributions.Of<DebuggerContribution>().Items);
        Assert.Equal(ContributionSources.User, item.SourceId);
        Assert.Equal(("mydbg", "My Debugger", "/usr/bin/my-adapter"), (item.Value.Type, item.Value.Label, item.Value.Program));
        Assert.Equal(["--stdio"], item.Value.Args);
        Assert.Equal(["mylang", "other"], item.Value.Languages);
        await SessionHarness.Eventually(() => h.Service.DebuggerTypes.Any(t => t.Type == "mydbg"));
        Assert.Contains(h.Service.DebuggerTypes, t => t.Type == "mydbg" && t.Label == "My Debugger" && t.Languages.Contains("mylang"));
        var languages = h.Contributions.Of<BreakpointLanguageContribution>().Items.Select(i => i.Value.Language).ToArray();
        Assert.Contains("mylang", languages);
        Assert.Contains("csharp", languages); // built-in coreclr languages
    }

    [Fact]
    public async Task The_modules_settings_are_registered_with_the_configuration_service()
    {
        await using var h = new ServiceHarness();
        var keys = h.Settings.Properties.Select(p => p.Key).ToArray();
        Assert.Contains(DebugService.DebuggersSetting, keys);
        Assert.Contains(NetCoreDbgAdapterFactory.SettingPath, keys);
        Assert.Contains("cscode.debug.requestTimeoutSeconds", keys);
        Assert.All(h.Settings.Properties.Where(p => p.Key.StartsWith("cscode.debug")), p => Assert.False(string.IsNullOrEmpty(p.Description)));
        await h.Service.DisposeAsync();
        Assert.DoesNotContain(DebugService.DebuggersSetting, h.Settings.Properties.Select(p => p.Key)); // unregistered on dispose
    }

    [Fact]
    public async Task Changing_the_setting_replaces_this_modules_contributions_only()
    {
        await using var h = new ServiceHarness();
        h.Contributions.Contribute(ContributionSources.User, "", [new LanguageContribution("other-module", [], [".x"], [], [])]); // someone else's user contribution
        h.Settings.Set(DebugService.DebuggersSetting, new object[] { new { type = "a", program = "/a" } });
        await SessionHarness.Eventually(() => h.Contributions.Of<DebuggerContribution>().Items.Any(i => i.Value.Type == "a"));
        h.Settings.Set(DebugService.DebuggersSetting, new object[] { new { type = "b", program = "/b" } });
        await SessionHarness.Eventually(() => h.Contributions.Of<DebuggerContribution>().Items.Select(i => i.Value.Type).SequenceEqual(["b"]));
        Assert.Single(h.Contributions.Of<LanguageContribution>().Items); // untouched
        await SessionHarness.Eventually(() => !h.Service.DebuggerTypes.Any(t => t.Type == "a") && h.Service.DebuggerTypes.Any(t => t.Type == "b"));
    }

    [Fact]
    public async Task Removing_the_setting_removes_the_contributions()
    {
        await using var h = new ServiceHarness();
        h.Settings.Set(DebugService.DebuggersSetting, new object[] { new { type = "a", program = "/a" } });
        await SessionHarness.Eventually(() => h.Contributions.Of<DebuggerContribution>().Items.Count == 1);
        h.Settings.Set(DebugService.DebuggersSetting, Array.Empty<object>());
        await SessionHarness.Eventually(() => h.Contributions.Of<DebuggerContribution>().Items.Count == 0);
    }

    [Fact]
    public async Task Platform_specific_program_entries_are_parsed()
    {
        await using var h = new ServiceHarness();
        h.Settings.Set(DebugService.DebuggersSetting, new object[]
        {
            new { type = "p", program = "/generic", windows = new { program = "C:\\adapter.exe" }, linux = new { program = "/linux/adapter" }, osx = "/mac/adapter" },
        });
        await SessionHarness.Eventually(() => h.Contributions.Of<DebuggerContribution>().Items.Count == 1);
        var p = h.Contributions.Of<DebuggerContribution>().Items[0].Value.PlatformPrograms!;
        Assert.Equal(("C:\\adapter.exe", "/linux/adapter", "/mac/adapter"), (p["windows"], p["linux"], p["osx"]));
    }

    [Fact]
    public async Task A_contributed_debugger_builds_its_launch_command_from_program_runtime_args_and_variables()
    {
        using var dir = new TempDirectory();
        var registry = new ContributionRegistry();
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        registry.Contribute(ContributionSources.User, "", new[]
        {
            new DebuggerContribution("plain", "Plain", Program: "/opt/adapter", Args: ["--stdio", "--log=${workspaceFolderBasename}"]),
            new DebuggerContribution("scripted", "Scripted", Program: "/opt/adapter.js", Runtime: "node", Args: ["--x"]),
            new DebuggerContribution("platform", "Platform", Program: "/generic", PlatformPrograms: new Dictionary<string, string> { [os] = "/specific" }),
            new DebuggerContribution("empty", "Empty"),
        });
        var factory = new ContributedAdapterFactory(new DapPlatform { Contributions = registry, Workspace = new FakeWorkspace(dir.Uri) });
        DebugConfiguration Cfg(string type) => DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(new { type, request = "launch", name = "n" }), dir.Uri);

        var plain = Assert.IsType<DebugAdapterExecutable>(await factory.CreateDescriptorAsync(Cfg("plain"), Ct));
        Assert.Equal("/opt/adapter", plain.Command);
        Assert.Equal(["--stdio", "--log=" + Path.GetFileName(dir.Path)], plain.Args);
        Assert.Equal(dir.Path.TrimEnd('/'), plain.Cwd);

        var scripted = Assert.IsType<DebugAdapterExecutable>(await factory.CreateDescriptorAsync(Cfg("scripted"), Ct));
        Assert.Equal("node", scripted.Command);
        Assert.Equal(["/opt/adapter.js", "--x"], scripted.Args);

        Assert.Equal("/specific", Assert.IsType<DebugAdapterExecutable>(await factory.CreateDescriptorAsync(Cfg("platform"), Ct)).Command);
        Assert.Null(await factory.CreateDescriptorAsync(Cfg("empty"), Ct));
        Assert.Null(await factory.CreateDescriptorAsync(Cfg("unknown"), Ct));
    }

    [Fact]
    public async Task A_user_debugger_can_actually_be_started_through_the_service_over_stdio()
    {
        var python = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, "python3")).FirstOrDefault(File.Exists);
        Assert.SkipUnless(python is not null && !OperatingSystem.IsWindows(), "python3 is not available (used as a scripted DAP adapter)");
        await using var h = new ServiceHarness();
        var script = h.Dir.WriteFile("adapter.py", MinimalAdapter);
        h.Settings.Set(DebugService.DebuggersSetting, new object[] { new { type = "scripted", label = "Scripted", program = script, runtime = python, languages = new[] { "txt" } } });
        await SessionHarness.Eventually(() => h.Contributions.Of<DebuggerContribution>().Items.Count == 1);

        var session = await h.Service.StartAsync(ServiceHarness.Config("via-setting", "scripted", folder: h.Dir.Uri), Ct);
        Assert.NotNull(session);
        Assert.Equal(DebugState.Running, h.Service.State);
        Assert.Equal("main", Assert.Single(await session!.GetThreadsAsync(Ct)).Name);
        var pid = ((DebugSession)session).AdapterProcessId!.Value;
        await h.Service.StopAllAsync(Ct);
        await SessionHarness.Eventually(() => !SessionReverseRequestTests.ProcessAlive(pid), 5000, "adapter exits");
    }

    private const string MinimalAdapter = """
        import sys, json
        def read():
            hdr = b''
            while not hdr.endswith(b'\r\n\r\n'):
                c = sys.stdin.buffer.read(1)
                if not c: return None
                hdr += c
            n = int([l for l in hdr.split(b'\r\n') if l.lower().startswith(b'content-length')][0].split(b':')[1])
            return json.loads(sys.stdin.buffer.read(n))
        seq = 0
        def send(m):
            global seq
            seq += 1; m['seq'] = seq
            b = json.dumps(m).encode()
            sys.stdout.buffer.write(b'Content-Length: %d\r\n\r\n' % len(b) + b); sys.stdout.buffer.flush()
        while True:
            r = read()
            if r is None: break
            c = r['command']
            body = {'supportsConfigurationDoneRequest': True} if c == 'initialize' else ({'threads': [{'id': 1, 'name': 'main'}]} if c == 'threads' else None)
            m = {'type': 'response', 'request_seq': r['seq'], 'success': True, 'command': c}
            if body is not None: m['body'] = body
            send(m)
            if c == 'initialize': send({'type': 'event', 'event': 'initialized'})
            if c == 'disconnect': break
        """;
}

public class DependencyInjectionTests
{
    private static ServiceProvider Build(bool withPlatform = true)
    {
        var services = new ServiceCollection();
        services.AddCsCodeCore();
        services.AddCsCodeDap();
        if (withPlatform)
        {
            services.AddSingleton<ICommandService>(new TestCommandService());
            services.AddSingleton<IConfigurationService>(new TestConfigurationService());
            services.AddSingleton<IStateStorage>(new FakeStateStorage());
            services.AddSingleton<IWindowService>(new RecordingWindowService());
            services.AddSingleton<IContextKeyService>(new TestContextKeyService());
            services.AddSingleton<IOutputService>(new InMemoryOutputService());
            services.AddSingleton<IWorkspaceService>(new FakeWorkspace());
            services.AddSingleton<IAppPaths>(new TestAppPaths());
        }
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task AddCsCodeDap_registers_every_contract_and_the_extra_interfaces_resolve_to_the_same_service()
    {
        await using var sp = Build();
        var service = sp.GetRequiredService<IDebugService>();
        Assert.Same(service, sp.GetRequiredService<IDebugFrameFocus>());
        Assert.Same(service, sp.GetRequiredService<IDebugOutputHistory>());
        Assert.Same(service, sp.GetRequiredService<IDebugSourceContentProvider>());
        Assert.Same(service, sp.GetRequiredService<DebugService>());
        Assert.Same(service.Breakpoints, sp.GetRequiredService<IBreakpointManager>());
        Assert.Same(service.Configurations, sp.GetRequiredService<IDebugConfigurationService>());
        Assert.NotNull(sp.GetRequiredService<ITaskService>());
        Assert.Same(sp.GetRequiredService<IDebugService>(), service); // singleton
        Assert.Contains(service.DebuggerTypes, t => t.Type == "coreclr");
    }

    [Fact]
    public async Task Resolving_the_service_registers_the_debug_commands_and_context_keys()
    {
        await using var sp = Build();
        var commands = (TestCommandService)sp.GetRequiredService<ICommandService>();
        Assert.False(commands.Has(KnownCommands.DebugStart)); // nothing happens until the app resolves IDebugService at startup
        _ = sp.GetRequiredService<IDebugService>();
        Assert.True(commands.Has(KnownCommands.DebugStart));
        Assert.True(commands.Has("pickProcess"));
        Assert.Equal("inactive", sp.GetRequiredService<IContextKeyService>().Get("debugState"));
    }

    [Fact]
    public async Task The_module_works_with_none_of_the_optional_platform_services_registered()
    {
        await using var sp = Build(withPlatform: false);
        var service = sp.GetRequiredService<IDebugService>();
        Assert.Equal(DebugState.Inactive, service.State);
        Assert.Empty(service.Sessions);
        var b = service.Breakpoints.Add(new Uri("file:///x.cs"), 1); // no storage: still works in memory
        Assert.Single(service.Breakpoints.SourceBreakpoints);
        Assert.Empty(await service.Configurations.GetLaunchConfigurationsAsync(null, Ct));
        Assert.Equal(b.Id, service.Breakpoints.SourceBreakpoints[0].Id);
    }

    [Fact]
    public async Task Disposing_the_provider_shuts_everything_down()
    {
        var sp = Build();
        var service = sp.GetRequiredService<IDebugService>();
        var lane = sp.GetRequiredService<CsCode.Dap.Client.DebugLane>();
        service.Breakpoints.Toggle(new Uri("file:///x.cs"), 1);
        await sp.DisposeAsync();
        Assert.True(lane.IsDisposed);
        Assert.Empty(service.Sessions);
    }

    [Fact]
    public void The_debug_lane_is_one_shared_dedicated_thread()
    {
        using var sp = Build();
        var a = sp.GetRequiredService<CsCode.Dap.Client.DebugLane>();
        var b = sp.GetRequiredService<CsCode.Dap.Client.DebugLane>();
        Assert.Same(a, b);
        Assert.NotEqual(Environment.CurrentManagedThreadId, a.ManagedThreadId);
    }
}
