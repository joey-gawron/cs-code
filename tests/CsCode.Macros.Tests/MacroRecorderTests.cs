using CsCode.Core.Platform;
using CsCode.Macros.Tests.Support;
using Xunit;

namespace CsCode.Macros.Tests;

public class MacroRecorderTests
{
    [Fact]
    public void Typed_text_merges_until_another_step_comes()
    {
        var r = new MacroRecorder();
        r.AddText("a");
        r.AddText("bc");
        r.AddKey(new KeyStroke("enter"));
        r.AddText("d");
        r.AddCommand("x", []);
        r.AddText("e");

        Assert.Equal([new TextStep("abc"), new KeyStep(new KeyStroke("enter")), new TextStep("d"), new CommandStep("x"), new TextStep("e")], r.ToSteps());
        Assert.Equal(5, r.Count);
    }

    [Fact]
    public void The_step_limit_refuses_further_steps()
    {
        var r = new MacroRecorder(maxSteps: 3);
        Assert.True(r.AddKey(new KeyStroke("a", Ctrl: true)));
        Assert.True(r.AddText("x"));
        Assert.True(r.AddText("y")); // merges: still three
        Assert.True(r.AddKey(new KeyStroke("left")));
        Assert.False(r.AddText("z"));
        Assert.True(r.IsFull);
        Assert.False(r.AddCommand("c", []));
        Assert.Equal(3, r.ToSteps().Count);
    }

    [Fact]
    public void Pauses_are_recorded_only_when_asked_and_capped()
    {
        var clock = new StepClock();
        var r = new MacroRecorder(pauseThreshold: TimeSpan.FromMilliseconds(250), time: clock);
        clock.Advance(TimeSpan.FromSeconds(5)); // before the first step: nothing to wait for
        r.AddText("a");
        clock.Advance(TimeSpan.FromMilliseconds(100));
        r.AddText("b"); // short gap: same text run
        clock.Advance(TimeSpan.FromMilliseconds(400));
        r.AddKey(new KeyStroke("enter"));
        clock.Advance(TimeSpan.FromMinutes(5));
        r.AddText("c");

        Assert.Equal([new TextStep("ab"), new PauseStep(400), new KeyStep(new KeyStroke("enter")), new PauseStep(10_000), new TextStep("c")], r.ToSteps());

        var typing = new MacroRecorder(pauseThreshold: TimeSpan.FromMilliseconds(250), time: clock);
        typing.AddText("a");
        clock.Advance(TimeSpan.FromSeconds(1));
        typing.AddText("b"); // a pause in the middle of typing splits the text
        Assert.Equal([new TextStep("a"), new PauseStep(1000), new TextStep("b")], typing.ToSteps());

        var plain = new MacroRecorder(time: clock);
        plain.AddText("a");
        clock.Advance(TimeSpan.FromSeconds(3));
        plain.AddText("b");
        Assert.Equal([new TextStep("ab")], plain.ToSteps());
    }
}
