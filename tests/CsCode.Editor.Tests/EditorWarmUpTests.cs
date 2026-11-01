using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.View;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>
/// The throwaway editor the warm spare renders before it is claimed (<see cref="EditorWarmUp"/>, docs/SESSIONS.md "The warm spare"): a real view,
/// coloured, over a document nobody else ever sees, and nothing of it left after Dispose.
/// </summary>
public class EditorWarmUpTests
{
    private const string Sample = "namespace Demo;\n\npublic sealed class Widget\n{\n    public int Size { get; set; } // 42\n}\n";

    [AvaloniaFact]
    public async Task A_warm_up_editor_is_a_real_coloured_view_over_a_document_no_one_else_sees_and_leaves_nothing_behind()
    {
        using var host = ViewHost.Create();
        var opened = new List<Core.Text.ITextDocument>();
        using var sub = host.Documents.Opened.Subscribe(e => opened.Add(e.Document));

        var warm = EditorWarmUp.Create(host.Factory, "csharp", Sample, "Sample.cs");
        Assert.NotNull(warm);
        var view = Assert.IsType<CodeEditorControl>(warm.View);
        Assert.Equal(EditorWarmUp.Scheme, view.Document.Uri.Scheme);
        Assert.True(view.Document.IsReadOnly);
        Assert.Equal("csharp", view.Document.LanguageId);
        Assert.Contains(view, host.Factory.Views);
        Assert.True(warm.HasGrammar);

        var window = new Window { Width = 800, Height = 400, Content = view };
        window.Show();
        await ViewHost.WaitFor(() => warm.TokensReady, "the sample's colours");
        Assert.Equal(1, host.Factory.Tokens.SessionCount);
        // Never registered: no language server, file watcher, tab or session hears of it.
        Assert.Empty(opened);
        Assert.Empty(host.Documents.Documents);

        window.Content = null;
        warm.Dispose();
        window.Close();
        Assert.True(view.IsDisposed);
        Assert.DoesNotContain(view, host.Factory.Views);
        Assert.Equal(0, host.Factory.Tokens.SessionCount);
        Assert.Empty(opened);
        warm.Dispose(); // twice is harmless
    }

    [AvaloniaFact]
    public void A_language_without_a_grammar_has_no_colours_to_wait_for()
    {
        using var host = ViewHost.Create();
        using var warm = EditorWarmUp.Create(host.Factory, "plaintext", "just text\n");
        Assert.NotNull(warm);
        Assert.False(warm.HasGrammar);
        Assert.True(warm.TokensReady);
        Assert.Equal(0, host.Factory.Tokens.SessionCount);
    }

    [AvaloniaFact]
    public void A_disposed_factory_makes_no_warm_up_editor()
    {
        var host = ViewHost.Create();
        var factory = host.Factory;
        host.Dispose();
        Assert.Null(EditorWarmUp.Create(factory, "csharp", Sample));
    }
}
