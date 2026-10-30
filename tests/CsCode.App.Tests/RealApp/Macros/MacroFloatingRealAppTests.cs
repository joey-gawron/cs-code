using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Platform;
using CsCode.Macros;
using Dock.Avalonia.Controls;
using Xunit;

namespace CsCode.App.Tests.RealApp.Macros;

/// <summary>
/// Macros in an editor that floats in a window of its own (Window > Float). Key and text events are routed through the window that holds the focused
/// editor, so the recorder's end-of-input handler and the player's typing guard must be on every shell window, not only the main one: without them
/// the recorder, told an input began but never that it ended, dropped every command after the first typed character, and typing during a playback
/// mixed with the macro's own.
/// </summary>
public class MacroFloatingRealAppTests
{
    private static KeyStep K(string key) => new(KeyStrokeParser.ParseStroke(key));

    private static async Task<MacroApp> StartFloatingAsync(string text, string? settings = null)
    {
        var app = await MacroApp.StartAsync(text, settings: settings);
        app.Well.Float(app.Tab);
        if (!await RealAppHost.WaitFor(() => app.Tab.LiveView is { } v && TopLevel.GetTopLevel(v) is HostWindow { IsVisible: true }))
            throw new TimeoutException("the editor did not float");
        app.Input = TopLevel.GetTopLevel(app.View)!;
        await app.FocusAsync();
        return app;
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Commands_after_typing_in_a_floating_editor_are_recorded()
    {
        await using var app = await StartFloatingAsync("start");
        app.RecordKey();
        Assert.True(app.Service.IsRecording);
        app.Type("foo");
        app.Press(Key.S, MacroApp.Ctrl);    // Save: a command, after typing
        await app.SettleAsync();
        Assert.False(app.Tab.IsDirty);
        app.Press(Key.Home);
        app.Type("bar");
        app.RecordKey();
        await app.SettleAsync();

        Assert.Equal([new TextStep("foo"), new CommandStep(KnownCommands.Save), K("home"), new TextStep("bar")], app.Service.Last!.Steps);
        Assert.Equal("barfoostart", app.Text);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Typing_in_a_floating_editor_while_a_macro_plays_there_is_swallowed_and_Esc_stops_it()
    {
        await using var app = await StartFloatingAsync("start", $"\"{MacroSettings.RecordPauses}\": true");
        app.RecordKey();
        app.Type("x");
        await Task.Delay(400);              // recorded as a pause (macros.recordPauses)
        app.Press(Key.Back);
        app.RecordKey();
        Assert.Equal(3, app.Service.Last!.Steps.Count);

        var playing = app.Host.Get<ICommandService>().ExecuteAsync(MacroCommands.PlayLast, 1_000).AsTask();
        Assert.True(await RealAppHost.WaitFor(() => app.Service.State == MacroState.Playing && app.Text == "xstart"));
        app.Type("Q");                      // swallowed: it would interleave with the macro's typing
        app.Press(Key.Escape);              // stops it
        Assert.True(await RealAppHost.WaitFor(() => playing.IsCompleted), "Esc did not stop the playback");
        await playing;
        Assert.Equal(MacroState.Idle, app.Service.State);
        Assert.Equal("xstart", app.Text);
    }
}
