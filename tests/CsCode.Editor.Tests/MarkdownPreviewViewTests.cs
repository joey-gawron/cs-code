using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CsCode.Core.Text;
using CsCode.Editor.Documents;
using CsCode.Editor.Markdown.Preview;
using CsCode.Editor.Tests.Support;
using CsCode.TestDoubles;
using Markdig.Syntax;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>
/// The preview view over the real editor module (docs/MARKDOWN.md): live updates from the document, incremental re-rendering, theme changes,
/// virtualisation of long documents, scroll positions by source line, anchors, images, a closed source and release of everything on dispose.
/// </summary>
public class MarkdownPreviewViewTests
{
    private static MarkdownPreviewFactory Previews(ViewHost host) => host.Provider.GetRequiredService<MarkdownPreviewFactory>();

    private static async Task<(EditorDocument Doc, MarkdownPreviewView View, Window Window)> OpenAsync(ViewHost host, string text, string name = "README.md", double height = 600)
    {
        host.Config.Set(MarkdownPreviewSettings.UpdateDelay, 10);
        var path = host.Dir.WriteFile(name, text);
        var doc = (EditorDocument)await host.Factory.OpenDocumentAsync(new Uri(path));
        var view = Previews(host).Create(doc.Uri);
        var window = new Window { Width = 800, Height = height, Content = view };
        window.Show();
        await ViewHost.WaitFor(() => view.Document.Blocks.Count > 0 || text.Length == 0, "the first render");
        ViewHost.Pump();
        return (doc, view, window);
    }

    private static IEnumerable<string> VisibleTexts(MarkdownPreviewView view) =>
        view.Blocks.GetLogicalDescendants().OfType<SelectableTextBlock>().Select(t => t.Inlines is { Count: > 0 } i ? string.Concat(i.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text)) : t.Text ?? "");

    [AvaloniaFact]
    public async Task The_preview_renders_the_document_and_follows_edits_after_the_delay()
    {
        using var host = ViewHost.Create();
        var (doc, view, window) = await OpenAsync(host, "# Title\n\nfirst paragraph\n");
        Assert.Equal(2, view.Document.Blocks.Count);
        Assert.Contains("first paragraph", VisibleTexts(view));
        Assert.Equal("Preview README.md", view.Title);

        doc.ApplyEdits([new TextEdit(TextRange.At(2, 0), "edited ")]);
        await ViewHost.WaitFor(() => VisibleTexts(view).Contains("edited first paragraph"), "the edit to reach the preview");
        Assert.Equal(doc.Version, view.Document.Version);
        window.Close();
        view.Dispose();
    }

    [AvaloniaFact]
    public async Task Typing_in_one_paragraph_rebuilds_that_block_only()
    {
        using var host = ViewHost.Create();
        var (doc, view, window) = await OpenAsync(host, "# Title\n\none\n\ntwo\n\nthree\n");
        var before = Enumerable.Range(0, 4).Select(view.Blocks.RealizedControl).ToArray();
        Assert.All(before, Assert.NotNull);

        doc.ApplyEdits([new TextEdit(TextRange.At(4, 3), "!")]);
        await ViewHost.WaitFor(() => VisibleTexts(view).Contains("two!"), "the edit");
        var after = Enumerable.Range(0, 4).Select(view.Blocks.RealizedControl).ToArray();
        Assert.Same(before[0], after[0]);
        Assert.Same(before[1], after[1]);
        Assert.NotSame(before[2], after[2]);
        Assert.Same(before[3], after[3]);
        window.Close();
        view.Dispose();
    }

    [AvaloniaFact]
    public async Task Fenced_code_is_coloured_with_the_editors_TextMate_grammar_and_theme()
    {
        using var host = ViewHost.Create();
        var (_, view, window) = await OpenAsync(host, "```csharp\npublic class C { }\n```\n");
        var code = (LeafBlock)view.Document.Blocks[0].Block;
        Assert.True(view.Document.CodeSpans.TryGetValue(code, out var spans) && spans.Count > 0, "no TextMate colours for the C# block");
        var tb = view.Blocks.GetLogicalDescendants().OfType<SelectableTextBlock>().Single();
        var keyword = tb.Inlines!.OfType<Avalonia.Controls.Documents.Run>().First(r => r.Text == "public");
        Assert.NotNull(keyword.Foreground);
        Assert.NotEqual(((ISolidColorBrush)view.Style.Foreground).Color, ((ISolidColorBrush)keyword.Foreground!).Color);
        window.Close();
        view.Dispose();
    }

    [AvaloniaFact]
    public async Task A_theme_change_restyles_the_preview_and_recolours_its_code()
    {
        using var host = ViewHost.Create();
        var (_, view, window) = await OpenAsync(host, "text\n\n```csharp\nvar x = 1;\n```\n");
        var darkBackground = ((ISolidColorBrush)view.Background!).Color;
        var firstRender = view.Document;

        await host.Factory.Themes.SetCurrentAsync("vscode.light-plus", TestContext.Current.CancellationToken);
        await ViewHost.WaitFor(() => !ReferenceEquals(view.Document, firstRender), "the re-render after the theme change");
        var lightBackground = ((ISolidColorBrush)view.Background!).Color;
        Assert.NotEqual(darkBackground, lightBackground);
        Assert.True(lightBackground.R > 200, $"light theme background is {lightBackground}");
        var text = view.Blocks.GetLogicalDescendants().OfType<SelectableTextBlock>().First();
        Assert.Same(view.Style.Foreground, text.Foreground);
        window.Close();
        view.Dispose();
    }

    [AvaloniaFact]
    public async Task A_long_document_builds_controls_only_near_the_viewport_and_scrolls_by_source_line()
    {
        using var host = ViewHost.Create();
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 3000; i++) sb.Append("## Section ").Append(i).Append("\n\nParagraph number ").Append(i).Append(" with some words in it.\n\n");
        var (_, view, window) = await OpenAsync(host, sb.ToString(), height: 600);
        Assert.Equal(6000, view.Document.Blocks.Count);
        Assert.True(view.Blocks.RealizedCount < 120, $"{view.Blocks.RealizedCount} blocks have controls");
        Assert.True(view.Scroller.Extent.Height > 100_000, $"extent {view.Scroller.Extent.Height}");

        // Section 2000's heading is on source line 8000
        view.ScrollToLine(8000);
        await ViewHost.WaitFor(() => VisibleTexts(view).Contains("Section 2000"), "section 2000 to be built");
        ViewHost.Pump();
        Assert.InRange(view.TopLine, 7999, 8001.5);
        Assert.True(view.Blocks.RealizedCount < 120, $"{view.Blocks.RealizedCount} blocks have controls after the jump");
        Assert.DoesNotContain("Section 0", VisibleTexts(view)); // the top's controls were dropped
        window.Close();
        view.Dispose();
    }

    [AvaloniaFact]
    public async Task A_long_document_preview_stays_within_its_memory_budget()
    {
        // docs/MEMORY.md, "Markdown preview": the parsed tree is kept (about 40 bytes per source character), controls only near the viewport.
        using var host = ViewHost.Create();
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 2000; i++)
            sb.Append("## Section ").Append(i).Append("\n\nSome **bold** and `code` and a [link](https://example.com/").Append(i).Append(") in paragraph ").Append(i).Append(".\n\n- item one\n- item two\n\n```csharp\nvar x").Append(i).Append(" = ").Append(i).Append(";\n```\n\n");
        var text = sb.ToString();
        host.Config.Set(MarkdownPreviewSettings.UpdateDelay, 10);
        var path = host.Dir.WriteFile("big.md", text);
        var doc = await host.Factory.OpenDocumentAsync(new Uri(path));
        var window = new Window { Width = 800, Height = 600 };
        window.Show();
        ViewHost.Pump();
        // a first preview loads what every later one shares (the C# grammar, Markdig's pipeline, JIT): not part of a preview's cost
        var warm = Previews(host).Create(doc.Uri);
        window.Content = warm;
        await ViewHost.WaitFor(() => warm.Document.Blocks.Count == 8000, "the warm-up render");
        window.Content = null;
        warm.Dispose();
        ViewHost.Pump();
        var before = Settled();
        var view = Previews(host).Create(doc.Uri);
        window.Content = view;
        await ViewHost.WaitFor(() => view.Document.Blocks.Count == 8000, "the render");
        ViewHost.Pump();
        var after = Settled();
        var perChar = (after - before) / (double)text.Length;
        Assert.True(perChar < 120, $"the preview of a {text.Length / 1024} KB document holds {(after - before) / 1024 / 1024.0:F1} MB ({perChar:F0} bytes per source character)");
        Assert.True(view.Blocks.RealizedCount < 150, $"{view.Blocks.RealizedCount} blocks have controls");
        TestContext.Current.SendDiagnosticMessage($"markdown preview: {text.Length / 1024} KB source, {(after - before) / 1024 / 1024.0:F1} MB retained, {perChar:F0} B/char, {view.Blocks.RealizedCount} blocks realised");
        window.Close();
        view.Dispose();

        static long Settled()
        {
            for (var i = 0; i < 3; i++)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
            }
            return GC.GetTotalMemory(true);
        }
    }

    [AvaloniaFact]
    public async Task Scrolled_reports_user_scrolling_but_not_ScrollToLine()
    {
        using var host = ViewHost.Create();
        var text = string.Concat(Enumerable.Range(0, 200).Select(i => $"Paragraph {i}\n\n"));
        var (_, view, window) = await OpenAsync(host, text, height: 300);
        var reported = new List<double>();
        view.Scrolled.Subscribe(reported.Add);

        view.ScrollToLine(100);
        ViewHost.Pump();
        ViewHost.Pump();
        Assert.Empty(reported);
        Assert.InRange(view.TopLine, 99.5, 100.5);

        view.Scroller.Offset = new Vector(0, view.Scroller.Offset.Y + 400);
        ViewHost.Pump();
        Assert.NotEmpty(reported);
        Assert.True(reported[^1] > 105, $"reported line {reported[^1]}");
        window.Close();
        view.Dispose();
    }

    [AvaloniaFact]
    public async Task A_scroll_requested_before_the_first_render_is_applied_by_it()
    {
        using var host = ViewHost.Create();
        host.Config.Set(MarkdownPreviewSettings.UpdateDelay, 10);
        var path = host.Dir.WriteFile("early.md", string.Concat(Enumerable.Range(0, 300).Select(i => $"Line {i}\n\n")));
        var doc = await host.Factory.OpenDocumentAsync(new Uri(path));
        var view = Previews(host).Create(doc.Uri);
        view.ScrollToLine(200);
        var window = new Window { Width = 800, Height = 300, Content = view };
        window.Show();
        await ViewHost.WaitFor(() => view.Document.Blocks.Count > 0 && Math.Abs(view.TopLine - 200) < 1.5, "the pending scroll");
        window.Close();
        view.Dispose();
    }

    [AvaloniaFact]
    public async Task Anchor_links_scroll_to_their_heading_and_other_links_are_reported()
    {
        using var host = ViewHost.Create();
        var text = "[jump](#far-away) [web](https://example.com)\n\n" + string.Concat(Enumerable.Range(0, 100).Select(i => $"filler {i}\n\n")) + "## Far Away\n\n" + string.Concat(Enumerable.Range(0, 100).Select(i => $"after {i}\n\n"));
        var (_, view, window) = await OpenAsync(host, text, height: 300);
        var links = new List<string>();
        view.LinkClicked.Subscribe(links.Add);
        var first = (SelectableTextBlock)view.Blocks.RealizedControl(0)!;
        Assert.Equal(["#far-away", "https://example.com"], MarkdownBlockRenderer.GetLinks(first)!.Select(l => l.Url));
        Assert.True(view.RevealAnchor("#far-away"));
        ViewHost.Pump();
        ViewHost.Pump();
        Assert.InRange(view.TopLine, 201, 203);
        Assert.False(view.RevealAnchor("missing"));

        window.Close();
        view.Dispose();
    }

    [AvaloniaFact]
    public async Task Editing_a_link_definition_updates_the_paragraph_that_references_it()
    {
        using var host = ViewHost.Create();
        var (doc, view, window) = await OpenAsync(host, "see [foo]\n\n[foo]: https://a.example/\n");
        var para = (SelectableTextBlock)view.Blocks.RealizedControl(0)!;
        Assert.Equal("https://a.example/", MarkdownBlockRenderer.GetLinks(para)![0].Url);

        doc.ApplyEdits([new TextEdit(new TextRange(new TextPosition(2, 15), new TextPosition(2, 16)), "b")]);
        await ViewHost.WaitFor(() => view.Document.Version == doc.Version, "the re-render");
        ViewHost.Pump();
        para = (SelectableTextBlock)view.Blocks.RealizedControl(0)!;
        Assert.Equal("https://b.example/", MarkdownBlockRenderer.GetLinks(para)![0].Url);
        window.Close();
        view.Dispose();
    }

    [AvaloniaFact]
    public async Task A_theme_change_still_restyles_every_block_when_another_render_supersedes_its_own()
    {
        using var host = ViewHost.Create();
        var (_, view, window) = await OpenAsync(host, "text\n\nmore text\n");
        var firstRender = view.Document;

        await host.Factory.Themes.SetCurrentAsync("vscode.light-plus", TestContext.Current.CancellationToken);
        view.Refresh(); // e.g. a debounced keystroke firing while the theme's render is in flight
        await ViewHost.WaitFor(() => !ReferenceEquals(view.Document, firstRender), "the re-render after the theme change");
        ViewHost.Pump();
        Assert.All(view.Blocks.GetLogicalDescendants().OfType<SelectableTextBlock>(), t => Assert.Same(view.Style.Foreground, t.Foreground));
        window.Close();
        view.Dispose();
    }

    [AvaloniaFact]
    public async Task Local_and_data_images_load_and_remote_ones_are_blocked_by_default()
    {
        using var host = ViewHost.Create();
        var png = TinyPng();
        File.WriteAllBytes(host.Dir.Combine("pic.png"), png);
        var data = "data:image/png;base64," + Convert.ToBase64String(png);
        var (_, view, window) = await OpenAsync(host, $"![local](pic.png)\n\n![inline]({data})\n\n![remote](https://example.com/x.png)\n");
        await ViewHost.WaitFor(() => view.Images.LoadedCount == 2, "both images to load");
        var images = view.Blocks.GetLogicalDescendants().OfType<Image>().ToList();
        Assert.Equal(2, images.Count);
        Assert.All(images, i => Assert.Equal(new PixelSize(4, 3), ((Bitmap)i.Source!).PixelSize));
        var blocked = view.Blocks.GetLogicalDescendants().OfType<Border>().Single(b => b.Child is TextBlock { Text: "remote" });
        Assert.Contains(MarkdownPreviewSettings.AllowRemoteImages, ToolTip.GetTip(blocked) as string);
        window.Close();
        view.Dispose();
    }

    [AvaloniaFact]
    public void Wide_images_are_decoded_downscaled_and_svg_is_not_decoded()
    {
        using var bmp = new WriteableBitmap(new PixelSize(MarkdownImageLoader.MaxDecodeWidth + 500, 10), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
        using var ms = new MemoryStream();
        bmp.Save(ms, new PngBitmapEncoderOptions());
        using var decoded = MarkdownImageLoader.Decode(ms.ToArray());
        Assert.Equal(MarkdownImageLoader.MaxDecodeWidth, decoded!.PixelSize.Width);
        Assert.Null(MarkdownImageLoader.Decode(System.Text.Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'/>")));
    }

    [AvaloniaFact]
    public async Task A_preview_of_a_file_that_is_not_open_reads_it_and_picks_the_document_up_when_it_opens()
    {
        using var host = ViewHost.Create();
        host.Config.Set(MarkdownPreviewSettings.UpdateDelay, 10);
        var path = host.Dir.WriteFile("closed.md", "# From disk\n");
        var view = Previews(host).Create(new Uri(path));
        var window = new Window { Width = 600, Height = 400, Content = view };
        window.Show();
        await ViewHost.WaitFor(() => VisibleTexts(view).Contains("From disk"), "the file's content");

        var doc = (EditorDocument)await host.Factory.OpenDocumentAsync(new Uri(path));
        doc.ApplyEdits([new TextEdit(TextRange.At(0, 2), "Live: ")]);
        await ViewHost.WaitFor(() => VisibleTexts(view).Contains("Live: From disk"), "the open document's edit");
        window.Close();
        view.Dispose();
    }

    [AvaloniaFact]
    public async Task Settings_change_the_font_size_and_line_breaks_live()
    {
        using var host = ViewHost.Create();
        var (_, view, window) = await OpenAsync(host, "one\ntwo\n");
        var first = view.Document;
        host.Config.Set(MarkdownPreviewSettings.FontSize, 20);
        host.Config.Set(MarkdownPreviewSettings.Breaks, true);
        await ViewHost.WaitFor(() => view.Style.FontSize == 20 && !ReferenceEquals(first, view.Document)
            && view.Blocks.GetLogicalDescendants().OfType<SelectableTextBlock>().Any(t => t.Inlines!.OfType<Avalonia.Controls.Documents.LineBreak>().Any()), "the settings to apply");
        Assert.Equal(20, view.Blocks.GetLogicalDescendants().OfType<SelectableTextBlock>().First().FontSize);
        window.Close();
        view.Dispose();
    }

    [AvaloniaFact]
    public async Task A_disposed_preview_is_released_and_stops_following_its_document()
    {
        using var host = ViewHost.Create();
        host.Config.Set(MarkdownPreviewSettings.UpdateDelay, 10);
        var path = host.Dir.WriteFile("leak.md", "# a\n\n```csharp\nvar x = 1;\n```\n\n![p](missing.png)\n");
        var doc = (EditorDocument)await host.Factory.OpenDocumentAsync(new Uri(path));
        var weak = await CreateRenderAndDisposeAsync(host, doc);
        doc.ApplyEdits([new TextEdit(TextRange.At(0, 0), "x")]); // must not reach a disposed view
        for (var i = 0; i < 10 && weak.TryGetTarget(out _); i++)
        {
            ViewHost.Pump();
            await Task.Delay(20);
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        Assert.False(weak.TryGetTarget(out _), "the disposed preview is still reachable");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference<MarkdownPreviewView>> CreateRenderAndDisposeAsync(ViewHost host, EditorDocument doc)
    {
        var view = Previews(host).Create(doc.Uri);
        var window = new Window { Width = 600, Height = 400, Content = view };
        window.Show();
        await ViewHost.WaitFor(() => view.Document.Blocks.Count == 3, "the render");
        window.Content = null;
        window.Close();
        view.Dispose();
        ViewHost.Pump();
        return new WeakReference<MarkdownPreviewView>(view);
    }

    [AvaloniaFact]
    public async Task The_editor_reports_its_top_line_scrolls_to_a_line_and_raises_Scrolled()
    {
        using var host = ViewHost.Create();
        var (_, editor, _) = await host.OpenAsync(string.Concat(Enumerable.Range(0, 500).Select(i => $"line {i}\n")), "long.md", height: 300);
        var controller = editor.Controller;
        var reported = new List<double>();
        controller.Scrolled.Subscribe(reported.Add);
        Assert.Equal(0, controller.TopLine, 3);

        controller.ScrollToLine(120.5);
        ViewHost.Pump();
        Assert.InRange(controller.TopLine, 120.3, 120.7);
        Assert.NotEmpty(reported);
        Assert.InRange(reported[^1], 120.3, 120.7);
        // the caret stays where it was
        Assert.Equal(new TextPosition(0, 0), controller.Caret);
    }

    /// <summary>A 4x3 PNG (made with Avalonia, so it is valid on every platform).</summary>
    private static byte[] TinyPng()
    {
        using var bmp = new WriteableBitmap(new PixelSize(4, 3), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
        using var ms = new MemoryStream();
        bmp.Save(ms, new PngBitmapEncoderOptions());
        return ms.ToArray();
    }
}
