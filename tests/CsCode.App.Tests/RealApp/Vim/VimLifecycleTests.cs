using System.Runtime.CompilerServices;
using Avalonia.Headless.XUnit;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Text;
using CsCode.Vim;
using Xunit;

namespace CsCode.App.Tests.RealApp.Vim;

/// <summary>One engine per editor view, created and destroyed with it; one buffer per open document; nothing retained after closing.</summary>
public class VimLifecycleTests
{
    private const string Quiet = "\"editor.quickSuggestions\": false";

    [AvaloniaFact(Timeout = 600_000)]
    public async Task Three_hundred_editors_opened_edited_and_closed_leave_no_engine_host_buffer_view_or_document_behind()
    {
        await using var app = await VimApp.StartAsync("anchor\n", settings: Quiet);
        var anchor = app.Tab;
        var samples = new List<WeakReference>();
        for (var i = 0; i < 300; i++)
        {
            var sampled = await OpenEditCloseAsync(app, i);
            if (i % 30 == 0) samples.AddRange(sampled);
            Assert.True(app.Vim.EngineCount <= 2, $"{app.Vim.EngineCount} engines alive after closing editor {i}");
        }
        app.Well.Activate(anchor);
        await app.FocusAsync();                                           // focus leaves the last closed view (AvaloniaEdit keeps the last focused one)
        Assert.Equal(1, app.Vim.EngineCount);
        Assert.Equal(1, app.Vim.BufferCount);
        for (var round = 0; round < 10 && samples.Any(w => w.IsAlive); round++)
        {
            await DevScene.SettleAsync(5);
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        var alive = samples.Where(w => w.IsAlive).Select(w => w.Target?.GetType().Name).ToArray();
        Assert.True(alive.Length == 0, $"{alive.Length} of {samples.Count} sampled objects still reachable: {string.Join(", ", alive)}");
    }

    /// <summary>Opens a file, edits it through Vim (undo groups with markers end up in its history), closes it; returns weak references (own frame).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference[]> OpenEditCloseAsync(VimApp app, int i)
    {
        var tab = await app.OpenAsync($"f{i}.txt", $"line {i}\nsecond\n", settleRounds: 0);
        var vimView = app.Vim.ViewOf(app.View)!;
        app.Keys("xjAok<Esc>u");
        Assert.Equal($"ine {i}\nsecond\n", app.Text);
        var refs = new WeakReference[]
        {
            new(vimView.Engine), new(vimView.Host), new(vimView.Buffer), new(vimView), new(app.View), new(app.View.Document), new(tab),
        };
        await app.Well.CloseAsync(tab, prompt: false);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return refs;
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task An_evicted_view_loses_its_engine_but_the_document_keeps_its_vim_history()
    {
        await using var app = await VimApp.StartAsync("first\n", settings: Quiet + ",\n\"cscode.shell.liveEditorViews\": 0");
        var first = app.Tab;
        app.Keys("xx");
        Assert.Equal("rst\n", app.Text);
        var engine = new WeakReference(app.Engine);
        await app.OpenAsync("b.txt", "b\n");
        await app.OpenAsync("c.txt", "c\n");
        Assert.True(await RealAppHost.WaitFor(() => first.LiveView is null), "the first tab's view was not evicted");
        Assert.True(app.Vim.EngineCount <= 2);
        app.Well.Activate(first);
        await app.FocusAsync();
        Assert.False(ReferenceEquals(engine.Target, app.Engine));         // a new view, a new engine...
        app.Keys("u");                                                    // ...over the same buffer: the undo history survived
        Assert.Equal("irst\n", app.Text);
        app.Keys("u");
        Assert.Equal("first\n", app.Text);
        Assert.False(first.Doc.IsDirty);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Registers_persist_across_restarts_and_a_vimrc_is_read_only_when_vim_vimrc_path_names_it()
    {
        using var saved = new CsCode.TestDoubles.TempDirectory("cscode-vim-state-");
        {
            await using var app = await VimApp.StartAsync("persist me\n", settings: Quiet);
            app.Keys("\"ayy");
            await app.Host.Get<CsCode.Core.Platform.IStateStorage>().FlushAsync();
            CopyTree(app.Host.Home.Path, saved.Path); // the next start sees this run's state (CSCODE_HOME is a temp directory per run)
            Assert.Empty(app.Vim.Service.VimrcErrors);
        }
        await using var second = await VimApp.StartAsync("x\n", settings: Quiet + ",\n\"vim.vimrc.path\": \"VIMRC_PATH\"", seed: h =>
        {
            foreach (var file in Directory.EnumerateFiles(saved.Path, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith("settings.json", StringComparison.Ordinal)))
            {
                var target = Path.Combine(h.Home.Path, Path.GetRelativePath(saved.Path, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
            var vimrc = h.Files.Combine("myvimrc");
            File.WriteAllText(vimrc, "nnoremap Q dd\n");
            var settings = h.Home.Combine("config/settings.json");
            File.WriteAllText(settings, File.ReadAllText(settings).Replace("VIMRC_PATH", vimrc.Replace("\\", "\\\\")));
        });
        await second.Vim.Service.VimrcLoaded;
        Assert.Empty(second.Vim.Service.VimrcErrors);
        second.Keys("\"ap");                                                // the register came back with the restart
        Assert.Equal("x\npersist me\n", second.Text);
        second.Keys("Q");                                                   // the named vimrc was read
        Assert.Equal("x\n", second.Text);
    }

    private static void CopyTree(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
