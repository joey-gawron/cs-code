using System.Text.Json;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using CsCode.Core.Debugging;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
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
        Assert.Equal("Ctrl+Shift+B", buildItem.Gesture);

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

        // $gcc found the error: the Error List's diagnostics have it
        var diagnostics = app.Get<IDiagnosticService>();
        Assert.True(await TerminalAppHost.WaitFor(() => diagnostics.GetAll().Any(d => d.Uri.LocalPath == Path.Combine(folder, "main.c"))), "no problem reached the diagnostics");
        var problem = diagnostics.GetAll().Single(d => d.Uri.LocalPath == Path.Combine(folder, "main.c")).Diagnostic;
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
}
