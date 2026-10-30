using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CsCode.App.Startup;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Platform;
using CsCode.Remote;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>
/// T6: the container of the REAL composition (every module, <c>CSCODE_HOME</c> in a temp directory), with every registered service resolved
/// (keyed ones included) in both language-host modes, is disposed both ways without throwing. Microsoft's synchronous
/// <see cref="ServiceProvider.Dispose"/> throws on the first singleton that implements only <see cref="IAsyncDisposable"/> and then skips every
/// singleton created before it; the app disposes its container synchronously on the UI thread (<see cref="AppShutdown"/> step 5), so every
/// disposable singleton must also be <see cref="IDisposable"/>, and the ones with real asynchronous work are disposed in the graceful step first.
/// Resolving everything also creates the process language host, which starts a cscode-server child: each test checks that none is left.
/// </summary>
public class ContainerDisposalTests
{
    public static TheoryData<string> LanguageHostModes => new() { "inProcess", "process" };

    /// <summary>Builds the real composition and resolves every registration (off the UI thread, as <see cref="CompositionTests"/> does).</summary>
    private static async Task<(ServiceProvider Provider, IReadOnlyList<object> Instances)> ResolveEverythingAsync(RealAppHome env, string mode, string marker,
        Action<IServiceCollection>? configure = null)
    {
        IServiceCollection? registered = null;
        var sp = AppServices.Build([], s =>
        {
            env.Configure(s);
            s.AddSingleton(new RemoteOptions { LanguageHostMode = mode, ServerMarker = marker });
            configure?.Invoke(s);
            registered = s;
        });
        var descriptors = registered!.Where(d => !d.ServiceType.IsGenericTypeDefinition).ToArray();
        var instances = new List<object>();
        var failures = new List<string>();
        await Task.Run(() =>
        {
            foreach (var type in descriptors.Where(d => !d.IsKeyedService).Select(d => d.ServiceType).Distinct())
            {
                try { instances.AddRange(sp.GetServices(type).OfType<object>()); }
                catch (Exception ex) { failures.Add($"{type.FullName}: {ex.GetType().Name}: {ex.Message}"); }
            }
            foreach (var (type, key) in descriptors.Where(d => d.IsKeyedService).Select(d => (d.ServiceType, d.ServiceKey)).Distinct())
            {
                try { instances.AddRange(sp.GetKeyedServices(type, key).OfType<object>()); }
                catch (Exception ex) { failures.Add($"{type.FullName} [{key}]: {ex.GetType().Name}: {ex.Message}"); }
            }
        }).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        return (sp, instances.Distinct(ReferenceEqualityComparer.Instance).ToArray());
    }

    private static string NewMarker() => "cscode-dispose-" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>Live (non-zombie) processes whose command line carries <paramref name="marker"/> (Linux /proc; macOS ps).</summary>
    private static bool ProcessesWithMarker(string marker) => ProcessTable.WithMarker(marker).Count > 0;

    private static async Task AssertNoLanguageHostLeftAsync(string marker) =>
        Assert.True(await DevHost.WaitFor(() => !ProcessesWithMarker(marker), 15_000), "a cscode-server language host outlived the container");

    [AvaloniaTheory(Timeout = 120_000)]
    [MemberData(nameof(LanguageHostModes))]
    public async Task Every_singleton_that_is_IAsyncDisposable_is_also_IDisposable(string mode)
    {
        using var env = new RealAppHome();
        var marker = NewMarker();
        var (sp, instances) = await ResolveEverythingAsync(env, mode, marker);
        try
        {
            var asyncOnly = instances.Where(i => i is IAsyncDisposable and not IDisposable).Select(i => i.GetType().FullName).Order().ToArray();
            TestContext.Current.TestOutputHelper?.WriteLine($"{instances.Count} services; IAsyncDisposable: " + string.Join(", ",
                instances.Where(i => i is IAsyncDisposable).Select(i => i.GetType().Name).Order()));
            Assert.True(asyncOnly.Length == 0, "only IAsyncDisposable (the synchronous container Dispose throws on them): " + string.Join(", ", asyncOnly));
        }
        finally
        {
            await sp.DisposeAsync();
        }
        await AssertNoLanguageHostLeftAsync(marker);
    }

    [AvaloniaTheory(Timeout = 120_000)]
    [MemberData(nameof(LanguageHostModes))]
    public async Task The_real_container_disposes_synchronously_on_the_UI_thread_without_throwing(string mode)
    {
        using var env = new RealAppHome();
        var marker = NewMarker();
        var (sp, _) = await ResolveEverythingAsync(env, mode, marker);
        Assert.True(Dispatcher.UIThread.CheckAccess());
        var clock = Stopwatch.StartNew();
        var error = Record.Exception(sp.Dispose);
        clock.Stop();
        Assert.Null(error);
        TestContext.Current.TestOutputHelper?.WriteLine($"synchronous dispose of the idle app's container ({mode}): {clock.Elapsed.TotalMilliseconds:F0} ms");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"disposing the idle app's container took {clock.Elapsed.TotalSeconds:F1} s");
        Assert.Throws<ObjectDisposedException>(() => sp.GetService<IStateStorage>());
        await AssertNoLanguageHostLeftAsync(marker);
    }

    [AvaloniaTheory(Timeout = 120_000)]
    [MemberData(nameof(LanguageHostModes))]
    public async Task The_real_container_disposes_asynchronously_without_throwing(string mode)
    {
        using var env = new RealAppHome();
        var marker = NewMarker();
        var (sp, _) = await ResolveEverythingAsync(env, mode, marker);
        var error = await Record.ExceptionAsync(() => sp.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Null(error);
        Assert.Throws<ObjectDisposedException>(() => sp.GetService<IStateStorage>());
        await AssertNoLanguageHostLeftAsync(marker);
    }

    [AvaloniaTheory(Timeout = 120_000)]
    [MemberData(nameof(LanguageHostModes))]
    public async Task The_orderly_exit_disposes_everything_without_a_failure_or_a_timeout(string mode)
    {
        using var env = new RealAppHome();
        var marker = NewMarker();
        var (sp, _) = await ResolveEverythingAsync(env, mode, marker);
        var report = await AppShutdown.RunAsync(sp, AppShutdown.DefaultDeadline);
        Assert.True(Dispatcher.UIThread.CheckAccess());
        Assert.True(report.Failures.Count == 0 && report.TimedOut.Count == 0, $"timed out: {string.Join(", ", report.TimedOut)}; failures: {string.Join(" | ", report.Failures)}");
        Assert.Throws<ObjectDisposedException>(() => sp.GetService<IStateStorage>());
        await AssertNoLanguageHostLeftAsync(marker);
    }

    private sealed class AsyncOnlySingleton : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// What the guard above is for: with one IAsyncDisposable-only singleton the synchronous container Dispose throws (and skips the singletons
    /// created before it); the orderly exit still completes, never throws, and reports the cause. (Only a few services are resolved here: resolving
    /// everything would also start the process language host, whose disposal this failure skips.)
    /// </summary>
    [AvaloniaFact(Timeout = 120_000)]
    public async Task An_IAsyncDisposable_only_singleton_fails_the_synchronous_dispose_and_the_exit_still_completes_and_says_why()
    {
        using var env = new RealAppHome();
        var sp = AppServices.Build([], s => { env.Configure(s); s.AddSingleton<AsyncOnlySingleton>(); });
        await Task.Run(() =>
        {
            _ = sp.GetRequiredService<IStateStorage>();
            _ = sp.GetRequiredService<Core.Languages.ILanguageServerManager>();
            _ = sp.GetRequiredService<AsyncOnlySingleton>();
        });

        var report = await AppShutdown.RunAsync(sp, AppShutdown.DefaultDeadline);
        Assert.Contains(report.Failures, f => f.StartsWith("disposing services:", StringComparison.Ordinal) && f.Contains(nameof(AsyncOnlySingleton), StringComparison.Ordinal));
        Assert.Empty(report.TimedOut);
        Assert.Throws<ObjectDisposedException>(() => sp.GetService<IStateStorage>());
    }
}
