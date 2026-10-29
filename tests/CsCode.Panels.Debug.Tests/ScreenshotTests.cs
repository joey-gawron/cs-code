using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using CsCode.Core.Debugging;
using CsCode.Panels.Debug.Console;
using CsCode.Panels.Debug.Tests.Fakes;
using CsCode.Panels.Debug.Variables;
using CsCode.Ui.Theming;
using Xunit;

namespace CsCode.Panels.Debug.Tests;

/// <summary>
/// Renders every debug window with realistic data through the real DI registration and the real VS2022 themes, and writes PNGs to
/// <c>.scratch/screenshots/debug-*.png</c> (git-ignored) for visual review. The assertions only guard that the views are populated.
/// </summary>
public class ScreenshotTests
{
    internal static VariablesTreeViewModel TreeOf(Control view) => view.DataContext switch
    {
        FrameVariablesViewModel f => f.Tree,
        WatchViewModel w => w.Tree,
        _ => throw new InvalidOperationException("not a variables view"),
    };

    private static async Task SecondStopAsync(Scenario sc)
    {
        // Step: x changes from 41 to 42, so the Locals show a red (changed) value.
        sc.Rig.Resume();
        await Task.Delay(50);
        sc.Session.Variables[1200][1] = new DebugVariable("x", "42", "int", EvaluateName: "x");
        sc.Rig.StopAt(2);
    }

    [AvaloniaTheory]
    [InlineData("dark")]
    [InlineData("light")]
    [InlineData("blue")]
    public async Task Locals_window(string theme)
    {
        TestTheme.Apply(ThemeOf(theme));
        using var sc = new Scenario(stopped: false); // the window exists before the first stop, like when a session starts
        var view = sc.View("debug.locals");
        var tree = TreeOf(view);
        sc.Rig.StopAt(2);
        await PanelRig.Eventually(() => tree.Rows.Count >= 8, what: "locals");
        await SecondStopAsync(sc);
        await PanelRig.Eventually(() => tree.Rows.Any(r => r.Name == "x" && r.IsChanged), what: "changed x");
        await tree.ToggleAsync(tree.Rows.Single(r => r.Name == "item"));
        await tree.ToggleAsync(tree.Rows.Single(r => r.Name == "numbers"));
        tree.SelectedRow = tree.Rows.Single(r => r.Name == "s");
        var path = await TestTheme.ShotAsync(view, $"debug-locals-{theme}", 900, 330);
        Assert.True(File.Exists(path));
        (view as IDisposable)?.Dispose();
    }

    [AvaloniaFact]
    public async Task Autos_window()
    {
        TestTheme.Apply(VsThemes.Dark);
        using var sc = new Scenario(frame: 2);
        var view = sc.View("debug.autos");
        await PanelRig.Eventually(() => TreeOf(view).Rows.Count >= 3, what: "autos");
        await TestTheme.ShotAsync(view, "debug-autos-dark", 900, 240);
        (view as IDisposable)?.Dispose();
    }

    [AvaloniaTheory]
    [InlineData("dark")]
    [InlineData("light")]
    public async Task Watch_window(string theme)
    {
        TestTheme.Apply(ThemeOf(theme));
        using var sc = new Scenario(frame: 2);
        foreach (var e in new[] { "x + 1", "s.Length", "item", "numbers.Count * 2", "nope" }) sc.Rig.Debug.AddWatch(e);
        var view = sc.View("debug.watch");
        var tree = TreeOf(view);
        await PanelRig.Eventually(() => tree.Rows.Count(r => r.Kind == VariableRowKind.Watch && r.DisplayValue.Length > 0) == 5, what: "watches evaluated");
        await tree.ToggleAsync(tree.Rows.Single(r => r.Name == "item"));
        tree.SelectedRow = tree.Rows.Single(r => r.Name == "s.Length");
        await TestTheme.ShotAsync(view, $"debug-watch-{theme}", 900, 300);
        (view as IDisposable)?.Dispose();
    }

    [AvaloniaTheory]
    [InlineData("dark")]
    [InlineData("light")]
    public async Task Call_stack_window(string theme)
    {
        TestTheme.Apply(ThemeOf(theme));
        using var sc = new Scenario();
        sc.Session.Threads.RemoveRange(1, 2); // one thread: a flat list of frames
        var view = sc.View("debug.callstack");
        var model = (CallStack.CallStackViewModel)view.DataContext!;
        await PanelRig.Eventually(() => model.Rows.Count == 5 && model.Rows[0].IsCurrent, what: "frames");
        await model.ActivateAsync(model.Rows[1], takeFocus: false); // a caller frame picked: outlined arrow
        model.SelectedRow = model.Rows[1];
        await TestTheme.ShotAsync(view, $"debug-callstack-{theme}", 900, 190);
        (view as IDisposable)?.Dispose();
    }

    [AvaloniaFact]
    public async Task Call_stack_window_with_threads_and_folded_external_code()
    {
        TestTheme.Apply(VsThemes.Dark);
        using var sc = new Scenario();
        var view = sc.View("debug.callstack");
        var model = (CallStack.CallStackViewModel)view.DataContext!;
        await PanelRig.Eventually(() => model.Rows.Count > 5, what: "frames");
        model.ShowExternalCode = false;
        await model.ActivateAsync(model.Rows.Last(r => r.Kind == CallStack.CallStackRowKind.Thread), takeFocus: false);
        await TestTheme.ShotAsync(view, "debug-callstack-threads-dark", 900, 230);
        (view as IDisposable)?.Dispose();
    }

    [AvaloniaTheory]
    [InlineData("dark")]
    [InlineData("light")]
    public async Task Breakpoints_window(string theme)
    {
        TestTheme.Apply(ThemeOf(theme));
        using var sc = new Scenario();
        var view = sc.View("debug.breakpoints");
        var model = (Breakpoints.BreakpointsViewModel)view.DataContext!;
        await PanelRig.Eventually(() => model.Rows.Count >= 11, what: "breakpoint rows");
        model.SelectedRow = model.Rows[2];
        await TestTheme.ShotAsync(view, $"debug-breakpoints-{theme}", 900, 330);
        (view as IDisposable)?.Dispose();
    }

    [AvaloniaFact]
    public async Task Threads_window()
    {
        TestTheme.Apply(VsThemes.Dark);
        using var sc = new Scenario();
        var view = sc.View("debug.threads");
        var model = (Threads.ThreadsViewModel)view.DataContext!;
        await PanelRig.Eventually(() => model.Rows.Count == 3 && model.Rows.All(r => r.Location.Length > 0), what: "threads");
        await TestTheme.ShotAsync(view, "debug-threads-dark", 900, 130);
        (view as IDisposable)?.Dispose();
    }

    [AvaloniaTheory]
    [InlineData("dark")]
    [InlineData("light")]
    public async Task Debug_console_window(string theme)
    {
        TestTheme.Apply(ThemeOf(theme));
        using var sc = new Scenario();
        sc.Rig.Debug.EmitOutput(new DebugOutputEventArgs(sc.Session, "console", "Loaded '/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/System.Runtime.dll'. Skipped loading symbols. Module is optimized.\n"));
        sc.Rig.Debug.EmitOutput(new DebugOutputEventArgs(sc.Session, "stdout", "loop 0\nloop 1\nloop 2\nloop 3\n"));
        sc.Rig.Debug.EmitOutput(new DebugOutputEventArgs(sc.Session, "stderr", "warning: the cache is cold\n"));
        sc.Rig.Debug.EmitOutput(new DebugOutputEventArgs(sc.Session, "important", "The program 'Sample' is paused at a breakpoint.\n"));
        var view = sc.View("debug.console");
        var model = (DebugConsoleViewModel)view.DataContext!;
        await PanelRig.Eventually(() => model.Lines.Count >= 7, what: "history replay");
        model.Input = "item.Count * 14";
        await model.SubmitAsync();
        model.Input = "nope";
        await model.SubmitAsync();
        sc.Rig.StopAt(0);
        await PanelRig.Eventually(() => model.Lines.Count >= 11, what: "repl lines");
        model.Input = "item.";
        await PanelRig.Eventually(() => model.CompletionsOpen, what: "completions");
        await TestTheme.ShotAsync(view, $"debug-console-{theme}", 900, 300);
        (view as IDisposable)?.Dispose();
    }

    private static CsCode.Core.Theming.ColorTheme ThemeOf(string name) => name switch { "light" => VsThemes.Light, "blue" => VsThemes.Blue, _ => VsThemes.Dark };
}
