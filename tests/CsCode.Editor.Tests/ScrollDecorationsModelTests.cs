using CsCode.Core.Theming;
using CsCode.Editor.Documents;
using CsCode.Editor.TextMate;
using CsCode.Editor.View.Decorations;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>The pure parts of the minimap and the overview ruler: rows under folding, the proportional window and slider, pixel-row bucketing, rasterising.</summary>
public class ScrollDecorationsModelTests
{
    // ------------------------------------------------------------------------------------------------ rows

    [Fact]
    public void Without_folds_every_line_is_a_row()
    {
        var map = MinimapRowMap.Create(100, []);
        Assert.Equal(100, map.RowCount);
        Assert.Equal(42, map.RowOfLine(42));
        Assert.Equal(42, map.LineOfRow(42));
        Assert.Equal(99, map.LineOfRow(500)); // clamped
    }

    [Fact]
    public void Folded_lines_take_no_rows_and_map_to_their_header()
    {
        // lines 11..20 hidden under the header on line 10; 51..52 under 50; a nested fold inside the first and an overlapping one merge into it
        var map = MinimapRowMap.Create(100, [(11, 20), (13, 15), (51, 52), (18, 22)]);
        Assert.Equal(2, map.HiddenRangeCount);
        Assert.Equal(100 - 12 - 2, map.RowCount);
        Assert.Equal(10, map.RowOfLine(10));
        Assert.Equal(10, map.RowOfLine(15)); // hidden: the header's row
        Assert.Equal(11, map.RowOfLine(23)); // the first line after the fold is the next row
        Assert.Equal(23, map.LineOfRow(11));
        Assert.Equal(10, map.LineOfRow(10));
        Assert.Equal(53, map.LineOfRow(map.RowOfLine(53)));
        for (var row = 0; row < map.RowCount; row++) Assert.Equal(row, map.RowOfLine(map.LineOfRow(row))); // a round trip for every row
    }

    [Fact]
    public void Row_maps_compare_by_their_ranges()
    {
        Assert.True(MinimapRowMap.Create(10, [(2, 3)]).SameAs(MinimapRowMap.Create(10, [(2, 3)])));
        Assert.False(MinimapRowMap.Create(10, [(2, 3)]).SameAs(MinimapRowMap.Create(10, [(2, 4)])));
        Assert.False(MinimapRowMap.Create(10, []).SameAs(MinimapRowMap.Create(11, [])));
    }

    // ------------------------------------------------------------------------------------------------ window and slider

    [Fact]
    public void A_short_document_is_shown_whole_and_the_slider_moves_over_it()
    {
        var f = MinimapGeometry.Compute(rowCount: 100, topRow: 20, viewportRows: 30, rowHeight: 2, height: 600);
        Assert.Equal(0, f.WindowTopRow);
        Assert.Equal(40, f.SliderTop);
        Assert.Equal(60, f.SliderHeight);
        Assert.Equal(20, MinimapGeometry.TopRowForSliderTop(40, 100, 30, 2, 600), 3);
    }

    [Fact]
    public void A_long_document_scrolls_the_minimap_in_proportion_so_the_slider_spans_top_to_bottom()
    {
        const int rows = 1_000_000;
        var top = MinimapGeometry.Compute(rows, 0, 40, 2, 600);
        Assert.Equal(0, top.WindowTopRow);
        Assert.Equal(0, top.SliderTop);
        Assert.Equal(300, top.WindowRows);

        var end = MinimapGeometry.Compute(rows, rows - 40, 40, 2, 600);
        Assert.Equal(rows - 300, end.WindowTopRow);
        Assert.Equal(600, end.SliderTop + end.SliderHeight, 3); // the slider sits on the minimap's bottom

        var middle = MinimapGeometry.Compute(rows, (rows - 40) / 2.0, 40, 2, 600);
        Assert.InRange(middle.SliderTop, 260, 270); // half of the slider's travel (600 - 80) / 2
        // dragging the slider back to where it is gives the same top row
        Assert.InRange(MinimapGeometry.TopRowForSliderTop(middle.SliderTop, rows, 40, 2, 600), (rows - 40) / 2.0 - 2, (rows - 40) / 2.0 + 2);
        Assert.Equal(0, MinimapGeometry.TopRowForSliderTop(-50, rows, 40, 2, 600));
        Assert.Equal(rows - 40, MinimapGeometry.TopRowForSliderTop(10_000, rows, 40, 2, 600), 3);
    }

    // ------------------------------------------------------------------------------------------------ ruler buckets

    [Fact]
    public void Ruler_marks_are_bucketed_per_pixel_row_and_the_more_severe_wins()
    {
        var m = new OverviewRulerModel();
        m.Reset(100);
        m.Add(10, 2, MarkerKind.Warning);
        m.Add(11, 2, MarkerKind.Error);
        m.Add(11, 1, MarkerKind.Info);
        m.Add(50, 0.1, MarkerKind.FindMatch); // under a pixel still takes one
        m.Add(-5, 2, MarkerKind.Added);       // clamped into the ruler
        m.Add(99.5, 5, MarkerKind.Cursor);
        Assert.Equal(MarkerKind.Warning, m.At(MarkerLane.Right, 10));
        Assert.Equal(MarkerKind.Error, m.At(MarkerLane.Right, 11));
        Assert.Equal(MarkerKind.Error, m.At(MarkerLane.Right, 12));
        Assert.Equal(MarkerKind.None, m.At(MarkerLane.Right, 13));
        Assert.Equal(MarkerKind.FindMatch, m.At(MarkerLane.Center, 50));
        Assert.Equal(MarkerKind.Added, m.At(MarkerLane.Left, 0));
        Assert.Equal(MarkerKind.Cursor, m.At(MarkerLane.Full, 99));
        var runs = new List<(int Top, int Height, MarkerKind Kind)>();
        m.Runs(MarkerLane.Right, runs);
        Assert.Equal([(10, 1, MarkerKind.Warning), (11, 2, MarkerKind.Error)], runs);
    }

    [Fact]
    public void Twenty_thousand_marks_draw_as_at_most_one_run_per_pixel_row()
    {
        var m = new OverviewRulerModel();
        m.Reset(700);
        for (var i = 0; i < 20_000; i++) m.Add(i * 700.0 / 20_000, 1, MarkerKind.FindMatch);
        var runs = new List<(int Top, int Height, MarkerKind Kind)>();
        m.Runs(MarkerLane.Center, runs);
        Assert.Single(runs); // every row is marked: one rectangle
        Assert.Equal((0, 700, MarkerKind.FindMatch), runs[0]);
    }

    // ------------------------------------------------------------------------------------------------ rasteriser

    private static readonly Rgba Bg = new(0x10, 0x10, 0x10, 0xFF), Fg = new(0xD4, 0xD4, 0xD4, 0xFF);

    private static (int[] Pixels, MinimapRenderJob Job) Raster(string[] lines, Func<int, int[]?>? tokens = null, bool characters = false, int maxColumn = 120,
        ColorTheme? theme = null, ScopeInterner? interner = null)
    {
        var text = string.Join('\n', lines);
        var snapshot = new DocumentSnapshot(0, new AvaloniaEdit.Utils.Rope<char>(text.ToCharArray()));
        var job = new MinimapRenderJob
        {
            PixelWidth = 200, PixelHeight = lines.Length * 4, RowPixels = 4, CharPixels = 1, MaxColumn = maxColumn, TabSize = 4, RenderCharacters = characters,
            Snapshot = snapshot, Colors = new MinimapColors(theme ?? new ColorTheme("t", "t", ThemeKind.Dark, new Dictionary<string, Rgba>(), []), Bg, Fg, interner),
        };
        job.EnsureCapacity(lines.Length);
        job.RowCount = lines.Length;
        var offset = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            job.Lines[i] = new MinimapLine { Offset = offset, Length = lines[i].Length, Tokens = tokens?.Invoke(i) };
            offset += lines[i].Length + 1;
        }
        var pixels = new int[job.PixelWidth * job.PixelHeight];
        new MinimapRasterizer().Render(job, pixels);
        return (pixels, job);
    }

    private static uint At(int[] pixels, int x, int y) => unchecked((uint)pixels[y * 200 + x]);

    [Fact]
    public void Text_becomes_cells_blended_over_the_background_with_blank_columns_and_tab_stops()
    {
        var (p, _) = Raster(["ab cd", "\tx"]);
        var bg = unchecked((uint)MinimapRasterizer.Premultiplied(Bg));
        var cell = MinimapRasterizer.Blend(Bg, Fg, 0.55);
        Assert.Equal(cell, At(p, 0, 0));
        Assert.Equal(cell, At(p, 1, 0));
        Assert.Equal(bg, At(p, 2, 0)); // the space
        Assert.Equal(cell, At(p, 4, 0));
        Assert.Equal(bg, At(p, 5, 0));
        Assert.Equal(bg, At(p, 0, 3)); // the gap under a 4-pixel row
        Assert.Equal(bg, At(p, 3, 4)); // the tab runs to column 4
        Assert.Equal(cell, At(p, 4, 4));
    }

    [Fact]
    public void Each_token_has_its_theme_colour_and_lines_are_cut_at_max_column()
    {
        var interner = new ScopeInterner();
        var keyword = interner.Intern(["source.cs", "keyword.control.cs"]);
        var theme = new ColorTheme("t", "t", ThemeKind.Dark, new Dictionary<string, Rgba>(),
            [new TokenColorRule(["keyword"], "#569CD6", null, null)]);
        var (p, _) = Raster(["if (x) return y; and a long tail"], _ => [0, keyword, 2, 0, 7, keyword, 13, 0], theme: theme, interner: interner, maxColumn: 20);
        var blue = MinimapRasterizer.Blend(Bg, Rgba.Parse("#569CD6"), 0.55);
        var plain = MinimapRasterizer.Blend(Bg, Fg, 0.55);
        Assert.Equal(blue, At(p, 0, 0));   // "if"
        Assert.Equal(plain, At(p, 3, 0));  // "x"
        Assert.Equal(blue, At(p, 7, 0));   // "return"
        Assert.Equal(plain, At(p, 14, 0)); // "y"
        Assert.Equal(unchecked((uint)MinimapRasterizer.Premultiplied(Bg)), At(p, 25, 0)); // past maxColumn
    }

    [Fact]
    public void Drawing_a_full_window_of_long_lines_costs_milliseconds_and_allocates_nothing_after_the_first()
    {
        var line = string.Concat(Enumerable.Repeat("token(x, y) + other; ", 10)); // 210 characters: cut at 120
        var lines = Enumerable.Repeat(line, 600).ToArray();
        var text = string.Join('\n', lines);
        var job = new MinimapRenderJob
        {
            PixelWidth = 240, PixelHeight = 2400, RowPixels = 4, CharPixels = 2, MaxColumn = 120, RenderCharacters = true, // a 2x screen
            Snapshot = new DocumentSnapshot(0, new AvaloniaEdit.Utils.Rope<char>(text.ToCharArray())),
            Colors = new MinimapColors(new ColorTheme("t", "t", ThemeKind.Dark, new Dictionary<string, Rgba>(), []), Bg, Fg, null),
        };
        job.EnsureCapacity(600);
        job.RowCount = 600;
        for (var i = 0; i < 600; i++) job.Lines[i] = new MinimapLine { Offset = i * (line.Length + 1), Length = line.Length };
        var pixels = new int[240 * 2400];
        var raster = new MinimapRasterizer();
        raster.Render(job, pixels); // warm up
        var sw = new System.Diagnostics.Stopwatch();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        sw.Start();
        for (var i = 0; i < 10; i++) raster.Render(job, pixels);
        sw.Stop();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - allocated);
        Assert.True(sw.Elapsed.TotalMilliseconds / 10 < 50, $"{sw.Elapsed.TotalMilliseconds / 10:F2} ms per window");
        TestContext.Current.TestOutputHelper?.WriteLine($"one 600-row window at 2x: {sw.Elapsed.TotalMilliseconds / 10:F2} ms");
    }

    [Fact]
    public void Character_rendering_shapes_cells_by_character_class()
    {
        var (p, _) = Raster(["Ab."], characters: true);
        var bg = unchecked((uint)MinimapRasterizer.Premultiplied(Bg));
        Assert.NotEqual(bg, At(p, 0, 0)); // a capital is full height
        Assert.Equal(bg, At(p, 1, 0));    // lower case starts lower
        Assert.NotEqual(bg, At(p, 1, 2));
        Assert.Equal(bg, At(p, 2, 1));    // punctuation is a low mark
        Assert.NotEqual(bg, At(p, 2, 2));
        Assert.NotEqual(At(p, 0, 2), At(p, 2, 2)); // ...and lighter
    }
}
