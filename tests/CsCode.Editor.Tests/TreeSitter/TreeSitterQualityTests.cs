using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CsCode.Core.Theming;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.Theming;
using CsCode.Editor.TreeSitter;
using CsCode.Editor.View;
using Xunit;
using static CsCode.Editor.Tests.TreeSitter.TreeSitterTestSupport;

namespace CsCode.Editor.Tests.TreeSitter;

/// <summary>
/// Quality evidence for docs/TREE-SITTER-EVALUATION.md: each evaluated language is rendered headlessly with TextMate and with tree-sitter
/// (same document, same theme, the setting switched live) into one side-by-side PNG under <c>.scratch/screenshots/treesitter/</c>, and the
/// per-character foreground colours of the two engines are compared (Dark+). The agreement and the most frequent differences are written to
/// <c>.scratch/treesitter/quality-&lt;language&gt;.md</c>. Asserted: both engines colour the sample, and they agree on most characters
/// (the capture → scope mapping keeps every theme meaningful).
/// </summary>
public class TreeSitterQualityTests
{
    public static TheoryData<string> Languages => [.. TreeSitterSamples.All.Select(s => s.Name)];

    internal static string ScratchDir(string sub)
    {
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.scratch", sub));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public async Task TextMate_and_tree_sitter_side_by_side(string language)
    {
        var sample = TreeSitterSamples.All.Single(s => s.Name == language);
        RequireGrammar(sample.Grammar);
        using var host = ViewHost.Create();
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        host.Factory.Worker.OnError = errors.Enqueue;
        await host.Factory.Themes.SetCurrentAsync("vscode.dark-plus");
        // rainbow brackets are an editor feature on top of either engine: off, so the two halves differ only by the engine
        host.Config.Set(CsCode.Core.Platform.SettingKeys.EditorBracketPairColorization, false);
        var (doc, view, window) = await host.OpenAsync(sample.Text, sample.FileName, 820, 760);
        var lines = doc.LineCount;

        // TextMate
        try
        {
            await ViewHost.WaitFor(() => view.Tokens is { } t && t.Store.ValidLines >= lines, "TextMate tokens");
        }
        catch (TimeoutException)
        {
            Assert.Fail($"TextMate tokens: valid {view.Tokens?.Store.ValidLines} of {lines} (store lines {view.Tokens?.Store.LineCount}, has tokens {view.Tokens?.Store.HasTokens(0, lines - 1)}, viewport {view.Tokens?.Viewport}, errors: {string.Join(" | ", errors.Select(e => e.ToString()[..Math.Min(600, e.ToString().Length)]))})");
        }
        ViewHost.Pump();
        var textMateFrame = window.CaptureRenderedFrame()!;
        var textMate = Colours(view, doc.Snapshot().GetText());

        // tree-sitter, switched live on the same view
        host.Config.Set(TreeSitterService.SettingKey, TreeSitterService.TreeSitter);
        await ViewHost.WaitFor(() => view.Tokens?.TreeSitter is { CompletedParses: > 0 } e && !e.Syntax.IsParsing && view.Tokens.Store.HasTokens(0, lines - 1), "tree-sitter tokens");
        ViewHost.Pump();
        var treeSitterFrame = window.CaptureRenderedFrame()!;
        var treeSitter = Colours(view, doc.Snapshot().GetText());

        var shots = ScratchDir("screenshots/treesitter");
        SaveSideBySide(textMateFrame, treeSitterFrame, Path.Combine(shots, $"{language}.png"), $"{language} - TextMate", $"{language} - tree-sitter (editor.experimental.treeSitter)");

        var (agreement, report) = Compare(doc.Snapshot().GetText(), textMate, treeSitter);
        File.WriteAllText(Path.Combine(ScratchDir("treesitter"), $"quality-{language}.md"), $"# {language}: TextMate vs tree-sitter (Dark+)\n\nagreement {agreement:P1} of non-blank characters\n\n{report}");

        // both engines really colour the sample (several distinct foregrounds), and they mostly agree
        Assert.True(textMate.Where(c => c.Fg is not null).Select(c => c.Fg).Distinct().Count() >= 3, "TextMate colours");
        Assert.True(treeSitter.Where(c => c.Fg is not null).Select(c => c.Fg).Distinct().Count() >= 3, "tree-sitter colours");
        Assert.True(agreement >= 0.55, $"{language}: only {agreement:P1} of the characters have the same colour\n{report}");
    }

    private sealed record CharColour(Rgba? Fg, string Leaf);

    private static CharColour[] Colours(CodeEditorControl view, string text)
    {
        var themes = view.Factory.Themes;
        var resolver = new TokenColorResolver(themes.Current, themes.GetColor("editor.foreground"));
        var snapshot = view.Document.Snapshot();
        var result = new CharColour[text.Length];
        Array.Fill(result, new CharColour(null, ""));
        for (var line = 0; line < snapshot.LineCount; line++)
        {
            var start = snapshot.LineStart(line);
            var length = snapshot.LineContentLength(line);
            for (var c = 0; c < length; c++)
            {
                var id = TreeSitterViewTests.IdAt(view, line, c);
                var scopes = id >= 0 ? view.Tokens!.Interner.Scopes(id) : [];
                result[start + c] = new CharColour(resolver.Resolve(scopes).Foreground, scopes.Length > 0 ? scopes[^1] : "");
            }
        }
        return result;
    }

    private static (double Agreement, string Report) Compare(string text, CharColour[] a, CharColour[] b)
    {
        var total = 0;
        var same = 0;
        var diffs = new Dictionary<(string, string), (int Count, string Example)>();
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i])) continue;
            total++;
            if (a[i].Fg == b[i].Fg)
            {
                same++;
                continue;
            }
            var key = (a[i].Leaf, b[i].Leaf);
            var start = i;
            while (start > 0 && char.IsLetterOrDigit(text[start - 1])) start--;
            var end = i;
            while (end < text.Length && char.IsLetterOrDigit(text[end])) end++;
            var word = text[start..Math.Max(end, i + 1)];
            diffs[key] = diffs.TryGetValue(key, out var d) ? (d.Count + 1, d.Example) : (1, word);
        }
        var sb = new StringBuilder("| chars | TextMate scope | tree-sitter scope | example |\n|---:|---|---|---|\n");
        foreach (var ((tm, ts), (count, example)) in diffs.OrderByDescending(kv => kv.Value.Count).Take(15))
            sb.Append(CultureInfo.InvariantCulture, $"| {count} | `{tm}` | `{ts}` | `{example.Replace("|", "\\|", StringComparison.Ordinal)}` |\n");
        return (total == 0 ? 1 : (double)same / total, sb.ToString());
    }

    private static void SaveSideBySide(Bitmap left, Bitmap right, string path, string leftLabel, string rightLabel)
    {
        const int header = 26;
        const int gap = 6;
        var w = left.PixelSize.Width + gap + right.PixelSize.Width;
        var h = Math.Max(left.PixelSize.Height, right.PixelSize.Height) + header;
        using var target = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
        using (var ctx = target.CreateDrawingContext())
        {
            ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)), new Rect(0, 0, w, h));
            ctx.DrawImage(left, new Rect(0, header, left.PixelSize.Width, left.PixelSize.Height));
            ctx.DrawImage(right, new Rect(left.PixelSize.Width + gap, header, right.PixelSize.Width, right.PixelSize.Height));
            var face = new Typeface(FontFamily.Default);
            ctx.DrawText(new FormattedText(leftLabel, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 14, Brushes.White), new Point(8, 4));
            ctx.DrawText(new FormattedText(rightLabel, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 14, Brushes.White), new Point(left.PixelSize.Width + gap + 8, 4));
        }
        using var fs = File.Create(path);
        target.Save(fs, new PngBitmapEncoderOptions());
    }
}
