using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Debugging;
using CsCode.Core.Languages;
using CsCode.Dap.Adapters;
using CsCode.Panels.Testing;
using CsCode.Testing.Engine;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>
/// The Test Explorer in the REAL application (<see cref="RealAppHost"/>) on a cargo package: the folder opens and its tests are discovered
/// (cargo builds, the test binary lists them); Run All runs them, a failure lands in the Error List at the panic's line; then Debug of one test
/// starts the test binary under the platform's Rust debugger — GDB on Linux, CodeLLDB on macOS (each the default there) — stops at a breakpoint
/// in the test, and after Continue the test's result comes back from the session's output. Skips with the reason when the debugger (GDB 14+, or
/// CodeLLDB's adapter: <see cref="CodeLldbToolchain.FindAdapter"/>), cargo or rustc is missing.
/// </summary>
public class TestExplorerRealAppTests
{
    private const string Lib = """
        pub fn add(a: i32, b: i32) -> i32 { a + b }

        #[cfg(test)]
        mod tests {
            use super::*;
            #[test]
            fn adds() {
                let sum = add(1, 2);
                assert_eq!(sum, 3);
            }
            #[test]
            fn fails() { assert_eq!(add(1, 2), 4); }
        }
        """;

    [AvaloniaFact(Timeout = 240_000)]
    public async Task Rust_tests_are_discovered_run_and_debugged_to_a_breakpoint_in_the_real_app()
    {
        var codelldb = OperatingSystem.IsMacOS() ? CodeLldbToolchain.FindAdapter() : null;
        var skip = OperatingSystem.IsMacOS()
            ? codelldb is null ? "CodeLLDB's adapter was not found (tools/fetch-codelldb.sh, or CSCODE_CODELLDB)" : GdbToolchain.FindCargo() is null ? "cargo is not on PATH" : null
            : GdbToolchain.SkipReason();
        Assert.SkipWhen(skip is not null, skip ?? "");
        await using var host = RealAppHost.Create(h =>
        {
            h.Files.WriteFile("Cargo.toml", "[package]\nname = \"rs_sample\"\nversion = \"0.1.0\"\nedition = \"2021\"\n");
            h.Files.WriteFile("src/lib.rs", Lib);
            var settingsPath = h.Home.Combine("config", "settings.json");
            var settings = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
            settings[GdbAdapterFactory.ArgsSettingPath] = new JsonArray("-nx");
            settings["cscode.debug.rustDebugger"] = codelldb is null ? "gdb" : "lldb";
            if (codelldb is not null) settings[CodeLldbAdapterFactory.SettingPath] = codelldb;
            settings[TestingSettings.OpenTesting] = "neverOpen";
            File.WriteAllText(settingsPath, settings.ToJsonString());
        });
        await host.StartAsync([host.Files.Path]);
        var testing = host.Get<TestingHost>();
        var tree = testing.Controller.Tree;
        Assert.True(await RealAppHost.WaitFor(() => tree.FindTest("lib/tests::adds") is not null && !testing.Controller.IsDiscovering, 180_000),
            "discovered: " + string.Join(", ", tree.AllTests().Select(t => t.TestId)) + "; output: " + host.Get<Core.Platform.IOutputService>().GetOrCreate(TestingHost.OutputChannelName).GetText());
        var lib = host.Files.Combine("src", "lib.rs");
        Assert.Equal(new TestLocation(lib, 6), tree.FindTest("lib/tests::adds")!.Location);

        // Run All: one passes, one fails, and the failure is in the Error List at the panic
        await testing.Controller.RunAllAsync();
        Assert.True(await RealAppHost.WaitFor(() => !testing.Controller.IsRunning && tree.FindTest("lib/tests::fails")!.Outcome == TestOutcome.Failed, 60_000), "the run finished");
        Assert.Equal(TestOutcome.Passed, tree.FindTest("lib/tests::adds")!.Outcome);
        Assert.True(await RealAppHost.WaitFor(() => host.Get<IDiagnosticService>().Get(new Uri(lib)).Any(d => d.Source == "Test Explorer" && d.Range.Start.Line == 11), 10_000), "the failure in the Error List");

        // Debug one test: a breakpoint in it stops the test binary there
        var debug = host.Get<IDebugService>();
        var line = 7; // let sum = add(1, 2);
        // rustc records the directory it ran in as the OS resolves it (macOS: /private/var/... for the temp folder's /var/...), and the debugger
        // matches breakpoints by that path: set it on the resolved path, as a user's breakpoint in a folder without links is
        var source = new Uri(TempDirectory.PhysicalPath(lib));
        debug.Breakpoints.Add(source, line);
        var run = testing.Controller.RunAsync([tree.FindTest("lib/tests::adds")!], debug: true);
        Assert.True(await RealAppHost.WaitFor(() => debug.State == DebugState.Stopped && debug.StopLocation is not null, 120_000),
            $"stopped (state {debug.State}); output: " + host.Get<Core.Platform.IOutputService>().GetOrCreate(TestingHost.OutputChannelName).GetText());
        Assert.Equal((source.LocalPath, line), (TempDirectory.PhysicalPath(debug.StopLocation!.Uri!.LocalPath), debug.StopLocation.Line));
        Assert.Equal(TestOutcome.Running, tree.FindTest("lib/tests::adds")!.Outcome);
        debug.Breakpoints.RemoveAll();
        await debug.ActiveSession!.ContinueAsync();
        Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));
        Assert.True(await RealAppHost.WaitFor(() => !testing.Controller.IsRunning && debug.Sessions.Count == 0, 30_000), "the debug run ended");
        Assert.Equal(TestOutcome.Passed, tree.FindTest("lib/tests::adds")!.Outcome); // read from the session's output
    }
}
