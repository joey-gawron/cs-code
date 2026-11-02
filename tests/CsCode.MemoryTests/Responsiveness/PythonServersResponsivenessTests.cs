using System.Globalization;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using CsCode.Core.Debugging;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Dap.Adapters;
using CsCode.MemoryTests.App;
using CsCode.Profiling.Testing;
using CsCode.Ui.Threading;
using Xunit;

namespace CsCode.MemoryTests.Responsiveness;

/// <summary>
/// Language stage 1's binding budget (docs/briefs/lang-1-python.md, "Threading &amp; isolation"): the hostile matrix's budgets
/// (<see cref="HostileMatrix"/>: dispatcher p99 ≤ 16 ms and max ≤ 50 ms, typing p99 ≤ 20 ms, the heap back within 64 MB, the queues drained within
/// 3 s) with Pyright, Ruff and debugpy all running, in the real app: a project with a <c>.venv</c>, three statements typed into its file at a key
/// every 50 ms with a pause after each (every key is a didChange to both servers; "." and "(" ask Pyright for completion and signature help; in
/// each pause Ruff's pulled and Pyright's pushed diagnostics land), and a debugpy session running a program that prints 200 lines a second into
/// the Debug Console. Measured as <see cref="AppHostileMatrixTests"/> measures (the same probes, keys posted at input priority, no rasterising on
/// the UI thread, best of three attempts, every attempt written to the test output and .scratch/w6-budgets.log), then checked: every key reached
/// the buffer and both servers reported on the typed text. The project's <c>.venv</c> is the tool venv of tools/fetch-debugpy.sh and
/// tools/fetch-lsp-tools.sh --with-python (debugpy, ruff); Pyright comes from the tools directory. Skips without them.
/// </summary>
[Trait("Category", "RealTools")]
public sealed class PythonServersResponsivenessTests(ITestOutputHelper output)
{
    private const string Calc = """
        import json
        import os
        import sys


        def add(a: int, b: int) -> int:
            return a + b


        def total(values: list[int]) -> int:
            result = 0
            for v in values:
                result = add(result, v)
            return result


        count: int = "not a number"
        print(total([1, 2, 3]), sys.argv)

        """;

    private const string Loop = """
        import time

        i = 0
        while True:
            i += 1
            print("debugpy line", i, flush=True)
            time.sleep(0.005)

        """;

    /// <summary>What the user types (three times): "." asks Pyright for completion, "(" is auto-closed and over-typed, <c>missing</c> is undefined.</summary>
    private const string Statement = "print(sys.argv, os.sep, missing); ";

    private static string? Tools() => AppWorkspace.RepoRoot() is { } root ? Path.Combine(root, ".scratch", "tools") : null;

    [AvaloniaFact(Timeout = 900_000)]
    public async Task Typing_stays_within_budget_with_Pyright_Ruff_and_debugpy_running()
    {
        var tools = Tools();
        var venv = tools is null ? "" : Path.Combine(tools, "python", "venv");
        Assert.SkipWhen(OperatingSystem.IsWindows() || tools is null || !File.Exists(Path.Combine(venv, "bin", "ruff"))
            || !File.Exists(Path.Combine(tools, "lsp", "node_modules", ".bin", "pyright-langserver"))
            || !Directory.EnumerateDirectories(Path.Combine(venv, "lib"), "debugpy", SearchOption.AllDirectories).Any(),
            "the Python tools are missing: run tools/fetch-lsp-tools.sh --with-python and tools/fetch-debugpy.sh");
        var failures = new List<string>();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var mode = System.Runtime.GCSettings.LatencyMode; // the app's GC mode, as the matrix sets it
            System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
            (bool Ok, string Report) result;
            try { result = await RunOnceAsync(tools!, venv, attempt); }
            finally { System.Runtime.GCSettings.LatencyMode = mode; }
            Note(result.Report);
            if (result.Ok) return;
            failures.Add(result.Report);
        }
        Assert.Fail("budgets missed on every attempt:\n" + string.Join('\n', failures));
    }

    private static async Task<(bool Ok, string Report)> RunOnceAsync(string tools, string venv, int attempt)
    {
        var options = new MemoryAppHostOptions
        {
            EnabledServers = ["python"],
            Settings = { ["vim.enable"] = false }, // typing as text: with Vim (on by default) the first keys would be Normal-mode commands
            Args = h => [h.Files.Path],
        };
        await using var host = await MemoryAppHost.StartAsync(options, h =>
        {
            Directory.CreateDirectory(h.Home.Combine("data", "tools"));
            Directory.CreateSymbolicLink(h.Home.Combine("data", "tools", "lsp"), Path.Combine(tools, "lsp")); // Pyright, as an install puts it
            Directory.CreateSymbolicLink(h.Files.Combine(".venv"), venv);
            h.Files.WriteFile("pyproject.toml", "[project]\nname = \"budget\"\nversion = \"0\"\n");
            h.Files.WriteFile("calc.py", Calc);
            h.Files.WriteFile("loop.py", Loop);
        });
        var folder = new Uri(host.Files.Path + "/");

        // Pyright and Ruff running and both reporting on the file
        await host.Get<INavigationService>().OpenAsync(new Uri(host.Files.Combine("calc.py")));
        var doc = host.Get<INavigationService>().ActiveDocument!;
        var diagnostics = host.Get<IDiagnosticService>();
        var servers = host.Get<ILanguageServerManager>();
        await Require(() => servers.Servers.Any(s => s.Id == "python" && s.State == LanguageServerState.Running)
            && servers.Servers.Any(s => s.Id == "ruff" && s.State == LanguageServerState.Running), "Pyright and Ruff running");
        await Require(() => diagnostics.Get(doc.Uri).Any(d => d.Code == "F401"), "Ruff's unused import");
        await Require(() => diagnostics.Get(doc.Uri).Any(d => d.Message.Contains("not a number", StringComparison.Ordinal)), "Pyright's type error");
        var bar = host.Get<IStatusBarService>();
        await Require(() => bar.Items.Any(i => i.Id == "python.interpreter" && i.Text.EndsWith("('.venv': venv)", StringComparison.Ordinal)), "the .venv in the status bar");

        // debugpy running a program that prints 200 lines a second, the Debug Console on screen
        var debug = host.Get<IDebugService>();
        var current = host.Get<DebugpyAdapterFactory>().ForCurrentFile(new Uri(host.Files.Combine("loop.py")), "python", folder)!;
        var session = await debug.StartAsync(current with
        {
            Properties = new Dictionary<string, JsonElement>(current.Properties) { ["console"] = JsonSerializer.SerializeToElement("internalConsole") },
        }) ?? throw new InvalidOperationException("debugpy did not start");
        await Require(() => debug is IDebugOutputHistory h && h.GetOutputHistory().Any(e => e.Output.Contains("debugpy line", StringComparison.Ordinal)),
            "the program's output");
        host.Shell.Tools.Show("debug.console");

        var area = await TypingScenario.TextAreaAsync(host);
        area.Caret.Offset = area.Document.TextLength; // type a new statement at the end of the file
        await MemoryAppHost.SettleAsync(20);
        FullGc.Collect();
        var heapBefore = GC.GetTotalMemory(true);
        var ui = host.Get<IUiDispatcher>();
        var publishes = 0;
        using var counting = diagnostics.Changed.Subscribe(_ => Interlocked.Increment(ref publishes));

        using var gc = new GcPauseRecorder();
        using var activity = new ThreadActivityRecorder("Avalonia.Diagnostic.Meter");
        using var probe = new ResponsivenessProbe(a => Dispatcher.UIThread.Post(a)).Start();
        var typing = new TypingLatencyProbe(a => Dispatcher.UIThread.Post(a, DispatcherPriority.Input),
            c => area.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = c.ToString() }));
        for (var i = 0; i < 3; i++) // 102 keys: three statements at a key every 50 ms, each followed by a pause in which the servers' results land
        {
            await typing.TypeAsync(Statement, TimeSpan.FromMilliseconds(50));
            await Task.Delay(600);
        }
        var typed = await typing.DrainAsync();
        var dispatcher = await probe.StopAsync();
        activity.Stop();
        gc.Stop();
        var analysed = Volatile.Read(ref publishes);
        var lines = ((IDebugOutputHistory)debug).GetOutputHistory().Count;

        // every key reached the buffer (auto-closed brackets over-typed), both servers analysed what was typed (the undefined name), the session
        // stopped, the queues drained within 3 s, the heap back
        if (!doc.GetText().EndsWith("\n" + string.Concat(Enumerable.Repeat(Statement, 3)), StringComparison.Ordinal))
            throw new InvalidOperationException("the typed text is not what was typed: " + doc.GetText()[^Math.Min(120, doc.Length)..]);
        if (analysed == 0) throw new InvalidOperationException("no diagnostics arrived while typing: the servers did not analyse the edits");
        await Require(() => diagnostics.Get(doc.Uri).Any(d => d.Code == "F821"), "Ruff's undefined name in the typed statements");
        await Require(() => diagnostics.Get(doc.Uri).Any(d => d.Code == "reportUndefinedVariable"), "Pyright's undefined name in the typed statements");
        var reported = string.Join(", ", diagnostics.Get(doc.Uri).Select(d => $"{d.Source} {d.Code}").Distinct());
        await session.StopAsync();
        await Require(() => debug.Sessions.Count == 0, "the session ended");
        var drained = await MemoryAppHost.WaitFor(() => ui.Stats.Posted - ui.Stats.Executed <= 2, 3000);
        FullGc.Collect();
        var heapGrowthMb = (GC.GetTotalMemory(true) - heapBefore) / 1024.0 / 1024.0;
        var ok = dispatcher.P99Ms <= HostileMatrix.DispatcherP99Ms && dispatcher.MaxMs <= HostileMatrix.DispatcherMaxMs && dispatcher.Dropped == 0
            && typed.Total.P99Ms <= HostileMatrix.TypingP99Ms && typed.Missing == 0 && heapGrowthMb <= HostileMatrix.HeapGrowthMb && drained;
        var report = string.Create(CultureInfo.InvariantCulture,
            $"python-servers attempt {attempt} (load {LoadAverage.OneMinute()}): {dispatcher}; {typed}; {analysed} diagnostics updates in the window (after: {reported}); " +
            $"{lines} debug output batches retained; heap {heapGrowthMb:+0.0;-0.0} MB after; queues drained {drained} (posted-executed {ui.Stats.Posted - ui.Stats.Executed}); " +
            $"in the measured window: {activity.Report()}; {gc.Report()}");
        return (ok, report);
    }

    private static async Task Require(Func<bool> condition, string what)
    {
        if (!await MemoryAppHost.WaitFor(condition, 180_000)) throw new TimeoutException("timed out waiting for: " + what);
    }

    private void Note(string text)
    {
        output.WriteLine(text);
        try
        {
            var dir = Path.Combine(AppWorkspace.RepoRoot() ?? Path.GetTempPath(), ".scratch");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "w6-budgets.log"), $"{DateTime.Now:HH:mm:ss} {text}\n");
        }
        catch (IOException) { }
    }
}
