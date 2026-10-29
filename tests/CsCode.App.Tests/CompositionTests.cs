using Avalonia.Headless.XUnit;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>
/// The REAL composition root with every real module (no fakes): each module's tests compose only itself, so a dependency cycle
/// between modules (e.g. editor services -> navigation -> document well -> editor view factory -> editor services) shows up only here.
/// A cycle through factory lambdas is not detected by the container: it recurses until its stack guard hops threads and deadlocks.
/// </summary>
public class CompositionTests
{
    [AvaloniaFact(Timeout = 60_000)]
    public async Task Every_registered_service_of_the_real_app_resolves_once_without_cycles()
    {
        await Task.Yield();
        using var home = new TempDirectory("cscode-composition-");
        var previousHome = Environment.GetEnvironmentVariable("CSCODE_HOME");
        Environment.SetEnvironmentVariable("CSCODE_HOME", home.Path); // never the user's real config/state directories
        try
        {
            var editorLanesBefore = CountThreads("cscode-editor-i");
            IServiceCollection? registered = null;
            var failures = new List<string>();
            await using (var sp = AppServices.Build([], s => registered = s))
            {
                var types = registered!.Where(d => !d.ServiceType.IsGenericTypeDefinition).Select(d => d.ServiceType).Distinct().ToArray();
                // Off the UI thread with a deadline: a cycle deadlocks inside the container, and a blocked UI thread would hang the run instead of failing it.
                await Task.Run(() =>
                {
                    foreach (var type in types)
                    {
                        try { _ = sp.GetServices(type).ToArray(); }
                        catch (Exception ex) { lock (failures) failures.Add($"{type.FullName}: {ex.GetType().Name}: {ex.Message}"); }
                    }
                }).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                // EditorServices owns one I/O lane; a construction cycle creates one per recursion level.
                Assert.InRange(CountThreads("cscode-editor-i") - editorLanesBefore, 0, 1);
            }
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CSCODE_HOME", previousHome);
        }
    }

    /// <summary>Live OS threads whose name starts with <paramref name="prefix"/> (Linux /proc; 0 elsewhere).</summary>
    private static int CountThreads(string prefix)
    {
        if (!OperatingSystem.IsLinux()) return 0;
        var n = 0;
        foreach (var dir in Directory.EnumerateDirectories("/proc/self/task"))
        {
            try { if (File.ReadAllText(Path.Combine(dir, "comm")).StartsWith(prefix, StringComparison.Ordinal)) n++; }
            catch (IOException) { /* the thread exited while we looked */ }
        }
        return n;
    }
}
