using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using AvaloniaEdit.Search;
using CsCode.Core.Platform;
using CsCode.Editor.Tests.Support;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>
/// T20: Find (<c>actions.find</c>, Ctrl+F) puts the keyboard in the find box at once, also the first time the panel opens and on the headless
/// platform (AvaloniaEdit focuses it only when its template is already applied). Typing then goes to the box, not the document.
/// </summary>
public class FindBoxFocusTests
{
    private static TextBox? FindBox(Avalonia.Visual view) =>
        view.GetVisualDescendants().OfType<SearchPanel>().SelectMany(p => p.GetVisualDescendants().OfType<TextBox>()).FirstOrDefault();

    [AvaloniaFact]
    public async Task Find_focuses_the_find_box_the_first_time_and_again_after_the_editor_had_focus()
    {
        using var host = ViewHost.Create();
        var (doc, view, window) = await host.OpenAsync("alpha beta\nalpha\n");
        view.Controller.Focus();
        await host.Commands.ExecuteAsync(KnownCommands.Find);
        // Synchronously: no layout pass or dispatcher turn needed.
        var box = FindBox(view);
        Assert.NotNull(box);
        Assert.True(box!.IsKeyboardFocusWithin, "the find box does not have the keyboard after Find");

        window.KeyTextInput("beta");
        ViewHost.Pump();
        Assert.Equal("beta", box.Text);
        Assert.Equal("alpha beta\nalpha\n", doc.GetText()); // nothing typed into the document

        view.Controller.Focus();
        Assert.False(box.IsKeyboardFocusWithin);
        await host.Commands.ExecuteAsync(KnownCommands.Replace);
        Assert.True(FindBox(view)!.IsKeyboardFocusWithin, "Replace (panel already open) does not focus the find box");
    }
}
