using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using CsCode.Core.Debugging;
using CsCode.Panels.Debug.Breakpoints;
using CsCode.Panels.Debug.CallStack;
using CsCode.Panels.Debug.Console;
using CsCode.Panels.Debug.Tests.Fakes;
using CsCode.Panels.Debug.Threads;
using CsCode.Panels.Debug.Variables;
using CsCode.Ui;
using CsCode.Ui.Theming;
using Xunit;

namespace CsCode.Panels.Debug.Tests;

/// <summary>
/// Memory-stability contract of the panels (ARCHITECTURE §8): the shell drops a tool window's view when the window is hidden, so
/// show/hide must leave nothing behind. Every panel is shown and hidden 100 times across 20 debug sessions; afterwards no view, view-model,
/// session or tracker may be reachable and no subscription may remain on the debug service, its sessions or the breakpoint manager.
/// </summary>
public class LeakTests
{
    /// <summary>Does what <c>ToolTab.DisposeView</c> does when a tool window is hidden.</summary>
    private static void Hide(Control view)
    {
        (view as IDisposable)?.Dispose();
        (view.DataContext as IDisposable)?.Dispose();
        view.DataContext = null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<List<WeakReference>> RunSessionsAsync(Scenario sc, int sessions, int cyclesPerSession, bool showInWindow)
    {
        var weak = new List<WeakReference>();
        for (var s = 0; s < sessions; s++)
        {
            var session = new FakeDebugSession("leak" + s, "Leak " + s);
            Scenario.ConfigureSession(session);
            weak.Add(new WeakReference(session));
            sc.Rig.Debug.AddSession(session);
            sc.Rig.Debug.StopAt(session, "breakpoint", 2);
            for (var cycle = 0; cycle < cyclesPerSession; cycle++)
            {
                foreach (var tool in sc.Tools.Values)
                {
                    var view = tool.CreateView(sc.Provider);
                    weak.Add(new WeakReference(view));
                    if (view.DataContext is not null) weak.Add(new WeakReference(view.DataContext));
                    Window? window = null;
                    if (showInWindow)
                    {
                        window = new Window { Width = 600, Height = 200, Content = view };
                        window.Show();
                        weak.Add(new WeakReference(window));
                    }
                    await Task.Delay(showInWindow ? 20 : 3); // let it load (and start requests) before it is hidden again
                    if (window is not null)
                    {
                        window.Content = null;
                        window.Close();
                    }
                    Hide(view);
                }
            }
            sc.Rig.Debug.RemoveSession(session);
            session.Terminate();
        }
        return weak;
    }

    private static async Task AssertNothingRetained(List<WeakReference> weak)
    {
        for (var i = 0; i < 12 && weak.Any(w => w.IsAlive); i++)
        {
            await Task.Delay(40); // pending dispatcher posts and timers release their references
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        var alive = weak.Where(w => w.IsAlive).Select(w => w.Target?.GetType().Name ?? "?").GroupBy(n => n).Select(g => $"{g.Key} x{g.Count()}").ToArray();
        Assert.True(alive.Length == 0, "still reachable after 12 full GCs: " + string.Join(", ", alive));
    }

    [AvaloniaFact]
    public async Task Showing_and_hiding_every_panel_100_times_across_20_sessions_retains_nothing()
    {
        TestTheme.Apply(VsThemes.Dark);
        using var sc = new Scenario(stopped: false);
        sc.Session.Terminate();
        sc.Rig.Debug.RemoveSession(sc.Session);
        Assert.Equal(9, sc.Tools.Count);

        var weak = await RunSessionsAsync(sc, sessions: 20, cyclesPerSession: 5, showInWindow: false);
        Assert.True(weak.Count >= 20 + 20 * 5 * 9 * 2 - 100); // sessions + 900 views + their view-models were tracked

        await AssertNothingRetained(weak);
        await Task.Delay(200);

        // the long-lived services hold no subscription of a hidden panel
        Assert.Equal(0, sc.Rig.Debug.FakeBreakpoints.SubscriberCount);
        Assert.Equal(0, sc.Rig.Debug.OutputEmitter.SubscriberCount);
        Assert.Equal(0, sc.Rig.Debug.OutputClearedEmitter.SubscriberCount);
        Assert.Equal(0, sc.Rig.Debug.StopLocationChangedEmitter.SubscriberCount - 0);
        Assert.Equal(0, sc.Rig.Debug.ActiveSessionChangedEmitter.SubscriberCount);
        Assert.Equal(0, sc.Rig.Debug.StateChangedEmitter.SubscriberCount - 0);
        Assert.Equal(0, sc.Rig.Debug.SessionStartedEmitter.SubscriberCount);
        Assert.Equal(0, sc.Rig.Debug.SessionTerminatedEmitter.SubscriberCount);
        Assert.Empty(sc.Rig.Debug.Sessions);
    }

    [AvaloniaFact]
    public async Task Views_shown_in_a_window_and_closed_retain_nothing_either()
    {
        TestTheme.Apply(VsThemes.Dark);
        using var sc = new Scenario(stopped: false);
        sc.Session.Terminate();
        sc.Rig.Debug.RemoveSession(sc.Session);
        var weak = await RunSessionsAsync(sc, sessions: 3, cyclesPerSession: 2, showInWindow: true);
        await AssertNothingRetained(weak);
    }

    [AvaloniaFact]
    public async Task A_session_that_is_still_running_keeps_no_subscription_of_a_hidden_panel()
    {
        TestTheme.Apply(VsThemes.Dark);
        using var sc = new Scenario(stopped: true);
        var before = sc.Session.SubscriberCount;
        for (var i = 0; i < 25; i++)
            foreach (var tool in sc.Tools.Values)
            {
                var view = tool.CreateView(sc.Provider);
                await Task.Delay(2);
                Hide(view);
            }
        await Task.Delay(100);
        Assert.Equal(before, sc.Session.SubscriberCount); // the live session got back to exactly what it had before any panel existed
    }

    [AvaloniaFact]
    public void Disposing_a_view_model_twice_is_harmless_and_late_events_are_ignored()
    {
        using var sc = new Scenario(stopped: true);
        var locals = new LocalsViewModel(sc.Rig.Services);
        var stack = new CallStackViewModel(sc.Rig.Services);
        var threads = new ThreadsViewModel(sc.Rig.Services);
        var breakpoints = new BreakpointsViewModel(sc.Rig.Services);
        var console = new DebugConsoleViewModel(sc.Rig.Services);
        var watch = new WatchViewModel(sc.Rig.Services);
        foreach (var d in new IDisposable[] { locals, stack, threads, breakpoints, console, watch })
        {
            d.Dispose();
            d.Dispose();
        }
        sc.Rig.Resume();
        sc.Rig.StopAt(1);
        sc.Rig.Debug.FakeBreakpoints.Add(new Uri("file:///work/a.cs"), 3);
        sc.Rig.Debug.EmitOutput(new DebugOutputEventArgs(sc.Session, "stdout", "x\n"));
        Assert.Empty(locals.Tree.Rows);
        Assert.Empty(stack.Rows);
        Assert.Empty(breakpoints.Rows);
        Assert.Empty(console.Lines);
    }
}
