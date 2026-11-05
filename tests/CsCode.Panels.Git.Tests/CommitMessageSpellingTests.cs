using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Panels.Git.Views;
using Xunit;

namespace CsCode.Panels.Git.Tests;

/// <summary>
/// The commit message box's spelling (docs/SPELLCHECK.md): the app's spell checker through Core's <see cref="ITextSpellChecker"/> underlines
/// misspelt words in the box, and its right-click menu replaces one or adds it to the dictionary. The checker here is a fake with a word list.
/// </summary>
public sealed class CommitMessageSpellingTests
{
    /// <summary>Flags the words it is given (any case), suggests a fixed replacement, records additions.</summary>
    private sealed class FakeChecker(params string[] misspelt) : ITextSpellChecker
    {
        private readonly HashSet<string> _bad = new(misspelt, StringComparer.OrdinalIgnoreCase);
        private readonly Emitter<bool> _changed = new();
        public List<string> Added { get; } = [];
        public int Checks;
        public IEventSource<bool> Changed => _changed;

        public Task<IReadOnlyList<SpellingIssue>> CheckTextAsync(string text, string languageId, CancellationToken ct = default)
        {
            Assert.Equal("scminput", languageId);
            Interlocked.Increment(ref Checks);
            var issues = new List<SpellingIssue>();
            var i = 0;
            while (i < text.Length)
            {
                if (!char.IsLetter(text[i])) { i++; continue; }
                var start = i;
                while (i < text.Length && char.IsLetter(text[i])) i++;
                var word = text[start..i];
                if (_bad.Contains(word)) issues.Add(new SpellingIssue(start, word.Length, word, $"\"{word}\": Unknown word."));
            }
            return Task.Run<IReadOnlyList<SpellingIssue>>(() => issues, ct); // answers off the UI thread, as the real lane does
        }

        public Task<IReadOnlyList<string>> SuggestAsync(string word, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(word.Equals("wierd", StringComparison.OrdinalIgnoreCase) ? ["weird", "wired"] : []);

        public Task AddToUserDictionaryAsync(string word, CancellationToken ct = default)
        {
            Added.Add(word);
            _bad.Remove(word);
            _changed.Fire(true);
            return Task.CompletedTask;
        }
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task WaitFor(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Timed out waiting for: " + what);
            Pump();
            await Task.Delay(15);
        }
    }

    private static (TextBox Box, CommitMessageSpelling Spelling, Window Window) Show(FakeChecker checker)
    {
        var box = new TextBox { AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Width = 300, MinHeight = 56 };
        var spelling = new CommitMessageSpelling(box, checker);
        var window = new Window { Width = 400, Height = 200, Content = new Panel { Children = { box, spelling } } };
        window.Show();
        Pump();
        return (box, spelling, window);
    }

    [AvaloniaFact]
    public async Task Misspelt_words_are_underlined_and_the_underline_goes_while_the_text_changes()
    {
        var checker = new FakeChecker("wierd", "definately");
        var (box, spelling, window) = Show(checker);
        box.Text = "Fix the wierd bug\n\nIt is definately fixed now.";
        await WaitFor(() => spelling.Issues.Count == 2, "two issues");
        Assert.Equal([(8, 5), (25, 10)], spelling.Issues.Select(i => (i.Start, i.Length)));
        var rects = spelling.Underlines();
        Assert.Equal(2, rects.Count);
        Assert.True(rects[0].X > 0 && rects[1].Y > rects[0].Y, $"{rects[0]} then {rects[1]}"); // the second word is on the third line

        box.Text += " More";
        Assert.Empty(spelling.Underlines()); // never drawn at old offsets
        await WaitFor(() => spelling.Issues.Count == 2 && spelling.Underlines().Count == 2, "checked again");
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_menu_replaces_the_word_or_adds_it_to_the_dictionary()
    {
        var checker = new FakeChecker("wierd", "zorblax");
        var (box, spelling, window) = Show(checker);
        box.Text = "A wierd zorblax";
        await WaitFor(() => spelling.Issues.Count == 2, "issues");

        var menu = await spelling.MenuForAsync(spelling.Issues[0]);
        Assert.NotNull(menu);
        var titles = menu!.Select(m => m.Header).ToList();
        Assert.Equal("weird", titles[0]);
        Assert.Contains("Add \"wierd\" to User Dictionary", titles);
        Assert.Contains("_Copy", titles);
        await menu!.First(m => m.Header == "weird").Run!();
        Assert.Equal("A weird zorblax", box.Text);

        await WaitFor(() => spelling.Issues.Count == 1, "checked again");
        var add = (await spelling.MenuForAsync(spelling.Issues[0]))!.First(m => m.Header == "Add \"zorblax\" to User Dictionary");
        await add.Run!();
        Assert.Equal(["zorblax"], checker.Added);
        await WaitFor(() => spelling.Issues.Count == 0, "the added word is no longer underlined");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Nothing_is_checked_while_the_box_is_not_in_a_window_and_detaching_unhooks_it()
    {
        var checker = new FakeChecker("wierd");
        var box = new TextBox();
        var spelling = new CommitMessageSpelling(box, checker);
        box.Text = "wierd";
        Pump();
        await Task.Delay(400);
        Pump();
        Assert.Equal(0, checker.Checks);
        spelling.Detach();
        var window = new Window { Content = new Panel { Children = { box, spelling } } };
        window.Show();
        box.Text = "wierd again";
        await Task.Delay(400);
        Pump();
        Assert.DoesNotContain(spelling.Issues, i => i.Start > 5);
        window.Close();
    }
}
