using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CsCode.App.Docking;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Debugging;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Dap.Adapters;
using CsCode.Editor.View;
using CsCode.Panels.Testing;
using CsCode.Testing.Engine;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>
/// Stage 5's "Done when" in the REAL application (<see cref="RealAppHost"/>: the real composition in a window), each ending with a screenshot in
/// <c>.scratch/screenshots</c> (docs/CSHARP.md):
/// <list type="bullet">
/// <item>CsCode's own repository opens with csharp-ls or Roslyn; F12 on <c>ConcurrentDictionary</c> opens <c>ConcurrentDictionary.cs [decompiled]</c>
/// read-only; with Roslyn, Go to Implementation on a <c>[GeneratedRegex]</c> method opens <c>RegexGenerator.g.cs [generated]</c>;</item>
/// <item>an MSTest-runner and an NUnit-runner project are discovered, run and debugged to a breakpoint in the Test Explorer;</item>
/// <item>F5 on a console project (launch.json with a build preLaunchTask) stops at its breakpoint under netcoredbg, in a folder behind a link.</item>
/// </list>
/// Each skips with the reason when its tool is missing (Roslyn not fetched, csharp-ls not installed, netcoredbg, the test framework packages).
/// </summary>
public class CSharpRealAppTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CsCode.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("CsCode.slnx not found above the test binary");
    }

    private static string? RoslynDll()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var tools = Path.Combine(dir.FullName, ".scratch", "tools", "roslyn-ls");
            if (Directory.Exists(tools))
                return Directory.EnumerateDirectories(tools).OrderByDescending(d => d, StringComparer.Ordinal)
                    .Select(d => Path.Combine(d, "Microsoft.CodeAnalysis.LanguageServer.dll")).FirstOrDefault(File.Exists);
        }
        return null;
    }

    private static string? NetCoreDbg()
    {
        if (Environment.GetEnvironmentVariable("CSCODE_NETCOREDBG") is { Length: > 0 } env && File.Exists(env)) return env;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Path.Combine(d.FullName, ".scratch", "tools", "netcoredbg", NetCoreDbgAdapterFactory.ExecutableName) is var c && File.Exists(c)) return c;
        return null;
    }

    private static void Settings(RealAppHost h, Action<JsonObject> change)
    {
        var path = h.Home.Combine("config", "settings.json");
        var settings = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        change(settings);
        File.WriteAllText(path, settings.ToJsonString());
    }

    private static EditorTab? ActiveTab(RealAppHost host) => host.Get<DocumentWell>().Active as EditorTab;

    private static async Task<CodeEditorControl> OpenAsync(RealAppHost host, string path, int line, int column)
    {
        await host.Get<INavigationService>().OpenAsync(new Uri(path), TextRange.At(line, column));
        Assert.True(await RealAppHost.WaitFor(() => ActiveTab(host) is { LiveView: CodeEditorControl } t && t.Uri.LocalPath == path, 20_000), "opened " + path);
        var view = (CodeEditorControl)ActiveTab(host)!.LiveView!;
        view.Modal.Controller.SetSelection(TextRange.At(line, column), reveal: true);
        Dispatcher.UIThread.RunJobs();
        return view;
    }

    private static (int Line, int Column) Find(string text, string needle, int offsetInNeedle = 0)
    {
        var at = text.IndexOf(needle, StringComparison.Ordinal) + offsetInNeedle;
        var line = text[..at].Count(c => c == '\n');
        return (line, at - (text.LastIndexOf('\n', Math.Max(0, at - 1)) + 1));
    }

    public static TheoryData<string> Servers => new() { "roslyn", "csharp" };

    [AvaloniaTheory(Timeout = 600_000)]
    [MemberData(nameof(Servers))]
    public async Task CsCodes_own_solution_opens_and_F12_reaches_library_and_generated_code(string server)
    {
        var roslyn = RoslynDll();
        if (server == "roslyn") Assert.SkipWhen(roslyn is null, "the Roslyn language server is not fetched: run tools/fetch-roslyn-ls.sh");
        else Assert.SkipUnless(File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools", "csharp-ls")),
            "csharp-ls is not installed (dotnet tool install --global csharp-ls)");
        {
            var repo = RepoRoot();
            await using var host = RealAppHost.Create(h => Settings(h, s =>
            {
                var servers = s["cscode.languageServers"]!.AsObject();
                servers.Remove("csharp");
                if (server == "roslyn")
                    servers["roslyn"] = new JsonObject { ["command"] = "dotnet", ["args"] = new JsonArray([roslyn!, .. BuiltinLanguageServersArgs()]) };
                s["cscode.lsp.serverChoice"] = new JsonObject { ["csharp"] = server };
                s["cscode.lsp.interactiveTimeoutMs"] = 60_000;
                s["cscode.lsp.backgroundTimeoutMs"] = 300_000;
                s["cscode.lsp.initializeTimeoutMs"] = 300_000;
            }));
            await host.StartAsync([repo]);
            var file = Path.Combine(repo, "src", "CsCode.Testing", "Providers", "DotNet", "DotNetTestProvider.cs");
            var text = await File.ReadAllTextAsync(file, Ct);
            var (line, column) = Find(text, "ConcurrentDictionary<string, ProjectInfo>", 3);
            await OpenAsync(host, file, line, column);
            var manager = host.Get<ILanguageServerManager>();
            Assert.True(await RealAppHost.WaitFor(() => manager.Servers.Any(s => s.Id == server && s.State == LanguageServerState.Running), 180_000),
                "server running: " + string.Join(", ", manager.Servers.Select(s => $"{s.Id}:{s.State}:{s.LastError}")));
            var log = host.Get<IOutputService>().GetOrCreate(server == "roslyn" ? "C# (Roslyn) (LSP)" : "C# (csharp-ls) (LSP)");
            if (server == "roslyn")
                Assert.True(await RealAppHost.WaitFor(() => log.GetText().Contains("project initialization complete", StringComparison.Ordinal), 300_000), "Roslyn loaded the solution:\n" + log.GetText());

            // F12 on ConcurrentDictionary: the decompiled type opens read-only in a tab of its own
            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (DateTime.UtcNow < deadline && ActiveTab(host)?.Title != "ConcurrentDictionary.cs [decompiled]")
            {
                await OpenAsync(host, file, line, column);
                await host.RunAsync(KnownCommands.GotoDefinition);
                await RealAppHost.WaitFor(() => ActiveTab(host)?.Title == "ConcurrentDictionary.cs [decompiled]", 5_000);
            }
            var library = ActiveTab(host);
            Assert.Equal("ConcurrentDictionary.cs [decompiled]", library?.Title);
            Assert.True(library!.Doc.IsReadOnly);
            Assert.Contains("class ConcurrentDictionary", library.Doc.GetText(), StringComparison.Ordinal);
            await DevScene.SettleAsync(20);
            TestContext.Current.SendDiagnosticMessage("screenshot: " + DevHostCapture(host, $"csharp-{server}-decompiled"));

            if (server != "roslyn") return;
            // Go to Implementation on a [GeneratedRegex] partial method: the generator's output
            var (gl, gc) = Find(text, "private static partial Regex FilterEscape", "private static partial Regex ".Length + 2);
            deadline = DateTime.UtcNow.AddMinutes(2);
            while (DateTime.UtcNow < deadline && ActiveTab(host)?.Title != "RegexGenerator.g.cs [generated]")
            {
                await OpenAsync(host, file, gl, gc);
                await host.RunAsync("editor.action.goToImplementation");
                await RealAppHost.WaitFor(() => ActiveTab(host)?.Title == "RegexGenerator.g.cs [generated]", 5_000);
            }
            var generated = ActiveTab(host);
            Assert.Equal("RegexGenerator.g.cs [generated]", generated?.Title);
            Assert.True(generated!.Doc.IsReadOnly);
            Assert.Contains("<auto-generated/>", generated.Doc.GetText(), StringComparison.Ordinal);
            await DevScene.SettleAsync(20);
            TestContext.Current.SendDiagnosticMessage("screenshot: " + DevHostCapture(host, "csharp-roslyn-generated"));
        }
    }

    /// <summary>
    /// ARCHITECTURE §6's budgets while Roslyn, the heaviest server CsCode runs, loads CsCode's own solution (restore, design-time builds of 40
    /// projects, analyzers): dispatcher latency p99 ≤ 16 ms and max ≤ 50 ms, and typing (a keystroke's buffer edit on the UI thread) p99 ≤ 20 ms,
    /// typing into the open file every 30 ms from the moment the folder opens until 5 s after the solution is loaded.
    /// </summary>
    [AvaloniaFact(Timeout = 600_000)]
    public async Task The_ui_stays_within_its_budgets_while_Roslyn_loads_CsCodes_solution()
    {
        var roslyn = RoslynDll();
        Assert.SkipWhen(roslyn is null, "the Roslyn language server is not fetched: run tools/fetch-roslyn-ls.sh");
        var repo = RepoRoot();
        await using var host = RealAppHost.Create(h => Settings(h, s =>
        {
            var servers = s["cscode.languageServers"]!.AsObject();
            servers.Remove("csharp");
            servers["roslyn"] = new JsonObject { ["command"] = "dotnet", ["args"] = new JsonArray([roslyn!, .. BuiltinLanguageServersArgs()]) };
            s["cscode.lsp.serverChoice"] = new JsonObject { ["csharp"] = "roslyn" };
            s["cscode.lsp.initializeTimeoutMs"] = 300_000;
        }));
        await host.StartAsync([repo]);
        var file = Path.Combine(repo, "src", "CsCode.Testing", "Providers", "DotNet", "DotNetTestProvider.cs");
        var view = await OpenAsync(host, file, 30, 0);
        var log = host.Get<IOutputService>().GetOrCreate("C# (Roslyn) (LSP)");
        using var probe = new CsCode.Profiling.Testing.ResponsivenessProbe(a => Dispatcher.UIThread.Post(a)).Start();
        var typing = new List<double>();
        var loaded = Stopwatch.StartNew();
        var sinceLoaded = (Stopwatch?)null;
        var document = view.Document;
        while (loaded.Elapsed < TimeSpan.FromMinutes(5) && (sinceLoaded is null || sinceLoaded.Elapsed < TimeSpan.FromSeconds(5)))
        {
            if (sinceLoaded is null && log.GetText().Contains("project initialization complete", StringComparison.Ordinal)) sinceLoaded = Stopwatch.StartNew();
            var t = Stopwatch.GetTimestamp();
            document.ApplyEdits([new TextEdit(TextRange.At(30, 0), "x")]);   // a keystroke, then its undo: the file is left as it was
            document.ApplyEdits([new TextEdit(TextRange.FromLines(30, 0, 30, 1), "")]);
            typing.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds / 2);
            var until = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 30 / 1000;
            while (Stopwatch.GetTimestamp() < until) { Dispatcher.UIThread.RunJobs(); await Task.Delay(1); }
        }
        var report = await probe.StopAsync(); // lets the ticks still queued run (Stop counted one posted just before it as pending)
        typing.Sort();
        var typingP99 = typing[(int)Math.Ceiling(typing.Count * 0.99) - 1];
        TestContext.Current.SendDiagnosticMessage($"Roslyn loaded the solution: {sinceLoaded is not null} ({loaded.Elapsed.TotalSeconds:F1} s in all); {report}; " +
            $"typing p99 {typingP99:F2} ms, max {typing[^1]:F2} ms ({typing.Count} keystrokes); load average {LoadAverage()}");
        Assert.NotNull(sinceLoaded);
        report.AssertWithin(16, 50);
        Assert.True(typingP99 <= 20, $"typing p99 {typingP99:F2} ms");
    }

    private static string LoadAverage()
    {
        if (File.Exists("/proc/loadavg")) return File.ReadAllText("/proc/loadavg").Trim();
        using var p = Process.Start(new ProcessStartInfo("/usr/sbin/sysctl", "-n vm.loadavg") { RedirectStandardOutput = true, UseShellExecute = false });
        return p?.StandardOutput.ReadToEnd().Trim() ?? "?";
    }

    private static IEnumerable<JsonNode?> BuiltinLanguageServersArgs() => CsCode.Lsp.Manager.BuiltinLanguageServers.RoslynArgs.Select(a => (JsonNode?)a);

    private static string DevHostCapture(RealAppHost host, string name)
    {
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = host.Window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame rendered");
        var path = Path.Combine(Hosting.DevHost.ScreenshotDirectory(), name + ".png");
        frame.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        return path;
    }

    private const string MsTestProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><EnableMSTestRunner>true</EnableMSTestRunner></PropertyGroup>
          <ItemGroup>
            <PackageReference Include="MSTest.TestFramework" Version="4.5.2" />
            <PackageReference Include="MSTest.TestAdapter" Version="4.5.2" />
            <PackageReference Include="Microsoft.Testing.Extensions.TrxReport" Version="2.5.2" />
          </ItemGroup>
        </Project>
        """;

    private const string MsTestSource = """
        namespace Calc.MsTest;

        [TestClass]
        public class CalcTests
        {
            [TestMethod]
            public void Adds()
            {
                var sum = 1 + 2;
                Assert.AreEqual(3, sum);
            }

            [TestMethod]
            public void Fails() => Assert.AreEqual(4, 2 + 3);
        }
        """;

    private const string NUnitProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><EnableNUnitRunner>true</EnableNUnitRunner></PropertyGroup>
          <ItemGroup>
            <PackageReference Include="NUnit" Version="4.6.1" />
            <PackageReference Include="NUnit3TestAdapter" Version="6.3.1" />
            <PackageReference Include="Microsoft.Testing.Extensions.TrxReport" Version="2.5.2" />
          </ItemGroup>
        </Project>
        """;

    private const string NUnitSource = """
        using NUnit.Framework;

        namespace Calc.NUnitTests;

        public class CalcTests
        {
            [Test]
            public void Adds()
            {
                var sum = 1 + 2;
                Assert.That(sum, Is.EqualTo(3));
            }

            [TestCase(1, 2, 3)]
            [TestCase(2, 2, 4)]
            public void Sums(int a, int b, int sum) => Assert.That(a + b, Is.EqualTo(sum));
        }
        """;

    [AvaloniaFact(Timeout = 600_000)]
    public async Task MSTest_runner_and_NUnit_projects_run_and_debug_in_the_Test_Explorer()
    {
        var netcoredbg = NetCoreDbg();
        Assert.SkipWhen(netcoredbg is null, "netcoredbg is not available: run tools/fetch-netcoredbg.sh (expects .scratch/tools/netcoredbg/)");
        await using var host = RealAppHost.Create(h =>
        {
            h.Files.WriteFile("Calc.MsTest/Calc.MsTest.csproj", MsTestProject);
            h.Files.WriteFile("Calc.MsTest/CalcTests.cs", MsTestSource);
            h.Files.WriteFile("Calc.NUnitTests/Calc.NUnitTests.csproj", NUnitProject);
            h.Files.WriteFile("Calc.NUnitTests/CalcTests.cs", NUnitSource);
            Settings(h, s =>
            {
                s[NetCoreDbgAdapterFactory.SettingPath] = netcoredbg;
                s[TestingSettings.OpenTesting] = "neverOpen";
            });
        });
        await host.StartAsync([host.Files.Path]);
        var testing = host.Get<TestingHost>();
        var tree = testing.Controller.Tree;
        await host.Get<ICommandService>().ExecuteAsync(TestingHost.FocusView);
        var output = host.Get<IOutputService>().GetOrCreate(TestingHost.OutputChannelName);
        Assert.True(await RealAppHost.WaitFor(() => tree.FindTest("Calc.MsTest.CalcTests.Adds") is not null && tree.FindTest("Calc.NUnitTests.CalcTests.Sums") is not null
            && !testing.Controller.IsDiscovering, 180_000), "discovered: " + string.Join(", ", tree.AllTests().Select(t => t.TestId)) + "\n" + output.GetText());

        await testing.Controller.RunAllAsync();
        Assert.True(await RealAppHost.WaitFor(() => !testing.Controller.IsRunning && tree.FindTest("Calc.MsTest.CalcTests.Fails")!.Outcome == TestOutcome.Failed, 300_000),
            "the run finished:\n" + output.GetText());
        Assert.Equal(TestOutcome.Passed, tree.FindTest("Calc.MsTest.CalcTests.Adds")!.Outcome);
        Assert.Equal(TestOutcome.Passed, tree.FindTest("Calc.NUnitTests.CalcTests.Adds")!.Outcome);
        Assert.Equal(TestOutcome.Passed, tree.FindTest("Calc.NUnitTests.CalcTests.Sums")!.Outcome); // both of its cases
        await DevScene.SettleAsync(20);
        TestContext.Current.SendDiagnosticMessage("screenshot: " + DevHostCapture(host, "csharp-test-explorer-run"));

        // Debug of one test per framework stops at its breakpoint, then reports the result
        var debug = host.Get<IDebugService>();
        foreach (var (id, folder) in new[] { ("Calc.MsTest.CalcTests.Adds", "Calc.MsTest"), ("Calc.NUnitTests.CalcTests.Adds", "Calc.NUnitTests") })
        {
            var source = host.Files.Combine(folder, "CalcTests.cs");
            var line = File.ReadAllText(source).Split('\n').ToList().FindIndex(l => l.Contains("var sum = 1 + 2;", StringComparison.Ordinal));
            debug.Breakpoints.RemoveAll();
            debug.Breakpoints.Add(new Uri(source), line);
            var run = testing.Controller.RunAsync([tree.FindTest(id)!], debug: true);
            Assert.True(await RealAppHost.WaitFor(() => debug.State == DebugState.Stopped && debug.StopLocation is not null, 180_000),
                $"{id} stopped (state {debug.State}):\n" + output.GetText());
            Assert.Equal((source, line), (debug.StopLocation!.Uri!.LocalPath, debug.StopLocation.Line));
            await DevScene.SettleAsync(20);
            TestContext.Current.SendDiagnosticMessage("screenshot: " + DevHostCapture(host, "csharp-test-explorer-debug-" + folder));
            debug.Breakpoints.RemoveAll();
            await debug.ActiveSession!.ContinueAsync();
            Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(120), Ct));
            Assert.True(await RealAppHost.WaitFor(() => !testing.Controller.IsRunning && debug.Sessions.Count == 0, 60_000), "the debug run ended");
            Assert.Equal(TestOutcome.Passed, tree.FindTest(id)!.Outcome);
        }
    }

    [AvaloniaFact(Timeout = 300_000)]
    public async Task F5_stops_at_a_breakpoint_under_netcoredbg_in_a_folder_behind_a_link()
    {
        var netcoredbg = NetCoreDbg();
        Assert.SkipWhen(netcoredbg is null, "netcoredbg is not available: run tools/fetch-netcoredbg.sh (expects .scratch/tools/netcoredbg/)");
        const string Program = "var greeting = \"hello\";\nSystem.Console.WriteLine(greeting); // @break\nreturn 0;\n";
        await using var host = RealAppHost.Create(h =>
        {
            h.Files.WriteFile("App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            h.Files.WriteFile("App/Program.cs", Program);
            h.Files.WriteFile("App/.vscode/tasks.json", """
                { "version": "2.0.0", "tasks": [ { "label": "build", "type": "process", "command": "dotnet", "args": ["build", "${workspaceFolder}/App.csproj"], "problemMatcher": "$msCompile", "group": { "kind": "build", "isDefault": true } } ] }
                """);
            h.Files.WriteFile("App/.vscode/launch.json", """
                { "version": "0.2.0", "configurations": [ { "name": ".NET: Launch", "type": "coreclr", "request": "launch", "preLaunchTask": "build",
                  "program": "${workspaceFolder}/bin/Debug/net10.0/App.dll", "cwd": "${workspaceFolder}", "console": "internalConsole", "stopAtEntry": false } ] }
                """);
            Settings(h, s => s[NetCoreDbgAdapterFactory.SettingPath] = netcoredbg);
        });
        var folder = host.Files.Combine("App");
        Assert.NotEqual(folder, TempDirectory.PhysicalPath(folder)); // the temp folder is behind /var -> /private/var on a Mac
        await host.StartAsync([folder]);
        var source = Path.Combine(folder, "Program.cs");
        await OpenAsync(host, source, 1, 0);
        var debug = host.Get<IDebugService>();
        debug.Breakpoints.Add(new Uri(source), 1);
        await host.Get<ICommandService>().ExecuteAsync("workbench.action.debug.start");
        Assert.True(await RealAppHost.WaitFor(() => debug.State == DebugState.Stopped && debug.StopLocation is not null, 240_000), $"stopped (state {debug.State})");
        // the stop is reported on the file as the editor opened it, not on its /private/var twin
        Assert.Equal((source, 1), (debug.StopLocation!.Uri!.LocalPath, debug.StopLocation.Line));
        await DevScene.SettleAsync(30);
        TestContext.Current.SendDiagnosticMessage("screenshot: " + DevHostCapture(host, "csharp-f5-breakpoint"));
        await debug.StopAllAsync();
    }
}
