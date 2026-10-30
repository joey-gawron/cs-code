using CsCode.Panels.Terminal.Input;
using CsCode.Terminal.Emulator;
using Xunit;

namespace CsCode.Panels.Terminal.Tests;

/// <summary>Selection geometry and text (soft wraps joined, block mode), word and line expansion, and link detection.</summary>
public sealed class SelectionAndLinkTests
{
    private static VtEmulator Term(string text, int cols = 20, int rows = 5)
    {
        var t = new VtEmulator(cols, rows);
        t.Feed(text);
        return t;
    }

    [Fact]
    public void Character_selection_text_joins_soft_wraps_and_breaks_hard_lines()
    {
        var t = Term("hello world, this wraps around\r\nnext line");
        var sel = new TerminalSelection(new GridPoint(0, 6), new GridPoint(2, 3), SelectionMode.Character);
        Assert.Equal("world, this wraps around\nnext", sel.GetText(t.GetRow, t.Columns));
        var reversed = sel with { Anchor = sel.Active, Active = sel.Anchor };
        Assert.Equal(sel.GetText(t.GetRow, t.Columns), reversed.GetText(t.GetRow, t.Columns));
    }

    [Fact]
    public void Block_selection_takes_a_rectangle()
    {
        var t = Term("abcdef\r\nghijkl\r\nmnopqr");
        var sel = new TerminalSelection(new GridPoint(0, 1), new GridPoint(2, 3), SelectionMode.Block);
        Assert.Equal("bcd\nhij\nnop", sel.GetText(t.GetRow, t.Columns));
        Assert.True(sel.Contains(1, 2));
        Assert.False(sel.Contains(1, 4));
    }

    [Fact]
    public void Words_use_the_separators_and_lines_follow_soft_wraps()
    {
        var t = Term("ls -la /usr/lib (x) \"quoted\"");
        var row = t.GetScreenRow(0);
        Assert.Equal((7, 14), TerminalSelection.WordAt(row, 9, t.Columns, " ()[]{}',\"`─‘’“”|"));
        var wrapped = Term("abcdefghijklmnopqrstuvwxyz0123\r\nnext");
        Assert.Equal((0L, 1L), TerminalSelection.LogicalLine(wrapped.GetRow, 1, 0, 4));
    }

    [Fact]
    public void Urls_paths_with_positions_and_osc8_links_are_found()
    {
        var t = Term("see https://example.com/a(b). src/App.cs:12:5 /tmp/x.log Program.cs(7,3) \e]8;;file:///etc/hosts\e\\hosts\e]8;;\e\\", 120, 2);
        var row = t.GetScreenRow(0);
        var url = LinkDetector.At(row, 6, 120)!;
        Assert.Equal(LinkKind.Url, url.Kind);
        Assert.Equal("https://example.com/a(b)", url.Target);
        var path = LinkDetector.At(row, 34, 120)!;
        Assert.Equal((LinkKind.File, "src/App.cs", 12, 5), (path.Kind, path.Target, path.Line, path.Column));
        var abs = LinkDetector.At(row, 50, 120)!;
        Assert.Equal("/tmp/x.log", abs.Target);
        var msbuild = LinkDetector.All(row, 120).Single(l => l.Target == "Program.cs");
        Assert.Equal((7, 3), (msbuild.Line, msbuild.Column));
        var osc8 = LinkDetector.At(row, row.GetText().IndexOf("hosts", StringComparison.Ordinal), 120)!;
        Assert.Equal((LinkKind.Hyperlink, "file:///etc/hosts"), (osc8.Kind, osc8.Target));
    }

    [Fact]
    public void Plain_words_are_not_links() => Assert.Empty(LinkDetector.All(Term("hello world 42 times").GetScreenRow(0), 20));
}
