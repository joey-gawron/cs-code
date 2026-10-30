using System.Text.Json;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using CsCode.App.Notifications;
using CsCode.Core.Debugging;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Tasks;
using CsCode.Panels.Terminal.Tests.Infrastructure;
using CsCode.Terminal.Sessions;
using Xunit;

namespace CsCode.Panels.Terminal.Tests;

/// <summary>
/// Tasks in terminals in the real composition (the Dap task runner, the terminal host, the Error List's diagnostics, the menus and keys):
/// Ctrl+Shift+B runs the default build task in a terminal named after it, its problem matcher fills the Error List, Run Task reuses the
/// idle task terminal, a debug configuration's preLaunchTask runs in a terminal too, and a key closes a finished task terminal.
/// </summary>
public sealed class RealAppTaskTests
{
    private const string TasksJson = """
        {"version":"2.0.0","tasks":[
          {"label":"build","type":"shell","command":"echo compiling...; printf '%s\\n' \"$PWD/main.c:3:7: error: expected ';' before 'return'\"; exit 1",
           "group":{"kind":"build","isDefault":true},"problemMatcher":"$gcc"},
          {"label":"hello","type":"shell","command":"echo \"hello-from-$((40+2))\""}
        ]}
        """;

    private static async Task<(TerminalAppHost App, string Folder)> StartWithTasksAsync()
    {
        var app = await TerminalAppHost.StartAsync();
        var folder = app.Sandbox.Combine("project");
        Directory.CreateDirectory(Path.Combine(folder, ".vscode"));
        File.WriteAllText(Path.Combine(folder, ".vscode", "tasks.json"), TasksJson);
        File.WriteAllText(Path.Combine(folder, "main.c"), "int main(void)\n{\n  int x = 1\n  return x;\n}\n");
        await app.Get<IWorkspaceService>().OpenFolderAsync(new Uri(folder));
        await TerminalAppHost.SettleAsync(10);
        return (app, folder);
    }

    private static TerminalSession? TaskTerminal(TerminalAppHost app, string name) => app.Terminals.Sessions.FirstOrDefault(s => s.Title == name);

    private static string Text(TerminalSession s) => s.InvokeAsync(e =>
    {
        var sb = new System.Text.StringBuilder();
        for (var line = e.ScrollbackTop; line < e.ScreenTop + e.Rows; line++)
        {
            if (e.GetRow(line) is not { } row) continue;
            if (!row.IsWrapped && sb.Length > 0) sb.Append('\n');
            row.AppendText(sb);
        }
        return sb.ToString();
    }).Result;

    [AvaloniaFact]
    public async Task Ctrl_shift_b_runs_the_build_task_in_its_terminal_and_its_problems_reach_the_error_list()
    {
        var (app, folder) = await StartWithTasksAsync();
        await using var _ = app;
        app.Shell.Menus.Refresh();
        var terminalMenu = app.Shell.Menus.Roots.Single(r => r.Header.Replace("_", "", StringComparison.Ordinal) == "Terminal");
        var buildItem = terminalMenu.Children.Single(c => c.CommandId == TaskCommandSet.Build);
        Assert.True(buildItem.IsEnabled && terminalMenu.Children.Single(c => c.CommandId == TaskCommandSet.RunTask).IsEnabled);
        Assert.Equal(TerminalAppHost.KeyLabel("ctrl+shift+b"), buildItem.Gesture);

        app.Window.KeyPress(Key.B, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.B, "B");
        TerminalSession? build = null;
        Assert.True(await TerminalAppHost.WaitFor(() => (build = TaskTerminal(app, "build")) is { HasExited: true }, 15_000), "no 'build' task terminal finished");
        Assert.True(await TerminalAppHost.WaitFor(() => Text(build!).Contains("press any key to close it", StringComparison.Ordinal)), Text(build!));
        var text = Text(build!);
        Assert.Contains(" *  Executing task: echo compiling...;", text);
        Assert.Contains("compiling...", text);
        Assert.Contains("terminated with exit code: 1.", text);
        Assert.True(build!.IsTransient);
        // the Terminal window shows it (reveal: always), without taking the keyboard from where it was (focus: false)
        Assert.True(await TerminalAppHost.WaitFor(() => app.View is { IsShown: true } v && ReferenceEquals(v.Control.Session, build)));

        // $gcc found the error: the Error List's diagnostics have it (at the path the task's bash printed: $PWD from getcwd, without symbolic links)
        var diagnostics = app.Get<IDiagnosticService>();
        Assert.True(await TerminalAppHost.WaitFor(() => diagnostics.GetAll().Any(d => d.Uri.LocalPath == Path.Combine(Support.PhysicalPath(folder), "main.c"))), "no problem reached the diagnostics");
        var problem = diagnostics.GetAll().Single(d => d.Uri.LocalPath == Path.Combine(Support.PhysicalPath(folder), "main.c")).Diagnostic;
        Assert.Equal(("expected ';' before 'return'", 2, 6, DiagnosticSeverity.Error), (problem.Message, problem.Range.Start.Line, problem.Range.Start.Character, problem.Severity));
        app.Capture("terminal-task-build");

        // Run Task "hello": the idle shared task terminal is reused and renamed
        await app.RunAsync(TaskCommandSet.RunTask, "hello");
        Assert.True(await TerminalAppHost.WaitFor(() => build.Title == "hello" && Text(build).Contains("hello-from-42", StringComparison.Ordinal), 15_000), Text(build));
        Assert.Single(app.Terminals.Sessions);
        // the second run ended too (each run says the terminal will be reused)
        Assert.True(await TerminalAppHost.WaitFor(() => build.HasExited
            && System.Text.RegularExpressions.Regex.Matches(Text(build), "press any key to close it").Count == 2), Text(build));

        // a key in the finished task terminal closes it
        app.View!.Control.Focus();
        Assert.True(await TerminalAppHost.WaitFor(() => app.View!.Control.IsKeyboardFocusWithin));
        app.Window.KeyTextInput("q");
        Assert.True(await TerminalAppHost.WaitFor(() => !app.Terminals.Sessions.Contains(build)), "a key did not close the finished task terminal");
    }

    [AvaloniaFact]
    public async Task A_pre_launch_task_runs_in_a_terminal_before_the_debugger_starts()
    {
        var (app, folder) = await StartWithTasksAsync();
        await using var _ = app;
        var props = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"type":"cscode-test-no-adapter","request":"launch","name":"try","preLaunchTask":"hello"}""")!;
        var config = new DebugConfiguration("cscode-test-no-adapter", "launch", "try", props, new Uri(folder), PreLaunchTask: "hello");
        var started = await app.Get<IDebugService>().StartAsync(config);
        Assert.Null(started); // no such adapter: but the preLaunchTask ran first, in its terminal
        var hello = TaskTerminal(app, "hello");
        Assert.NotNull(hello);
        Assert.True(await TerminalAppHost.WaitFor(() => Text(hello!).Contains("hello-from-42", StringComparison.Ordinal)), Text(hello!));
        Assert.Equal(0, hello!.ExitCode);
    }

    [AvaloniaFact]
    public async Task A_background_task_that_cannot_be_tracked_runs_on_quietly_from_run_task_and_only_the_debugger_reports_it()
    {
        // T43: isBackground tasks without background patterns ("problemMatcher": [] or none: a dev server, `dotnet watch`) are common
        var app = await TerminalAppHost.StartAsync();
        await using var _ = app;
        var folder = app.Sandbox.Combine("serve-project");
        Directory.CreateDirectory(Path.Combine(folder, ".vscode"));
        File.WriteAllText(Path.Combine(folder, ".vscode", "tasks.json"), """
            {"version":"2.0.0","tasks":[
              {"label":"serve","type":"shell","command":"echo serving-$((40+2)); exec sleep 1000","isBackground":true,"problemMatcher":[]},
              {"label":"serve-pre","type":"shell","command":"echo pre-serving-$((40+2)); exec sleep 1000","isBackground":true}
            ]}
            """);
        await app.Get<IWorkspaceService>().OpenFolderAsync(new Uri(folder));
        await TerminalAppHost.SettleAsync(10);
        var notifications = app.Get<NotificationCenter>();
        var tasks = app.Get<ITaskService>();

        await app.RunAsync(TaskCommandSet.RunTask, "serve");
        var props = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"type":"cscode-test-no-adapter","request":"launch","name":"try","preLaunchTask":"serve-pre"}""")!;
        var debug = app.Get<IDebugService>().StartAsync(new DebugConfiguration("cscode-test-no-adapter", "launch", "try", props, new Uri(folder), PreLaunchTask: "serve-pre")).AsTask();
        TerminalSession? serve = null;
        Assert.True(await TerminalAppHost.WaitFor(() => (serve = TaskTerminal(app, "serve")) is not null && Text(serve).Contains("serving-42", StringComparison.Ordinal), 15_000),
            "the 'serve' task did not start in its terminal");

        // the debugger waits for readiness: after five seconds VS Code's "cannot be tracked", with Debug Anyway / Abort
        NotificationItem? asked = null;
        Assert.True(await TerminalAppHost.WaitFor(() => (asked = notifications.History.FirstOrDefault(n => n.Message.Contains("cannot be tracked", StringComparison.Ordinal))) is not null, 20_000),
            "the debugger did not report the preLaunchTask that cannot be tracked");
        Assert.Contains("'serve-pre'", asked!.Message);
        Assert.Equal(["Debug Anyway", "Abort"], asked.Actions.Select(a => a.Title));
        asked.InvokeCommand.Execute(asked.Actions.Single(a => a.Title == "Abort"));
        Assert.True(await TerminalAppHost.WaitFor(() => debug.IsCompleted, 10_000));
        Assert.Null(await debug);

        // Run Task's own run got the same result (a second run joins it: it returns once the first one has), and showed nothing
        var joined = tasks.RunAsync("serve").AsTask(); // (the folder Run Task used: the primary one)
        Assert.True(await TerminalAppHost.WaitFor(() => joined.IsCompleted, 20_000), "the running 'serve' never became ready or untracked");
        await TerminalAppHost.SettleAsync(20);
        Assert.DoesNotContain(notifications.History, n => n.Message.Contains("'serve'", StringComparison.Ordinal));
        Assert.False(serve!.HasExited); // it runs on
        Assert.True(tasks.IsActive("serve"));
    }
}
