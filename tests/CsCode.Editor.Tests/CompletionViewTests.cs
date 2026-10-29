using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Editor.Features;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.View;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>MUST 9 (completion): request lifecycle, filtering, accept paths, snippets, and misbehaving providers — in a real control.</summary>
public class CompletionViewTests
{
    private static void Type(CodeEditorControl v, string text)
    {
        foreach (var ch in text) v.TextArea.PerformTextInput(ch.ToString());
    }

    private static void Key(Avalonia.Controls.Window w, Key key) => w.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);

    private static IDisposable Register(ViewHost host, ICompletionProvider provider, string language = "plaintext") =>
        host.Factory.Services.Features.Register<ICompletionProvider>(DocumentSelector.ForLanguage(language), provider);

    private static CompletionFeature Feature(CodeEditorControl v) => v.Feature<CompletionFeature>()!;

    [AvaloniaFact]
    public async Task Typing_a_word_opens_the_popup_filters_while_typing_and_enter_accepts()
    {
        using var host = ViewHost.Create();
        Register(host, FakeCompletionProvider.Items("Console", "ConsoleKey", "Convert", "Count", "Collect"));
        var (_, view, window) = await host.OpenAsync("", "a.txt");
        var feature = Feature(view);
        Type(view, "Co");
        await ViewHost.WaitFor(() => feature.IsOpen, "popup opens");
        Assert.Equal(5, feature.VisibleLabels.Count);
        Type(view, "ns");
        await ViewHost.WaitFor(() => feature.VisibleLabels.Count == 2, "filter narrows to two");
        Assert.Equal(["Console", "ConsoleKey"], feature.VisibleLabels.ToArray());
        Key(window, Avalonia.Input.Key.Enter);
        ViewHost.Pump();
        Assert.Equal("Console", view.Document.GetText());
        Assert.False(feature.IsOpen);
        Assert.Equal(7, view.Editor.CaretOffset);
    }

    [AvaloniaFact]
    public async Task Arrow_keys_move_the_selection_tab_accepts_and_escape_closes()
    {
        using var host = ViewHost.Create();
        Register(host, FakeCompletionProvider.Items("alpha", "alpine", "alto"));
        var (_, view, window) = await host.OpenAsync("", "a.txt");
        var feature = Feature(view);
        Type(view, "al");
        await ViewHost.WaitFor(() => feature.IsOpen, "popup");
        Key(window, Avalonia.Input.Key.Down);
        ViewHost.Pump();
        Assert.Equal(1, feature.SelectedIndex);
        Key(window, Avalonia.Input.Key.Tab);
        ViewHost.Pump();
        Assert.Equal(feature.VisibleLabels.Count == 0 ? "alpine" : "alpine", view.Document.GetText());

        Type(view, " al");
        await ViewHost.WaitFor(() => feature.IsOpen, "popup again");
        Key(window, Avalonia.Input.Key.Escape);
        ViewHost.Pump();
        Assert.False(feature.IsOpen);
        Assert.Equal("alpine al", view.Document.GetText());
    }

    [AvaloniaFact]
    public async Task A_non_word_character_closes_the_popup_and_is_inserted_normally()
    {
        using var host = ViewHost.Create();
        Register(host, FakeCompletionProvider.Items("alpha"));
        var (_, view, _) = await host.OpenAsync("", "a.txt");
        Type(view, "al");
        try
        {
            await ViewHost.WaitFor(() => Feature(view).IsOpen, "popup", 3000);
        }
        catch (TimeoutException)
        {
            Assert.Fail("popup never opened: " + Feature(view).DebugState + " text='" + view.Document.GetText() + "'");
        }
        Type(view, " ");
        ViewHost.Pump();
        Assert.False(Feature(view).IsOpen);
        Assert.Equal("al ", view.Document.GetText());
    }

    [AvaloniaFact]
    public async Task A_commit_character_accepts_the_selected_item_and_stays_after_it()
    {
        using var host = ViewHost.Create();
        var items = new[] { new CompletionItem { Label = "Console", CommitCharacters = ["."] } };
        Register(host, FakeCompletionProvider.Of(items));
        var (_, view, _) = await host.OpenAsync("", "a.txt");
        Type(view, "Con");
        await ViewHost.WaitFor(() => Feature(view).IsOpen, "popup");
        Type(view, ".");
        ViewHost.Pump();
        Assert.Equal("Console.", view.Document.GetText());
        Assert.False(Feature(view).IsOpen);
    }

    [AvaloniaFact]
    public async Task TextEdit_and_additional_edits_apply_as_one_undo_unit()
    {
        using var host = ViewHost.Create();
        var item = new CompletionItem
        {
            Label = "Console",
            TextEdit = new TextEdit(TextRange.FromLines(1, 0, 1, 2), "Console"),
            AdditionalTextEdits = [TextEdit.Insert(TextPosition.Zero, "using System;\n")],
        };
        Register(host, FakeCompletionProvider.Of([item]));
        var (_, view, window) = await host.OpenAsync("\nCo", "a.txt");
        view.Controller.SetSelection(TextRange.At(1, 2));
        Feature(view).Trigger();
        await ViewHost.WaitFor(() => Feature(view).IsOpen, "popup");
        Key(window, Avalonia.Input.Key.Enter);
        ViewHost.Pump();
        Assert.Equal("using System;\n\nConsole", view.Document.GetText());
        view.Controller.Undo();
        Assert.Equal("\nCo", view.Document.GetText()); // ONE Ctrl+Z reverts both edits
    }

    [AvaloniaFact]
    public async Task A_snippet_item_starts_a_tab_stop_session_with_mirrors()
    {
        using var host = ViewHost.Create();
        var item = new CompletionItem { Label = "forloop", InsertText = "for (${1:i} = 0; $1 < ${2:n}; $1++)", InsertTextFormat = InsertTextFormat.Snippet };
        Register(host, FakeCompletionProvider.Of([item]));
        var (_, view, window) = await host.OpenAsync("", "a.txt");
        Type(view, "for");
        await ViewHost.WaitFor(() => Feature(view).IsOpen, "popup");
        Key(window, Avalonia.Input.Key.Enter);
        ViewHost.Pump();
        await ViewHost.WaitFor(() => view.Document.GetText() == "for (i = 0; i < n; i++)", "snippet inserted");
        var snippets = view.Feature<SnippetFeature>()!;
        Assert.True(snippets.IsActive);
        Assert.Equal(TextRange.FromLines(0, 5, 0, 6), view.Controller.Selections[0]); // first placeholder selected
        Type(view, "idx");
        ViewHost.Pump();
        Assert.Equal("for (idx = 0; idx < n; idx++)", view.Document.GetText());     // mirrors follow live
        Key(window, Avalonia.Input.Key.Tab);
        ViewHost.Pump();
        Assert.Equal("n", view.TextArea.Selection.GetText());
        Key(window, Avalonia.Input.Key.Tab);                                          // the implicit final stop ends the session
        ViewHost.Pump();
        Assert.False(snippets.IsActive);
        Assert.Equal(view.Document.Length, view.Editor.CaretOffset);
    }

    [AvaloniaFact]
    public async Task Escape_ends_a_snippet_session_and_leaves_the_text()
    {
        using var host = ViewHost.Create();
        var (_, view, window) = await host.OpenAsync("", "a.txt");
        var snippets = view.Feature<SnippetFeature>()!;
        await snippets.InsertAsync("a ${1:b} ${2:c}", null);
        Assert.True(snippets.IsActive);
        Key(window, Avalonia.Input.Key.Escape);
        ViewHost.Pump();
        Assert.False(snippets.IsActive);
        Assert.Equal("a b c", view.Document.GetText());
    }

    [AvaloniaFact]
    public async Task Moving_the_caret_out_of_a_snippet_ends_the_session()
    {
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync("tail", "a.txt");
        var snippets = view.Feature<SnippetFeature>()!;
        view.Controller.SetSelection(TextRange.At(0, 0));
        await snippets.InsertAsync("[${1:x}]", null);
        Assert.True(snippets.IsActive);
        view.Controller.SetSelection(TextRange.At(0, view.Document.Length));
        ViewHost.Pump();
        Assert.False(snippets.IsActive);
    }

    [AvaloniaFact]
    public async Task Snippet_insertion_is_one_undo_unit()
    {
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync("", "a.txt");
        await view.Feature<SnippetFeature>()!.InsertAsync("if ($1) {\n\t$0\n}", null);
        Assert.Equal("if () {\n    \n}", view.Document.GetText());
        view.Controller.Undo();
        Assert.Equal("", view.Document.GetText());
    }

    // ---- misbehaving providers ----

    [AvaloniaFact]
    public async Task A_provider_that_never_answers_costs_a_loading_line_and_nothing_else()
    {
        using var host = ViewHost.Create();
        var sawCancel = new TaskCompletionSource();
        var hung = new FakeCompletionProvider(async (_, _, _, ct) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                sawCancel.TrySetResult();
            }
            return null;
        });
        Register(host, hung);
        var (_, view, window) = await host.OpenAsync("", "a.txt");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Type(view, "Hello");                                    // typing is not affected: these run while the request is in flight
        Assert.True(sw.ElapsedMilliseconds < 500);
        Assert.Equal("Hello", view.Document.GetText());
        await ViewHost.WaitFor(() => Feature(view).IsOpen, "the loading affordance appears");
        Assert.Empty(Feature(view).VisibleLabels);               // ...showing no items
        Key(window, Avalonia.Input.Key.Escape);
        ViewHost.Pump();
        Assert.False(Feature(view).IsOpen);
        await sawCancel.Task.WaitAsync(TimeSpan.FromSeconds(5)); // closing cancels the provider's token
    }

    [AvaloniaFact]
    public async Task Fast_results_show_first_and_a_slow_provider_is_merged_when_it_answers()
    {
        using var host = ViewHost.Create();
        Register(host, FakeCompletionProvider.Items("fast1", "fast2"));
        Register(host, new FakeCompletionProvider(async (_, _, _, ct) =>
        {
            await Task.Delay(700, ct);
            return new CompletionList([new CompletionItem { Label = "fast3-slow" }]);
        }));
        var (_, view, _) = await host.OpenAsync("", "a.txt");
        Type(view, "fa");
        await ViewHost.WaitFor(() => Feature(view).VisibleLabels.Count == 2, "fast results");
        await ViewHost.WaitFor(() => Feature(view).VisibleLabels.Count == 3, "slow results merged");
        Assert.Contains("fast3-slow", Feature(view).VisibleLabels);
    }

    [AvaloniaFact]
    public async Task A_throwing_provider_does_not_hide_the_others_results()
    {
        using var host = ViewHost.Create();
        Register(host, new FakeCompletionProvider((_, _, _, _) => throw new InvalidOperationException("boom")));
        Register(host, FakeCompletionProvider.Items("good"));
        var (_, view, _) = await host.OpenAsync("", "a.txt");
        Type(view, "go");
        await ViewHost.WaitFor(() => Feature(view).VisibleLabels.Contains("good"), "good result shown");
    }

    [AvaloniaFact]
    public async Task A_result_that_arrives_after_the_word_was_abandoned_is_never_shown()
    {
        using var host = ViewHost.Create();
        Register(host, new FakeCompletionProvider(async (_, _, _, ct) =>
        {
            await Task.Delay(500, ct);
            return new CompletionList([new CompletionItem { Label = "late" }]);
        }));
        var (_, view, _) = await host.OpenAsync("", "a.txt");
        Type(view, "la");
        Type(view, " ");                                         // the user moved on before the answer arrived
        await Task.Delay(900);
        ViewHost.Pump();
        Assert.False(Feature(view).IsOpen);
        Assert.Empty(Feature(view).VisibleLabels);
    }

    [AvaloniaFact]
    public async Task Typing_more_while_a_slow_provider_works_filters_its_late_answer_against_the_current_word()
    {
        using var host = ViewHost.Create();
        Register(host, new FakeCompletionProvider(async (_, _, _, ct) =>
        {
            await Task.Delay(300, ct);
            return new CompletionList([new CompletionItem { Label = "alpha" }, new CompletionItem { Label = "beta" }]);
        }));
        var (_, view, _) = await host.OpenAsync("", "a.txt");
        Type(view, "a");
        Type(view, "lp");                                        // still the same word when the answer arrives
        await ViewHost.WaitFor(() => Feature(view).VisibleLabels.Count > 0, "late answer shown");
        Assert.Equal(["alpha"], Feature(view).VisibleLabels.ToArray()); // "beta" does not match the CURRENT prefix "alp"
    }

    [AvaloniaFact]
    public async Task Incomplete_results_are_requested_again_for_a_longer_prefix()
    {
        using var host = ViewHost.Create();
        var provider = FakeCompletionProvider.Of([new CompletionItem { Label = "alpha" }], incomplete: true);
        Register(host, provider);
        var (_, view, _) = await host.OpenAsync("", "a.txt");
        Type(view, "a");
        await ViewHost.WaitFor(() => Feature(view).IsOpen, "popup");
        Type(view, "l");
        await ViewHost.WaitFor(() => provider.Calls >= 2, "second request");
        Assert.Contains(provider.Contexts, c => c.TriggerKind == CompletionTriggerKind.TriggerForIncompleteCompletions);
    }

    [AvaloniaFact]
    public async Task Trigger_characters_and_the_trigger_suggest_command_work_without_typing_a_word()
    {
        using var host = ViewHost.Create();
        var provider = FakeCompletionProvider.Of([new CompletionItem { Label = "Write" }, new CompletionItem { Label = "Read" }], triggers: ["."]);
        Register(host, provider);
        var (_, view, _) = await host.OpenAsync("", "a.txt");
        host.Factory.NotifyFocused(view);
        Type(view, "Console.");
        await ViewHost.WaitFor(() => Feature(view).IsOpen, "popup after '.'");
        Assert.Contains(provider.Contexts, c => c is { TriggerKind: CompletionTriggerKind.TriggerCharacter, TriggerCharacter: "." });
        Assert.Equal(2, Feature(view).VisibleLabels.Count);
        Feature(view).Close();
        await host.Commands.ExecuteAsync("editor.action.triggerSuggest");
        await ViewHost.WaitFor(() => Feature(view).IsOpen, "popup after Ctrl+Space");
        Assert.Contains(provider.Contexts, c => c.TriggerKind == CompletionTriggerKind.Invoked);
    }

    [AvaloniaFact]
    public async Task Resolving_the_selected_item_fills_in_documentation()
    {
        using var host = ViewHost.Create();
        var provider = FakeCompletionProvider.Items("Console");
        var resolved = new TaskCompletionSource();
        provider.Resolve = (item, _) =>
        {
            resolved.TrySetResult();
            return Task.FromResult(item with { Documentation = MarkupContent.Markdown("Writes to the **console**."), Detail = "class System.Console" });
        };
        Register(host, provider);
        var (_, view, _) = await host.OpenAsync("", "a.txt");
        Type(view, "Con");
        await ViewHost.WaitFor(() => Feature(view).IsOpen, "popup");
        await resolved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ViewHost.WaitFor(() => view.Overlay.Children.OfType<Avalonia.Controls.Border>().Any(b => b.IsVisible), "popup visible");
        ViewHost.Pump();
    }

    [AvaloniaFact]
    public async Task Quick_suggestions_do_not_trigger_inside_comments_or_when_turned_off()
    {
        using var host = ViewHost.Create();
        var provider = FakeCompletionProvider.Items("alpha");
        host.Factory.Services.Features.Register<ICompletionProvider>(DocumentSelector.ForLanguage("csharp"), provider);
        var (_, view, _) = await host.OpenAsync("// ", "c.cs");
        await ViewHost.WaitFor(() => view.Tokens is { } t && t.Store.ValidLines >= 1, "tokens");
        view.Controller.SetSelection(TextRange.At(0, 3));
        Type(view, "al");
        await Task.Delay(300);
        ViewHost.Pump();
        Assert.Equal(0, provider.Calls); // comment: no quick suggestion
        host.Config.Set("editor.quickSuggestions", false);
        ViewHost.Pump();
        view.Controller.SetSelection(TextRange.At(0, 0));
        Type(view, "zz");
        await Task.Delay(300);
        Assert.Equal(0, provider.Calls);
    }

    [AvaloniaFact]
    public async Task Builtin_snippets_are_offered_alongside_language_server_items()
    {
        using var host = ViewHost.Create();
        var (_, view, window) = await host.OpenAsync("", "c.cs");
        Type(view, "#reg");
        await ViewHost.WaitFor(() => Feature(view).VisibleLabels.Contains("#region"), "snippet offered");
        Key(window, Avalonia.Input.Key.Enter);
        ViewHost.Pump();
        await ViewHost.WaitFor(() => view.Document.GetText().StartsWith("#region", StringComparison.Ordinal), "snippet inserted");
    }

    [AvaloniaFact]
    public async Task A_screenshot_with_the_completion_popup_open_is_written()
    {
        using var host = ViewHost.Create();
        var items = new[]
        {
            new CompletionItem { Label = "WriteLine", Kind = CompletionItemKind.Method, Detail = "void Console.WriteLine(string value)", Documentation = MarkupContent.Markdown("Writes the specified string value, followed by the current line terminator, to the **standard output** stream.\n\n```csharp\nConsole.WriteLine(\"hi\");\n```") },
            new CompletionItem { Label = "Write", Kind = CompletionItemKind.Method, Detail = "void Console.Write(string value)" },
            new CompletionItem { Label = "WindowWidth", Kind = CompletionItemKind.Property, Detail = "int" },
            new CompletionItem { Label = "WriteLineAsync", Kind = CompletionItemKind.Method },
            new CompletionItem { Label = "Wrap", Kind = CompletionItemKind.Keyword },
            new CompletionItem { Label = "Wire", Kind = CompletionItemKind.Class },
        };
        host.Factory.Services.Features.Register<ICompletionProvider>(DocumentSelector.ForLanguage("csharp"), FakeCompletionProvider.Of(items));
        var (_, view, window) = await host.OpenAsync("using System;\n\nclass P\n{\n    static void Main()\n    {\n        Console.W\n    }\n}\n", "p.cs");
        view.Controller.SetSelection(TextRange.At(6, 17));
        Feature(view).Trigger();
        await ViewHost.WaitFor(() => Feature(view).VisibleLabels.Count >= 4, "popup");
        await Task.Delay(400);
        ViewHost.Pump();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.scratch/screenshots"));
        Directory.CreateDirectory(dir);
        using var fs = File.Create(Path.Combine(dir, "editor-completion-popup.png"));
        frame!.Save(fs, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
