using System.Text.Json;
using CsCode.Core.Debugging;
using CsCode.Dap.Adapters;
using CsCode.Dap.Session;
using CsCode.Dap.Tests.Fakes;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>
/// The lldb-dap factory end to end through the real <see cref="CsCode.Dap.Service.DebugService"/> with a STAND-IN named <c>lldb-dap</c> (a shell
/// script recording how it was started, then the scripted Python DAP adapter of <see cref="ProcessAdapterTests"/> on its stdio): the session
/// starts it on stdio with no arguments, niced, in the folder, sends the launch the provider filled in, and ends it cleanly.
/// </summary>
[Collection(SerialCollection.Name)]
public class LldbDapStandInTests
{
    [Fact]
    public async Task An_lldb_dap_session_starts_the_adapter_on_stdio_niced_in_the_folder_with_the_filled_in_launch_and_ends_cleanly()
    {
        var python = ProcessAdapterTests.Python();
        Assert.SkipUnless(python is not null && !OperatingSystem.IsWindows() && CsCode.Dap.Transport.ProcessTransport.FindNice() is not null, "Unix with python3 and nice");
        await using var h = new ServiceHarness(moreBuiltins: p => LldbDapAdapterFactory.Builtins(new LldbDapAdapterFactory(p) { AskXcrun = false }));
        var log = h.Dir.Combine("standin");
        Directory.CreateDirectory(log);
        var adapterPy = h.Dir.WriteFile("standin/adapter.py", ProcessAdapterTests.Script.Replace(
            "mode = (r.get('arguments') or {}).get('mode')",
            "mode = (r.get('arguments') or {}).get('mode')\n        open(os.environ['LAUNCH_LOG'], 'w').write(json.dumps(r.get('arguments')))", StringComparison.Ordinal));
        var adapter = h.Dir.WriteFile("standin/llvm/bin/lldb-dap", $"""
            #!/bin/sh
            printf '%s\n' "$#" > '{log}/argc.txt'
            ps -o nice= -p $$ > '{log}/nice.txt'
            pwd -P > '{log}/cwd.txt'
            LAUNCH_LOG='{log}/launch.json' exec '{python}' -I -u '{adapterPy}'

            """);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(adapter, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        h.Settings.Set(LldbDapAdapterFactory.SettingPath, h.Dir.Combine("standin", "llvm", "bin")); // LLVM's bin directory: resolved to lldb-dap

        var session = (DebugSession)await h.StartAsync(ServiceHarness.Config("c", "lldb-dap", extra: new Dictionary<string, object?>
        {
            ["program"] = "/w/a.out",
            ["env"] = new Dictionary<string, object?> { ["MODE"] = "test", ["DROP"] = null },
        }, folder: h.Dir.Uri));
        Assert.Equal(DebugSessionState.Running, session.State);
        var pid = session.AdapterProcessId!.Value;
        Assert.NotNull(session.AdapterProcessMarker);
        Assert.Equal("main", Assert.Single(await session.GetThreadsAsync(Ct)).Name); // DAP over the stand-in's stdio

        Assert.Equal("0", File.ReadAllText(Path.Combine(log, "argc.txt")).Trim()); // stdio: no --connection
        Assert.Equal("10", File.ReadAllText(Path.Combine(log, "nice.txt")).Trim()); // below normal priority, inherited by the debuggee
        // the folder (written with links resolved: macOS's temp directory is below /var, a link into /private)
        Assert.EndsWith("/" + Path.GetFileName(h.Dir.Path.TrimEnd('/')), File.ReadAllText(Path.Combine(log, "cwd.txt")).Trim(), StringComparison.Ordinal);
        using (var launch = JsonDocument.Parse(File.ReadAllText(Path.Combine(log, "launch.json"))))
        {
            var args = launch.RootElement;
            Assert.Equal("/w/a.out", args.GetProperty("program").GetString());
            Assert.Equal(["MODE=test"], args.GetProperty("env").EnumerateArray().Select(e => e.GetString()));
            Assert.Equal(h.Dir.Path.TrimEnd('/'), args.GetProperty("cwd").GetString());
            Assert.Equal("lldb-dap", args.GetProperty("type").GetString());
        }

        await session.StopAsync(Ct);
        Assert.Equal(DebugSessionState.Terminated, session.State);
        await SessionHarness.Eventually(() => !ProcessTable.IsAlive(pid), 5_000, "the adapter exits");
    }
}
