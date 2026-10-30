using CsCode.App.Input;
using CsCode.App.Macros;
using CsCode.App.Services;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Platform;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.App.Tests.RealApp.Macros;

/// <summary>The keyboard pipeline's tap (macro recording and playback): what it sees, and what it may decide before the key bindings.</summary>
public class KeyboardTapTests
{
    private sealed class Tap : IKeyboardTap
    {
        public Func<KeyStroke, KeyDispatch?> Before { get; set; } = _ => null;
        public List<(string Key, KeyDispatch Dispatch)> After { get; } = new();
        public KeyDispatch? BeforeResolve(KeyStroke stroke) => Before(stroke);
        public void AfterResolve(KeyStroke stroke, KeyDispatch dispatch) => After.Add((stroke.ToString(), dispatch));
    }

    private static (KeyboardPipeline Pipeline, FakeKeybindingService Bindings, List<string> Ran) Rig()
    {
        var keys = new SimpleContextKeys();
        var bindings = new FakeKeybindingService(keys);
        var commands = new TestCommandService();
        var ran = new List<string>();
        foreach (var id in new[] { "a.cmd", "chord.cmd" }) commands.Register(id, _ => { ran.Add(id); return ValueTask.FromResult<object?>(null); });
        bindings.Add("a.cmd", "ctrl+b");
        bindings.Add("chord.cmd", "ctrl+k ctrl+c");
        var ui = new ManualUiDispatcher();
        return (new KeyboardPipeline(bindings, commands, ui, new StatusBarService(ui), context: keys), bindings, ran);
    }

    private static KeyStroke S(string s) => KeyStrokeParser.ParseStroke(s);

    [Fact]
    public void The_tap_sees_every_key_with_the_pipelines_decision()
    {
        var (pipeline, _, ran) = Rig();
        var tap = new Tap();
        pipeline.Tap = tap;

        pipeline.Handle(S("left"));
        pipeline.Handle(S("ctrl+b"));
        pipeline.Handle(S("ctrl+k"));
        pipeline.Handle(S("ctrl+c"));
        pipeline.Handle(S("a"));

        Assert.Equal([("left", KeyDispatch.PassThrough), ("ctrl+b", KeyDispatch.Handled), ("ctrl+k", KeyDispatch.Handled), ("ctrl+c", KeyDispatch.Handled), ("a", KeyDispatch.PassThrough)], tap.After);
        Assert.Equal(["a.cmd", "chord.cmd"], ran);
    }

    [Fact]
    public void Before_resolution_the_tap_may_consume_a_key_or_leave_it_to_the_focused_control_unresolved()
    {
        var (pipeline, _, ran) = Rig();
        var tap = new Tap();
        pipeline.Tap = tap;

        tap.Before = k => k.Key == "escape" ? KeyDispatch.Handled : KeyDispatch.PassThrough;
        Assert.Equal(KeyDispatch.Handled, pipeline.Handle(S("escape")));
        Assert.Equal(KeyDispatch.PassThrough, pipeline.Handle(S("ctrl+b"))); // a replayed key: never a binding
        Assert.Equal(KeyDispatch.PassThrough, pipeline.Handle(S("ctrl+k")));
        Assert.False(pipeline.IsChordPending);
        Assert.Empty(ran);
        Assert.Empty(tap.After); // decided before resolution: nothing resolved

        pipeline.Tap = null;
        Assert.Equal(KeyDispatch.Handled, pipeline.Handle(S("ctrl+b")));
    }

    [Fact]
    public void Key_strokes_map_back_to_avalonia_keys_for_replay()
    {
        foreach (var name in new[] { "a", "z", "0", "f5", "f12", "enter", "escape", "backspace", "delete", "tab", "space", "home", "end", "pageup", "left", ",", ".", "[", "\\", "numpad3", "numpad_add" })
        {
            Assert.True(MacroKeys.TryGetKey(S("ctrl+shift+" + name), out var key, out var mods), name);
            Assert.True(AvaloniaKeyMapper.TryMap(key, mods, out var back), name);
            Assert.Equal(S("ctrl+shift+" + name), back);
        }
        Assert.False(MacroKeys.TryGetKey(new KeyStroke("nosuchkey"), out _, out _));
    }
}
