using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using CsCode.Core.Platform;
using CsCode.Panels.Search.Engine;
using CsCode.Panels.Search.Settings;
using CsCode.Panels.Search.ViewModels;
using CsCode.Panels.Search.Views;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Panels.Search.Tests.Hostile;

/// <summary>
/// Leak and thread-count tests run alone: <see cref="LeakTests.CountThreads"/> sees every engine thread in the process, so a search started
/// by a test class running in parallel would show up in the count.
/// </summary>
[CollectionDefinition(nameof(SerialLeakTests), DisableParallelization = true)]
public sealed class SerialLeakTests;

/// <summary>Every lifecycle-bearing type is proven released after dispose/close with <see cref="LeakAssert"/> (ARCHITECTURE §8).</summary>
[Collection(nameof(SerialLeakTests))]
public class LeakTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Running_and_cancelling_100_searches_then_closing_the_panel_retains_nothing()
    {
        using var f = new VmFixture();
        for (var i = 0; i < 30; i++) f.Harness.Write($"f{i}.txt", "needle one\nneedle two\nother");

        await LeakAssert.NotRetainedAsync(async () =>
        {
            var vm = f.NewFind();
            var objects = new List<object> { vm, vm.Rows, vm.History, vm.Previews };
            for (var i = 0; i < 100; i++)
            {
                vm.Query = i % 2 == 0 ? "needle" : "other";
                vm.Start();
                if (i % 3 == 0) vm.Cancel();
                else { await f.Ui.PumpAsync(vm.WaitForSearchAsync()); }
                f.Ui.RunPending();
                if (vm.LastCompletion is not null) objects.Add(vm.LastCompletion);
            }
            objects.AddRange(vm.Rows.Take(5));
            vm.Dispose();
            f.Ui.RunPending();
            await Task.Delay(50, Ct);
            f.Ui.RunPending();
            return objects.ToArray();
        });
        Assert.Equal(0, f.Harness.Engine.RunningWorkers);
    }

    [Fact]
    public async Task A_finished_session_is_not_kept_alive_by_the_engine()
    {
        using var f = new VmFixture();
        f.Harness.Write("a.txt", "needle");
        await LeakAssert.NotRetainedAsync(async () =>
        {
            var session = f.Harness.Engine.Start(new SearchQuery("needle"));
            var sub = session.Batches.Subscribe(_ => { });
            await session.Result.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            await session.WorkersStopped.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            sub.Dispose();
            return [session];
        });
    }

    [Fact]
    public async Task Cancelled_sessions_are_not_retained_either()
    {
        using var f = new VmFixture();
        for (var i = 0; i < 100; i++) f.Harness.Write($"f{i}.txt", string.Join("\n", Enumerable.Repeat("needle", 100)));
        await LeakAssert.NotRetainedAsync(async () =>
        {
            var list = new List<object>();
            for (var i = 0; i < 20; i++)
            {
                var s = f.Harness.Engine.Start(new SearchQuery("needle"));
                s.Cancel();
                await s.WorkersStopped.WaitAsync(TimeSpan.FromSeconds(10), Ct);
                list.Add(s);
            }
            return list.ToArray();
        });
    }

    [Fact]
    public async Task The_settings_view_model_unsubscribes_from_the_configuration_on_dispose()
    {
        using var f = new VmFixture();
        f.ConfigStore.RegisterProperties([new ConfigurationProperty("editor.tabSize", JsonSerializer.SerializeToElement(4), "Tab size", JsonDocument.Parse("""{"type":"integer"}""").RootElement.Clone())]);
        await LeakAssert.NotRetainedAsync(async () =>
        {
            var vm = new SettingsViewModel(f.Services, debounceMs: 10);
            var items = vm.AllItems.ToArray();
            f.ConfigStore.Set("editor.tabSize", 3);
            f.Ui.RunPending();
            await vm.RefreshAsync();
            vm.Dispose();
            f.Ui.RunPending();
            return [vm, .. items];
        });
    }

    [Fact]
    public async Task The_keyboard_shortcuts_view_model_unsubscribes_on_dispose()
    {
        using var f = new VmFixture();
        f.Keybindings.Bindings.Add(new KeyBinding("a.b", KeyStrokeParser.ParseChord("ctrl+a"), null, null, KeyBindingSource.Default));
        await LeakAssert.NotRetainedAsync(() =>
        {
            var vm = new KeyboardShortcutsViewModel(f.Services);
            vm.Filter = "a";
            vm.Dispose();
            return Task.FromResult<object[]>([vm]);
        });
        Assert.Equal(0, f.Ui.Pending);
    }

    [Fact]
    public async Task The_host_does_not_retain_released_view_models()
    {
        using var f = new VmFixture();
        var services = new ServiceCollection();
        services.AddSingleton<IWorkspaceService>(f.Harness.Workspace);
        services.AddSingleton(f.Configuration);
        services.AddSingleton<IWindowService>(f.Windows);
        services.AddSingleton<ICommandService>(f.Commands);
        services.AddSingleton<CsCode.Ui.Threading.IUiDispatcher>(f.Ui);
        services.AddCsCodeSearchPanels();
        using var sp = services.BuildServiceProvider();
        var host = sp.GetRequiredService<SearchPanelsHost>();
        await LeakAssert.NotRetainedAsync(() =>
        {
            var find = host.CreateFindViewModel();
            var settings = host.CreateSettingsViewModel();
            host.ReleaseFindViewModel(find);
            host.ReleaseSettingsViewModel(settings);
            return Task.FromResult<object[]>([find, settings]);
        });
        Assert.Null(host.LiveFind);
        Assert.Null(host.LiveSettings);
    }

    [AvaloniaFact]
    public async Task Opening_and_closing_the_find_view_100_times_retains_no_view_or_view_model()
    {
        UiSupport.EnsureTheme();
        using var f = new UiFixture();
        f.WriteSampleSources();
        await LeakAssert.NotRetainedAsync(async () =>
        {
            var kept = new List<object>();
            for (var i = 0; i < 100; i++)
            {
                var vm = new FindInFilesViewModel(f.Services);
                var view = new FindInFilesView(vm);
                var window = UiSupport.Host(view, 400, 300);
                if (i % 10 == 0)
                {
                    vm.Query = "UserService";
                    vm.Start();
                    await UiSupport.WaitUntilAsync(() => vm.WaitForSearchAsync().IsCompleted);
                }
                window.Close();
                view.Dispose();
                if (i % 10 == 0) kept.Add(view);
                if (i % 10 == 0) kept.Add(vm);
            }
            UiSupport.Pump();
            return kept.ToArray();
        });
        Assert.Equal(0, f.Harness.Engine.RunningWorkers);
    }

    [AvaloniaFact]
    public async Task Opening_and_closing_the_settings_view_retains_nothing()
    {
        UiSupport.EnsureTheme();
        using var f = new UiFixture();
        f.Harness.Config.RegisterProperties([new ConfigurationProperty("editor.tabSize", JsonSerializer.SerializeToElement(4), "Tab size", JsonDocument.Parse("""{"type":"integer"}""").RootElement.Clone())]);
        await LeakAssert.NotRetainedAsync(() =>
        {
            var kept = new List<object>();
            for (var i = 0; i < 20; i++)
            {
                var vm = new SettingsViewModel(f.Services, debounceMs: 5);
                var view = new SettingsView(vm);
                var window = UiSupport.Host(view, 600, 400);
                window.Close();
                view.Dispose();
                kept.Add(view);
                kept.Add(vm);
            }
            UiSupport.Pump();
            return Task.FromResult(kept.ToArray());
        });
    }

    /// <summary>Number of live OS threads whose name starts with <paramref name="prefix"/> (Linux: /proc/self/task/*/comm); -1 elsewhere.</summary>
    internal static int CountThreads(string prefix)
    {
        if (!OperatingSystem.IsLinux()) return -1;
        var n = 0;
        foreach (var dir in Directory.EnumerateDirectories("/proc/self/task"))
        {
            try { if (File.ReadAllText(Path.Combine(dir, "comm")).StartsWith(prefix, StringComparison.Ordinal)) n++; }
            catch (IOException) { /* the thread exited while we looked */ }
        }
        return n;
    }

    [Fact]
    public void Engine_threads_exit_with_the_engine()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Skip("counts threads by name through /proc"); return; }
        var before = CountThreads("cscode-search");
        using (var f = new SearchHarness())
        {
            f.Write("a.txt", "needle");
            for (var i = 0; i < 5; i++) f.Engine.Start(new SearchQuery("needle")).Cancel();
            Thread.Sleep(50);
        }
        for (var i = 0; i < 100 && CountThreads("cscode-search") > before; i++) Thread.Sleep(20);
        Assert.Equal(before, CountThreads("cscode-search"));
    }

    [Fact]
    public async Task The_engine_names_its_threads_so_they_can_be_told_from_the_pool()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Skip("counts threads by name through /proc"); return; }
        using var f = new SearchHarness(new SearchLimits { Parallelism = 4 });
        for (var i = 0; i < 400; i++) f.Write($"f{i}.txt", string.Join("\n", Enumerable.Repeat("filler line", 2000)));
        using var session = f.Engine.Start(new SearchQuery("will not match"));
        var seen = 0;
        for (var i = 0; i < 200 && !session.IsCompleted; i++) { seen = Math.Max(seen, CountThreads("cscode-search-")); await Task.Delay(2, Ct); }
        await session.Result.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        Assert.True(seen >= 2, $"expected the coordinator and workers to be visible by name, saw {seen}");
    }
}
