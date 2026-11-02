using Xunit;
using CsCode.Core.Build;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Build.CMake.Tests;

public sealed class CMakeHostTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A language-server manager that only keeps definitions (what the clangd wiring touches).</summary>
    private sealed class Servers : ILanguageServerManager
    {
        private readonly List<(long Seq, LanguageServerDefinition Def)> _defs = [];
        private long _seq;
        public Servers() => _defs.Add((0, new LanguageServerDefinition("clangd", "C / C++ (clangd)", DocumentSelector.ForLanguages(["c"]), "clangd", ["--log=error"])));
        public IReadOnlyList<LanguageServerDefinition> Definitions { get { lock (_defs) return [.. _defs.GroupBy(d => d.Def.Id).Select(g => g.MaxBy(x => x.Seq).Def)]; } }
        public IReadOnlyList<LanguageServerInfo> Servers_ => [];
        IReadOnlyList<LanguageServerInfo> ILanguageServerManager.Servers => [];
        public IDisposable AddDefinition(LanguageServerDefinition definition)
        {
            long seq;
            lock (_defs) _defs.Add((seq = ++_seq, definition));
            return Disposable.Create(() => { lock (_defs) _defs.RemoveAll(d => d.Seq == seq); });
        }
        public ValueTask StartAsync(string id, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask StopAsync(string id, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask RestartAsync(string id, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask StopAllAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public IEventSource<IReadOnlyList<LanguageServerInfo>> ServersChanged => EventSource.Never<IReadOnlyList<LanguageServerInfo>>();
    }

    private sealed record Setup(ServiceProvider Provider, CMakeService Service, CMakeHost Host, TestCommandService Commands, InMemoryStatusBarService Status,
        Servers Servers, ScriptedProcesses Processes, TestConfigurationService Config) : IDisposable
    {
        public void Dispose()
        {
            Host.Dispose();
            Service.Dispose();
            Provider.Dispose();
        }
    }

    private static Setup Create(TempDirectory dir)
    {
        var bin = dir.Combine("bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "cmake"), "");
        var processes = new ScriptedProcesses
        {
            Answer = c =>
            {
                if (c.Arguments[0] == "--build")
                {
                    File.WriteAllText(dir.Combine("build", "app"), "");
                    return ([], 0);
                }
                var reply = CMakeServiceTests.ConfigureReply(c);
                File.WriteAllText(dir.Combine("build", "compile_commands.json"), "[]");
                return reply;
            },
        };
        var config = new TestConfigurationService();
        config.Set(CMakeSettings.ConfigureOnOpen, false);
        var commands = new TestCommandService();
        var status = new InMemoryStatusBarService();
        var servers = new Servers();
        var services = new ServiceCollection()
            .AddSingleton<ICommandService>(commands).AddSingleton<IStatusBarService>(status).AddSingleton<ILanguageServerManager>(servers)
            .AddSingleton<IConfigurationService>(config).AddSingleton<IWindowService>(new RecordingWindowService()).BuildServiceProvider();
        var service = new CMakeService(new TestWorkspace(dir.Path), config, new MemoryState(), null, null, null, processes, null, n => n == "PATH" ? bin : null);
        var host = new CMakeHost(services, service);
        host.Start();
        return new Setup(services, service, host, commands, status, servers, processes, config);
    }

    [Fact]
    public async Task Commands_configure_and_the_status_bar_and_clangd_follow()
    {
        using var dir = new TempDirectory();
        CMakeCSample.Write(dir);
        using var s = Create(dir);
        await s.Service.Detected.WaitAsync(Ct);
        await CMakeServiceTests.WaitUntil(() => s.Status.Items.Any(i => i.Id == "cmake.config"));
        Assert.Contains(s.Status.Items, i => i.Id == "cmake.config" && i.Text.Contains("[Debug]", StringComparison.Ordinal) && i.CommandId == CMakeHost.SelectBuildType);
        Assert.Contains(s.Status.Items, i => i.Id == "cmake.build" && i.CommandId == CMakeHost.Build);
        Assert.DoesNotContain(s.Status.Items, i => i.Id == "cmake.launchTarget"); // no target known before a configure
        foreach (var id in new[] { CMakeHost.Configure, CMakeHost.Build, CMakeHost.Clean, CMakeHost.SelectConfigurePreset, CMakeHost.SelectBuildPreset,
            CMakeHost.SelectBuildType, CMakeHost.SelectLaunchTarget, CMakeHost.RunTarget, CMakeHost.DebugTarget, CMakeHost.LaunchTargetPath })
            Assert.True(s.Commands.Has(id), id);

        var configured = await s.Commands.ExecuteAsync(CMakeHost.Configure);

        Assert.IsType<CMakeProject>(configured);
        await CMakeServiceTests.WaitUntil(() => s.Status.Items.Any(i => i.Id == "cmake.launchTarget"));
        var clangd = s.Servers.Definitions.Single(d => d.Id == "clangd");
        Assert.Equal(["--log=error", "--compile-commands-dir=" + dir.Combine("build")], clangd.Args); // the user's other arguments stay

        // the single executable needs no choice: the path command builds it first and answers its artifact
        var path = await s.Commands.ExecuteAsync(CMakeHost.LaunchTargetPath);
        Assert.Equal(dir.Combine("build", "app"), path);
        Assert.Equal(["--build", dir.Combine("build"), "--target", "app"], s.Processes.Snapshot()[^1].Arguments);
        await CMakeServiceTests.WaitUntil(() => s.Status.Items.Any(i => i.Id == "cmake.launchTarget" && i.Text == "[app]"));

        s.Config.Set(CMakeSettings.PointClangd, false);
        await s.Commands.ExecuteAsync(CMakeHost.Configure);
        await CMakeServiceTests.WaitUntil(() => s.Servers.Definitions.Single(d => d.Id == "clangd").Args.Count == 1);
    }

    [Fact]
    public void Debug_target_configuration_uses_the_chosen_debugger_and_debug_config()
    {
        var target = new CMakeTarget("app", CMakeTargetType.Executable, ["/b/app"], "/s", [], [], ["/s/main.c"]);
        var config = new TestConfigurationService();
        var lldb = CMakeHost.DebugConfigurationFor(target, "/b", config, "/s");
        Assert.Equal(OperatingSystem.IsMacOS() ? "lldb" : "gdb", lldb.Type);
        Assert.Equal(("/b/app", "/b"), (lldb.GetString("program"), lldb.GetString("cwd")));
        if (OperatingSystem.IsMacOS()) Assert.Equal("c", lldb.Properties["sourceLanguages"][0].GetString());

        config.Set(CMakeSettings.Debugger, "lldb-dap");
        config.Set(CMakeSettings.DebugConfig, new Dictionary<string, object> { ["args"] = new[] { "--fast" }, ["stopOnEntry"] = true, ["program"] = "/ignored" });
        var dap = CMakeHost.DebugConfigurationFor(target with { Sources = ["/s/main.cpp"] }, "/b", config, "/s");
        Assert.Equal("lldb-dap", dap.Type);
        Assert.Equal("/b/app", dap.GetString("program"));
        Assert.Equal("--fast", dap.Properties["args"][0].GetString());
        Assert.True(dap.Properties["stopOnEntry"].GetBoolean());
        Assert.Equal("cpp", CMakeHost.SourceLanguage(target with { Sources = ["/s/main.cpp"] }));
        Assert.Equal(("lldb", "gdb", "lldb-dap"), (CMakeHost.DebugType("CodeLLDB", false), CMakeHost.DebugType(null, false), CMakeHost.DebugType("lldb-dap", false)));
    }

    [Fact]
    public async Task Host_is_not_retained_after_dispose()
    {
        using var dir = new TempDirectory();
        CMakeCSample.Write(dir);
        await LeakAssert.NotRetainedAsync(async () =>
        {
            var s = Create(dir);
            await s.Service.Detected;
            await s.Commands.ExecuteAsync(CMakeHost.Configure);
            var host = s.Host;
            s.Dispose();
            Assert.Empty(s.Status.Items);
            Assert.False(s.Commands.Has(CMakeHost.Configure));
            return [host, s.Service];
        });
    }
}
