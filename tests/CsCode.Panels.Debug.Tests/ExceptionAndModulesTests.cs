using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using CsCode.Core.Debugging;
using CsCode.Panels.Debug.Exceptions;
using CsCode.Panels.Debug.Modules;
using CsCode.Panels.Debug.Tests.Fakes;
using CsCode.Panels.Debug.ToolWindows;
using CsCode.Ui.Theming;
using Xunit;

namespace CsCode.Panels.Debug.Tests;

public class ExceptionAndModulesTests
{
    private const string ExceptionInfoJson = """
        {
          "exceptionId": "System.InvalidOperationException",
          "description": "Operation is not valid",
          "breakMode": "unhandled",
          "details": {
            "message": "Sequence contains no elements",
            "typeName": "InvalidOperationException",
            "fullTypeName": "System.InvalidOperationException",
            "stackTrace": "   at System.Linq.Enumerable.First[T](IEnumerable`1 source)\n   at Sample.Program.Main() in /work/Program.cs:line 25",
            "innerException": [
              { "message": "inner boom", "fullTypeName": "System.ArgumentException", "stackTrace": "   at Sample.Inner.Run()", "innerException": [ { "message": "root cause", "typeName": "FormatException" } ] }
            ]
          }
        }
        """;

    private static JsonElement? Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [AvaloniaFact]
    public async Task An_exception_stop_shows_type_message_stack_and_the_inner_chain()
    {
        using var rig = new PanelRig();
        rig.Session.CustomHandler = (command, _) => command == "exceptionInfo" ? Json(ExceptionInfoJson) : null;
        using var vm = new ExceptionHelperViewModel(rig.Services);
        Assert.False(vm.HasException);

        rig.StopAt(0, "exception");
        await PanelRig.Eventually(() => vm.HasException, what: "exception details");
        Assert.Equal("Unhandled Exception", vm.Title);
        Assert.Equal(3, vm.Chain.Count);
        Assert.Equal("System.InvalidOperationException", vm.Chain[0].TypeName);
        Assert.Equal("Sequence contains no elements", vm.Chain[0].Message);
        Assert.Contains("Sample.Program.Main()", vm.Chain[0].StackTrace);
        Assert.Equal(["System.ArgumentException", "FormatException"], vm.Chain.Skip(1).Select(c => c.TypeName));
        Assert.Equal([0, 1, 2], vm.Chain.Select(c => c.Depth));
    }

    [AvaloniaFact]
    public async Task Copy_details_copies_the_whole_chain_and_continue_resumes()
    {
        using var rig = new PanelRig();
        rig.Session.CustomHandler = (command, _) => command == "exceptionInfo" ? Json(ExceptionInfoJson) : null;
        using var vm = new ExceptionHelperViewModel(rig.Services);
        rig.StopAt(0, "exception");
        await PanelRig.Eventually(() => vm.HasException);

        await vm.CopyCommand.ExecuteAsync();
        var text = Assert.Single(rig.Window.Clipboard);
        Assert.StartsWith("System.InvalidOperationException: Sequence contains no elements", text);
        Assert.Contains("inner exception", text);
        Assert.Contains("root cause", text);
        Assert.Contains("   at Sample.Program.Main()", text);

        await vm.ContinueCommand.ExecuteAsync();
        Assert.Equal(["continue"], rig.Session.StepLog);
        await PanelRig.Eventually(() => !vm.HasException, what: "cleared on resume");
    }

    [AvaloniaFact]
    public async Task An_adapter_without_exception_info_falls_back_to_the_stop_description()
    {
        using var rig = new PanelRig();
        rig.Session.Capabilities = rig.Session.Capabilities with { SupportsExceptionInfo = false };
        using var vm = new ExceptionHelperViewModel(rig.Services);
        rig.StopAt(0, "exception", "Paused on exception", "System.NullReferenceException: Object reference not set");
        await PanelRig.Eventually(() => vm.HasException);
        Assert.Contains("NullReferenceException", vm.Chain[0].Message);
        Assert.Equal(0, rig.Session.CustomHandler is null ? 0 : 1);
    }

    [AvaloniaFact]
    public async Task A_breakpoint_stop_is_not_an_exception()
    {
        using var rig = new PanelRig();
        using var vm = new ExceptionHelperViewModel(rig.Services);
        rig.StopAt(0, "breakpoint");
        await PanelRig.Eventually(() => vm.StatusText == "Stopped, not on an exception.");
        Assert.False(vm.HasException);
        Assert.True(vm.ShowStatus);
    }

    [AvaloniaFact]
    public async Task The_runtime_keeps_the_exception_context_key_for_auto_show()
    {
        using var sc = new Scenario(stopped: false);
        sc.Session.CustomHandler = (command, _) => command == "exceptionInfo" ? Json(ExceptionInfoJson) : null;
        _ = sc.Provider.GetRequiredService<DebugPanelsRuntime>();
        Assert.Equal(DebugPanelsRuntime.ExceptionStoppedKey, sc.Tools["debug.exception"].AutoShowWhen);
        Assert.Equal("inDebugMode", sc.Tools["debug.callstack"].AutoShowWhen);
        Assert.Null(sc.Tools["debug.modules"].AutoShowWhen);

        sc.Rig.StopAt(0, "exception");
        await PanelRig.Eventually(() => sc.Rig.ContextKeys.Get(DebugPanelsRuntime.ExceptionStoppedKey) is true, what: "key set");
        sc.Rig.Resume();
        await PanelRig.Eventually(() => sc.Rig.ContextKeys.Get(DebugPanelsRuntime.ExceptionStoppedKey) is false, what: "key cleared");
        sc.Rig.StopAt(0, "breakpoint");
        await Task.Delay(100);
        Assert.False(sc.Rig.ContextKeys.Get(DebugPanelsRuntime.ExceptionStoppedKey) is true);
    }

    [AvaloniaFact]
    public async Task A_view_opened_after_the_exception_stop_still_shows_it()
    {
        using var rig = new PanelRig();
        rig.Session.CustomHandler = (command, _) => command == "exceptionInfo" ? Json(ExceptionInfoJson) : null;
        rig.StopAt(0, "exception"); // the window is created only now
        using var vm = new ExceptionHelperViewModel(rig.Services);
        await PanelRig.Eventually(() => vm.HasException, what: "details of an already stopped exception");
    }

    // ------------------------------------------------------------------ modules

    private const string ModulesJson = """
        { "modules": [
          { "id": "1", "name": "Sample.dll", "path": "/work/out/Sample.dll", "version": "1.0.0.0", "isUserCode": true, "symbolStatus": "Symbols loaded." },
          { "id": 2, "name": "System.Private.CoreLib.dll", "path": "/usr/share/dotnet/System.Private.CoreLib.dll", "version": "10.0.12", "isUserCode": false, "symbolStatus": "Skipped loading symbols." }
        ] }
        """;

    [AvaloniaFact]
    public async Task The_modules_window_lists_the_adapters_modules_on_each_stop()
    {
        using var rig = new PanelRig();
        rig.Session.CustomHandler = (command, _) => command == "modules" ? Json(ModulesJson) : null;
        using var vm = new ModulesViewModel(rig.Services);
        rig.StopAt(0);
        await PanelRig.Eventually(() => vm.Rows.Count == 2, what: "modules");
        Assert.Equal(["Sample.dll", "System.Private.CoreLib.dll"], vm.Rows.Select(r => r.Name));
        Assert.Equal("Yes", vm.Rows[0].UserCode);
        Assert.Equal("No", vm.Rows[1].UserCode);
        Assert.Equal("1.0.0.0", vm.Rows[0].Version);
        Assert.False(vm.ShowStatus);
    }

    [AvaloniaFact]
    public async Task An_adapter_without_modules_support_says_so_and_refresh_retries()
    {
        using var rig = new PanelRig();
        rig.Session.CustomHandler = (_, _) => throw new NotSupportedException("no modules");
        using var vm = new ModulesViewModel(rig.Services);
        rig.StopAt(0);
        await PanelRig.Eventually(() => vm.StatusText == "The debug adapter does not support the modules request.");
        rig.Session.CustomHandler = (command, _) => command == "modules" ? Json(ModulesJson) : null;
        await vm.RefreshCommand.ExecuteAsync();
        Assert.Equal(2, vm.Rows.Count);
    }

    [AvaloniaFact]
    public async Task The_modules_window_shows_the_modules_the_session_collected_without_sending_the_modules_request()
    {
        // Regression (dbgp-int-4): the window sent the raw `modules` request on every stop, which netcoredbg (and most adapters) do not
        // implement, so it showed an error while the session already held every module the adapter reported with `module` events.
        var session = new FakeModulesDebugSession();
        session.WithDefaultScript();
        session.ModuleList.Add(new DebugModule("1", "Sample.dll", "/work/out/Sample.dll", "1.0.0.0", true, "Symbols loaded."));
        session.ModuleList.Add(new DebugModule("2", "System.Private.CoreLib.dll", "/usr/share/dotnet/System.Private.CoreLib.dll", "10.0.12", false, "Skipped loading symbols."));
        var requests = new List<string>();
        session.CustomHandler = (command, _) =>
        {
            lock (requests) requests.Add(command);
            throw new InvalidOperationException("Unknown command: " + command);
        };
        using var rig = new PanelRig(session: session);
        using var vm = new ModulesViewModel(rig.Services);
        rig.StopAt(0);
        await PanelRig.Eventually(() => vm.Rows.Count == 2 || vm.StatusText != "Not debugging", what: "modules loaded");
        Assert.Null(vm.StatusText);
        Assert.Equal(["Sample.dll", "System.Private.CoreLib.dll"], vm.Rows.Select(r => r.Name));
        Assert.Equal("Yes", vm.Rows[0].UserCode);
        Assert.Equal("10.0.12", vm.Rows[1].Version);

        session.ModuleList.Add(new DebugModule("3", "", "/work/out/Lib.dll")); // loaded while running: shown at the next stop
        rig.Resume();
        rig.StopAt(0);
        await PanelRig.Eventually(() => vm.Rows.Count == 3, what: "the module reported in between");
        Assert.Equal("(unnamed)", vm.Rows[2].Name);
        Assert.True(session.ModulesCalls >= 2);
        lock (requests) Assert.DoesNotContain("modules", requests);
    }

    [AvaloniaFact]
    public async Task The_modules_window_shows_a_module_loaded_while_the_debuggee_runs_without_a_request_and_lets_the_session_go()
    {
        // Regression: the session had no change event, so a module loaded while running only appeared at the next stop (or on Refresh).
        var session = new FakeModulesDebugSession();
        session.WithDefaultScript();
        session.ModuleList.Add(new DebugModule("1", "Sample.dll", "/work/out/Sample.dll"));
        using var rig = new PanelRig(session: session);
        var vm = new ModulesViewModel(rig.Services);
        rig.StopAt(0);
        await PanelRig.Eventually(() => vm.Rows.Count == 1, what: "modules at the stop");
        rig.Resume();
        var calls = session.ModulesCalls;

        session.LoadModule(new DebugModule("2", "Lib.dll", "/work/out/Lib.dll"));
        await PanelRig.Eventually(() => vm.Rows.Count == 2, what: "the module loaded while running");
        Assert.Equal("Lib.dll", vm.Rows[1].Name);
        Assert.Equal(calls, session.ModulesCalls); // re-read, not requested

        vm.Dispose(); // the window was hidden
        Assert.Equal(0, session.ModulesChangedEmitter.SubscriberCount);
    }

    [AvaloniaFact]
    public async Task A_session_whose_adapter_refused_the_modules_request_is_not_asked_again_at_every_stop()
    {
        // A session that does not expose its modules is asked with the raw request; one refusal is enough (Refresh still retries).
        using var rig = new PanelRig();
        var asked = 0;
        rig.Session.CustomHandler = (command, _) =>
        {
            if (command != "modules") return null;
            Interlocked.Increment(ref asked);
            throw new InvalidOperationException("Unknown command: modules");
        };
        using var vm = new ModulesViewModel(rig.Services);
        rig.StopAt(0);
        await PanelRig.Eventually(() => vm.StatusText?.StartsWith("Cannot read the modules", StringComparison.Ordinal) == true, what: "refused");

        rig.Resume();
        rig.StopAt(1);
        rig.Resume();
        rig.StopAt(0);
        await Task.Delay(300);
        Assert.Equal(1, Volatile.Read(ref asked));
        Assert.StartsWith("Cannot read the modules", vm.StatusText);

        rig.Session.CustomHandler = (command, _) => command == "modules" ? Json(ModulesJson) : null;
        await vm.RefreshCommand.ExecuteAsync();
        Assert.Equal(2, vm.Rows.Count);
        Assert.Null(vm.StatusText);
    }

    [AvaloniaFact]
    public async Task Exception_and_modules_windows_render()
    {
        TestTheme.Apply(VsThemes.Dark);
        using var sc = new Scenario(stopped: false);
        sc.Session.CustomHandler = (command, _) => command switch { "exceptionInfo" => Json(ExceptionInfoJson), "modules" => Json(ModulesJson), _ => null };
        var exception = sc.View("debug.exception");
        var modules = sc.View("debug.modules");
        sc.Rig.StopAt(0, "exception");
        await PanelRig.Eventually(() => ((ExceptionHelperViewModel)exception.DataContext!).HasException && ((ModulesViewModel)modules.DataContext!).Rows.Count == 2);
        await TestTheme.ShotAsync(exception, "debug-exception-dark", 900, 230);
        await TestTheme.ShotAsync(modules, "debug-modules-dark", 900, 120);
        (exception as IDisposable)?.Dispose();
        (modules as IDisposable)?.Dispose();
    }
}
