using CsCode.Core.Debugging;
using Xunit;

namespace CsCode.Core.Tests;

public class DebugModelsTests
{
    [Fact]
    public void Output_events_are_numbered_in_creation_order_and_with_copies_keep_the_number()
    {
        var first = new DebugOutputEventArgs(null!, "stdout", "a\n");
        var second = new DebugOutputEventArgs(null!, "stdout", "a\n");
        Assert.True(second.Sequence > first.Sequence);
        Assert.NotEqual(first, second); // same text, different events

        var cut = first with { Output = "tail\n" };
        var relabelled = first with { Category = "console" };
        Assert.Equal(first.Sequence, cut.Sequence);
        Assert.Equal(first.Sequence, relabelled.Sequence);
        Assert.True(new DebugOutputEventArgs(null!, "stdout", "b\n").Sequence > second.Sequence);
    }
}
