using System.Text.Json;
using CsCode.Core.Debugging;
using CsCode.Dap.Adapters;
using CsCode.Dap.Session;
using CsCode.Dap.Tests.Fakes;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>
/// The CodeLLDB factory end to end through the real <see cref="CsCode.Dap.Service.DebugService"/> with a STAND-IN for CodeLLDB's adapter: a
/// shell script installed where CodeLLDB's adapter would be (<c>extension/adapter/codelldb</c>) that records how it was started and then runs
/// the scripted Python DAP adapter of <see cref="ProcessAdapterTests"/> on its stdio. Proves the start the real adapter would get — stdio, the
/// command line, below normal priority, the folder as working directory, the launch arguments the provider filled in — and a clean exit,
/// without running CodeLLDB itself.
/// </summary>
[Collection(SerialCollection.Name)]
public class CodeLldbStandInTests
{
    [Fact]
    public async Task An_lldb_session_starts_codelldb_on_stdio_niced_in_the_folder_with_the_filled_in_launch_and_ends_cleanly()
    {
        var python = ProcessAdapterTests.Python();
        Assert.SkipUnless(python is not null && OperatingSystem.IsLinux() && CsCode.Dap.Transport.ProcessTransport.FindNice() is not null, "Linux with python3 and nice");
        if (!OperatingSystem.IsLinux()) return; // (for the platform analyzer)
        await using var h = new ServiceHarness(moreBuiltins: p => CodeLldbAdapterFactory.Builtins(new CodeLldbAdapterFactory(p)));
        var log = h.Dir.Combine("standin");
        Directory.CreateDirectory(log);
        // the scripted adapter, also writing the launch arguments it received
        var adapterPy = h.Dir.WriteFile("standin/adapter.py", ProcessAdapterTests.Script.Replace(
            "mode = (r.get('arguments') or {}).get('mode')",
            "mode = (r.get('arguments') or {}).get('mode')\n        open(os.environ['LAUNCH_LOG'], 'w').write(json.dumps(r.get('arguments')))", StringComparison.Ordinal));
        var adapter = h.Dir.WriteFile("standin/install/extension/adapter/codelldb", $"""
            #!/bin/sh
            printf '%s\n' "$@" > '{log}/args.txt'
            nice > '{log}/nice.txt'
            pwd > '{log}/cwd.txt'
            LAUNCH_LOG='{log}/launch.json' exec '{python}' -I -u '{adapterPy}'

            """);
        File.SetUnixFileMode(adapter, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        h.Settings.Set(CodeLldbAdapterFactory.SettingPath, h.Dir.Combine("standin", "install")); // the install directory: resolved to its adapter

        var session = (DebugSession)await h.StartAsync(ServiceHarness.Config("rust", "lldb", extra: new Dictionary<string, object?> { ["program"] = "/w/target/debug/app" }, folder: h.Dir.Uri));
        Assert.Equal(DebugSessionState.Running, session.State);
        var pid = session.AdapterProcessId!.Value;
        Assert.NotNull(session.AdapterProcessMarker);
        Assert.Equal("main", Assert.Single(await session.GetThreadsAsync(Ct)).Name); // DAP over the stand-in's stdio

        Assert.Equal(["--settings", """{"sourceLanguages":["rust"],"evaluateForHovers":true,"commandCompletions":true}"""], File.ReadAllLines(Path.Combine(log, "args.txt")));
        Assert.Equal("10", File.ReadAllText(Path.Combine(log, "nice.txt")).Trim()); // below normal priority, inherited by what it starts
        Assert.Equal(h.Dir.Path.TrimEnd('/'), File.ReadAllText(Path.Combine(log, "cwd.txt")).Trim()); // rustc for the formatters runs in the folder
        using (var launch = JsonDocument.Parse(File.ReadAllText(Path.Combine(log, "launch.json"))))
        {
            var args = launch.RootElement;
            Assert.Equal("/w/target/debug/app", args.GetProperty("program").GetString());
            Assert.Equal("console", args.GetProperty("terminal").GetString());
            Assert.Equal("evaluate", args.GetProperty("_adapterSettings").GetProperty("consoleMode").GetString());
            Assert.Equal(["rust"], args.GetProperty("sourceLanguages").EnumerateArray().Select(l => l.GetString()));
            Assert.Equal(h.Dir.Path.TrimEnd('/'), args.GetProperty("relativePathBase").GetString());
            Assert.Equal(h.Dir.Path.TrimEnd('/'), args.GetProperty("cwd").GetString());
        }

        await session.StopAsync(Ct);
        Assert.Equal(DebugSessionState.Terminated, session.State);
        await SessionHarness.Eventually(() => !ProcessTable.IsAlive(pid), 5_000, "the adapter exits");
    }
}
