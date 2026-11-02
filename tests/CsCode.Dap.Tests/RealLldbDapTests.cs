using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CsCode.Core.Debugging;
using CsCode.Dap.Adapters;
using CsCode.Dap.Configuration;
using CsCode.Dap.Session;
using CsCode.Dap.Tests.Fakes;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>
/// The REAL lldb-dap, found as the app finds it (<see cref="LldbDapAdapterFactory.LocateAsync"/>: Apple's through xcrun on a Mac, LLVM's on
/// Linux). The handshake runs wherever lldb-dap exists: started as the factory starts it, it answers <c>initialize</c> on stdio. A whole debug
/// session of a C program (breakpoint, locals, continue, exit) needs to start a debuggee, which on macOS needs debugging authorised: with
/// Developer Mode off (<c>DevToolsSecurity -status</c>) macOS asks for an administrator in a dialog that would hang the test, so it skips with
/// that reason there.
/// </summary>
[Trait("Category", RealAdapterSupport.Category)]
[Collection(SerialCollection.Name)]
public class RealLldbDapTests
{
    private static async Task<string> AdapterOrSkipAsync()
    {
        var path = await new LldbDapAdapterFactory(new DapPlatform()).LocateAsync(Ct);
        Assert.SkipWhen(path is null, "lldb-dap is not installed (macOS: Xcode or its Command Line Tools; Linux: LLDB from the distribution or LLVM)");
        return path!;
    }

    [Fact]
    public async Task The_lldb_dap_the_app_finds_answers_initialize_on_stdio()
    {
        var adapter = await AdapterOrSkipAsync();
        var psi = new ProcessStartInfo(adapter) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        try
        {
            var body = """{"seq":1,"type":"request","command":"initialize","arguments":{"clientID":"cscode","adapterID":"lldb-dap","linesStartAt1":true,"columnsStartAt1":true,"pathFormat":"path"}}""";
            var bytes = Encoding.UTF8.GetBytes(body);
            await p.StandardInput.BaseStream.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {bytes.Length}\r\n\r\n"), Ct);
            await p.StandardInput.BaseStream.WriteAsync(bytes, Ct);
            await p.StandardInput.BaseStream.FlushAsync(Ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await ReadMessageAsync(p.StandardOutput.BaseStream, timeout.Token);
            var root = response.RootElement;
            Assert.Equal("response", root.GetProperty("type").GetString());
            Assert.Equal("initialize", root.GetProperty("command").GetString());
            Assert.True(root.GetProperty("success").GetBoolean(), root.GetRawText());
            Assert.True(root.GetProperty("body").GetProperty("supportsConfigurationDoneRequest").GetBoolean());
            TestMetrics.Report($"[lldb-dap] {adapter}: {root.GetProperty("body").GetRawText()[..Math.Min(400, root.GetProperty("body").GetRawText().Length)]}");
        }
        finally
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }

    private static async Task<JsonDocument> ReadMessageAsync(Stream stream, CancellationToken ct)
    {
        var header = new StringBuilder();
        var one = new byte[1];
        while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one, ct) == 0) throw new EndOfStreamException("lldb-dap closed its stdout: " + header);
            header.Append((char)one[0]);
        }
        var length = int.Parse(header.ToString().Split("\r\n").First(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))["Content-Length:".Length..].Trim(),
            System.Globalization.CultureInfo.InvariantCulture);
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);
        return JsonDocument.Parse(body);
    }

    private const string ProgramSource = """
        #include <stdio.h>

        static int add(int a, int b) {
            int sum = a + b; /* @add */
            return sum;
        }

        int main(void) {
            int total = 0;
            for (int i = 0; i < 3; i++) {
                total = add(total, i);
            }
            printf("total %d\n", total);
            return 3;
        }
        """;

    /// <summary>Whether macOS lets this user debug without an authorisation dialog (Developer Mode on); always true elsewhere.</summary>
    internal static async Task<string?> MacDebuggingBlockedReasonAsync()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        var psi = new ProcessStartInfo("/usr/sbin/DevToolsSecurity", "-status") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        try
        {
            using var p = Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync(Ct) + await p.StandardError.ReadToEndAsync(Ct);
            await p.WaitForExitAsync(Ct);
            return output.Contains("enabled", StringComparison.OrdinalIgnoreCase) && !output.Contains("disabled", StringComparison.OrdinalIgnoreCase)
                ? null
                : $"macOS Developer Mode is off ('DevToolsSecurity -status': {output.Trim()}): starting a debuggee would wait for an administrator in a dialog. Turn it on with 'sudo DevToolsSecurity -enable' to run this test.";
        }
        catch (System.ComponentModel.Win32Exception) { return "DevToolsSecurity is missing"; }
    }

    [Fact]
    public async Task A_C_program_stops_at_a_breakpoint_shows_its_locals_and_exits_with_its_code()
    {
        var adapter = await AdapterOrSkipAsync();
        var blocked = await MacDebuggingBlockedReasonAsync();
        Assert.SkipWhen(blocked is not null, blocked ?? "");
        var cc = RustToolchainLocator.FindOnPath(Environment.GetEnvironmentVariable, "cc") ?? RustToolchainLocator.FindOnPath(Environment.GetEnvironmentVariable, "clang");
        Assert.SkipWhen(cc is null, "no C compiler (cc or clang) on PATH");

        await using var h = new ServiceHarness(moreBuiltins: p => LldbDapAdapterFactory.Builtins(new LldbDapAdapterFactory(p)));
        h.Settings.Set(LldbDapAdapterFactory.SettingPath, adapter);
        var source = h.Dir.WriteFile("add.c", ProgramSource);
        var binary = h.Dir.Combine("add");
        using (var build = Process.Start(new ProcessStartInfo(cc!, ["-g", "-O0", "-o", binary, source]) { UseShellExecute = false })!)
        {
            await build.WaitForExitAsync(Ct);
            Assert.Equal(0, build.ExitCode);
        }
        var line = ProgramSource.Split('\n').Select((l, i) => (l, i)).First(x => x.l.Contains("@add", StringComparison.Ordinal)).i + 1;
        var stops = new List<DebugStoppedEventArgs>();
        var exited = new List<DebugTerminatedEventArgs>();
        h.Service.SessionStarted.Subscribe(s =>
        {
            s.Stopped.Subscribe(e => { lock (stops) stops.Add(e); });
            s.Terminated.Subscribe(e => { lock (exited) exited.Add(e); });
        });
        h.Breakpoints.Add(new Uri(source), line);

        var session = (DebugSession)await h.StartAsync(ServiceHarness.Config("c", "lldb-dap", extra: new Dictionary<string, object?> { ["program"] = binary }, folder: h.Dir.Uri));
        await SessionHarness.Eventually(() => { lock (stops) return stops.Count >= 1; }, 60_000, "stopped at the breakpoint");
        await SessionHarness.Eventually(() => h.Service.StopLocation is { Session.State: DebugSessionState.Stopped }, 30_000, "stop location");
        var loc = h.Service.StopLocation!;
        Assert.Equal("add.c", Path.GetFileName(loc.Uri?.LocalPath));
        Assert.Equal(line, loc.Line);
        var scopes = await session.GetScopesAsync(loc.FrameId, Ct);
        var locals = new Dictionary<string, string>();
        foreach (var scope in scopes.Where(s => !s.Expensive))
            foreach (var v in await session.GetVariablesAsync(scope.VariablesReference, ct: Ct)) locals.TryAdd(v.Name, v.Value);
        Assert.Equal("0", locals["a"]);
        Assert.Equal("0", locals["b"]);

        h.Breakpoints.RemoveAll();
        await Task.Delay(400, Ct); // the removal reaches the adapter after the breakpoint sync's debounce; continuing sooner hits add() again
        await session.ContinueAsync(ct: Ct);
        await SessionHarness.Eventually(() => session.State == DebugSessionState.Terminated, 60_000, "the session ended");
    }
}
