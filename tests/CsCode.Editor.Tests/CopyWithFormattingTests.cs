using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Core.Theming;
using CsCode.Editor.Editing;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.Theming;
using CsCode.Editor.View;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>
/// Copy with Formatting (clipboard brief A7): the copied text plus an HTML rendition with the theme's syntax colours, offered as text/html by the
/// clipboard service. The fragment format (pure) and the real view (TextMate tokens of a C# file, the current theme).
/// </summary>
public class CopyWithFormattingTests
{
    private static readonly HtmlClipboardStyle Style = new(new Rgba(0x1e, 0x1e, 0x1e), new Rgba(0xd4, 0xd4, 0xd4), "Cascadia Code, Consolas", 14, 19, 4);
    private static ResolvedTokenStyle Fg(byte r, byte g, byte b, bool bold = false, bool italic = false) => new(new Rgba(r, g, b), null, bold, italic, false, false);

    [Fact]
    public void The_fragment_has_the_editor_colours_and_font_one_div_per_line_and_one_span_per_style_run()
    {
        var keyword = Fg(0x56, 0x9c, 0xd6);
        var html = HtmlClipboard.Build(
        [
            new HtmlClipboardLine("var x = \"<a&b>\";", [new HtmlClipboardRun(0, 3, keyword), new HtmlClipboardRun(8, 7, Fg(0xd6, 0x9d, 0x85, italic: true))]),
            new HtmlClipboardLine("", []),
            new HtmlClipboardLine("\treturn;", [new HtmlClipboardRun(1, 3, keyword), new HtmlClipboardRun(4, 3, keyword)]), // equal styles merge
        ], Style);

        Assert.StartsWith("<meta charset=\"utf-8\"><div style=\"color: #d4d4d4;background-color: #1e1e1e;font-family: 'Cascadia Code', 'Consolas', monospace;", html);
        Assert.Contains("font-size: 14px;line-height: 19px;white-space: pre;tab-size: 4;", html);
        Assert.Equal(3, Regex.Matches(html, "<div>").Count);
        Assert.Contains("<div><span style=\"color: #569cd6;\">var</span><span style=\"color: #d4d4d4;\"> x = </span>" +
                        "<span style=\"color: #d69d85;font-style: italic;\">&quot;&lt;a&amp;b&gt;&quot;</span><span style=\"color: #d4d4d4;\">;</span></div>", html);
        Assert.Contains("<div><br></div>", html); // an empty line keeps its height
        Assert.Contains("<div><span style=\"color: #d4d4d4;\">\t</span><span style=\"color: #569cd6;\">return</span><span style=\"color: #d4d4d4;\">;</span></div>", html);
        Assert.EndsWith("</div></div>", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Bold_underline_backgrounds_translucent_colours_and_hostile_font_names_are_written_safely()
    {
        var html = HtmlClipboard.Build([new HtmlClipboardLine("ab", [new HtmlClipboardRun(0, 1, new ResolvedTokenStyle(new Rgba(1, 2, 3, 128), new Rgba(4, 5, 6), true, false, true, true))])],
            Style with { FontFamily = "Evil\"><script>, 'Fira Code', monospace" });
        Assert.Contains("color: rgba(1, 2, 3, 0.502);background-color: #040506;font-weight: bold;text-decoration: underline line-through;", html);
        Assert.Contains("font-family: 'Fira Code', monospace;", html); // the unsafe name is dropped
        Assert.DoesNotContain("<script", html);
    }

    [Fact]
    public void An_avalonia_font_collection_name_is_written_as_its_family_name() =>
        Assert.Equal("'Fira Code', 'Menlo', monospace", HtmlClipboard.CssFontFamily("fonts:CsCode#Fira Code, Menlo"));

    [AvaloniaFact]
    public async Task A_users_font_setting_reaches_the_html_as_written()
    {
        var clipboard = new InMemoryClipboardService();
        using var host = ViewHost.Create(s => s.AddSingleton<IClipboardService>(clipboard));
        var (_, view, _) = await host.OpenAsync("class A {}\n");
        host.Config.Set("editor.fontFamily", "'Fira Code', Menlo, monospace");
        ViewHost.Pump();
        view.Controller.SetSelection(TextRange.FromLines(0, 0, 0, 10));
        await view.Controller.CopyWithFormattingAsync();
        Assert.Contains("font-family: 'Fira Code', 'Menlo', monospace;", clipboard.Content!.Html!);
    }

    [AvaloniaFact]
    public async Task Copy_with_formatting_in_a_real_view_puts_the_text_and_html_with_the_themes_token_colours_on_the_clipboard()
    {
        var clipboard = new InMemoryClipboardService();
        using var host = ViewHost.Create(s => s.AddSingleton<IClipboardService>(clipboard));
        var (doc, view, _) = await host.OpenAsync("using System;\n\nclass Program\n{\n    // a comment\n    static string Name => \"<hi>\";\n}\n");
        await ViewHost.WaitFor(() => view.Tokens is { } t && t.Store.ValidLines >= 7, "tokens");

        view.Controller.SetSelection(TextRange.FromLines(0, 0, 6, 1));
        await view.Controller.CopyWithFormattingAsync();
        var clip = clipboard.Content!;
        Assert.Equal(doc.GetText(TextRange.FromLines(0, 0, 6, 1)), clip.Text); // the text is the plain copy
        var html = Assert.IsType<string>(clip.Html);

        var themes = host.Provider.GetRequiredService<IColorThemeService>();
        var resolver = new TokenColorResolver(themes.Current, themes.GetColor("editor.foreground"));
        string Color(params string[] scopes) => HtmlClipboard.Hex(resolver.Resolve(scopes).Foreground!.Value);
        Assert.Contains($"<span style=\"color: {Color("source.cs", "keyword.other.directive.using.cs")};\">using</span>", html);
        Assert.Contains($"color: {Color("source.cs", "comment.line.double-slash.cs")};", html);
        Assert.Contains("&quot;&lt;hi&gt;&quot;", html);
        Assert.Contains($"background-color: {HtmlClipboard.Hex(themes.GetColor("editor.background"))};", html);
        Assert.Equal(7, Regex.Matches(html, "<div>").Count);
        // the font as the setting names it (FontFamilies.DefaultCode): Fira Code by name, not the collection the view builds its font from
        Assert.Contains("font-family: " + HtmlClipboard.CssFontFamily(FontFamilies.DefaultCode) + ";", html);
        Assert.Contains("font-family: 'Fira Code', ", html);
        Assert.DoesNotContain("fonts:", html);
        Assert.DoesNotContain("compositefont", html);

        // The plain Copy carries no HTML.
        await view.Controller.CopyAsync();
        Assert.Null(clipboard.Content!.Html);
        // A caret copies its whole line, formatted too.
        view.Controller.SetSelection(TextRange.At(0, 3));
        await view.Controller.CopyWithFormattingAsync();
        Assert.Equal("using System;\n", clipboard.Content!.Text);
        Assert.Single(Regex.Matches(clipboard.Content!.Html!, "<div>"));
    }

    [AvaloniaFact]
    public async Task A_copy_over_the_limit_carries_only_text_and_the_ui_thread_only_gathers_the_lines()
    {
        var clipboard = new InMemoryClipboardService();
        using var host = ViewHost.Create(s => s.AddSingleton<IClipboardService>(clipboard));
        var line = "    static int Value = 42; // a line of C#\n";
        var (doc, view, _) = await host.OpenAsync(string.Concat(Enumerable.Repeat(line, 3000)));
        await ViewHost.WaitFor(() => view.Tokens is { } t && t.Store.ValidLines >= 3000, "tokens", 60_000);

        // Just under the limit: HTML, and the UI-thread part (gathering lines and token rows) stays small.
        var lines = HtmlClipboard.MaxChars / line.Length;
        view.Controller.SetSelection(TextRange.FromLines(0, 0, lines, 0));
        var sw = Stopwatch.StartNew();
        var copy = view.Controller.CopyWithFormattingAsync();
        var synchronous = sw.Elapsed.TotalMilliseconds; // up to the first await: the worker builds the HTML
        await copy;
        Assert.NotNull(clipboard.Content!.Html);
        Assert.True(synchronous < 50, $"the UI thread spent {synchronous:F1} ms before handing the HTML to the worker");

        view.Controller.SetSelection(TextRange.FromLines(0, 0, 2999, 0)); // over 64K characters
        await view.Controller.CopyWithFormattingAsync();
        Assert.True(clipboard.Content!.Text.Length > HtmlClipboard.MaxChars);
        Assert.Null(clipboard.Content.Html);
        _ = doc;
    }

    [AvaloniaFact]
    public async Task The_editor_contributes_Copy_with_Formatting_to_its_right_click_menu_and_registers_the_command()
    {
        using var host = ViewHost.Create();
        await host.OpenAsync("class A {}\n");
        var commands = host.Commands;
        Assert.True(commands.Has(EditorMenus.CopyWithFormatting));
        Assert.Equal("Copy with Formatting", commands.All.Single(c => c.Id == EditorMenus.CopyWithFormatting).Title);
        var contributions = host.Factory.Services.Contributions.Of<Core.Contributions.MenuContribution>();
        var item = Assert.Single(contributions.Items.Select(c => c.Value), c => c.Command == EditorMenus.CopyWithFormatting && c.MenuId == "editor/context");
        Assert.Equal("Copy wit_h Formatting", item.Title);
    }
}
