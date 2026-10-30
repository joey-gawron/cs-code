using System.Text.Json;
using CsCode.Core.Platform;
using CsCode.Dap.Adapters;
using CsCode.Dap.Service;
using CsCode.Dap.Tests.Fakes;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>
/// F5 without a launch configuration offers "Create launch.json": the launch.json is made for the folder's debugger — a cargo package gets the
/// Rust debugger (GDB by default, CodeLLDB with <c>cscode.debug.rustDebugger</c>: "lldb") — and the user picks when the folder does not tell.
/// Before, the first debugger in label order (.NET) was used for every folder.
/// </summary>
public class DefaultDebuggerTests
{
    private static ServiceHarness Harness() => new(moreBuiltins: p =>
    [
        .. GdbAdapterFactory.Builtins(new GdbAdapterFactory(p)),
        .. CodeLldbAdapterFactory.Builtins(new CodeLldbAdapterFactory(p)),
        .. ServiceCollectionExtensions.JsDebugAdapters().Select(a => a(new JsDebugAdapterFactory(p))),
    ]);

    private static async Task<IReadOnlyList<string?>> CreateViaF5Async(ServiceHarness h)
    {
        h.Window.MessageActionResponses.Enqueue("Create launch.json");
        await h.Commands.ExecuteAsync(KnownCommands.DebugStart);
        var launch = Path.Combine(h.Dir.Path, ".cscode", "launch.json");
        await SessionHarness.Eventually(() => File.Exists(launch), 5000, "launch.json created");
        using var doc = JsonDocument.Parse(File.ReadAllText(launch), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return [.. doc.RootElement.GetProperty("configurations").EnumerateArray().Select(c => c.GetProperty("type").GetString())];
    }

    private static void CargoPackage(ServiceHarness h)
    {
        h.Dir.WriteFile("Cargo.toml", "[package]\nname = \"app\"\nversion = \"0.1.0\"\n");
        h.Dir.WriteFile("src/main.rs", "fn main() {}\n");
    }

    [Fact]
    public async Task A_cargo_package_gets_a_gdb_launch_json_by_default()
    {
        await using var h = Harness();
        CargoPackage(h);
        Assert.Equal(["gdb", "gdb", "gdb"], await CreateViaF5Async(h));
        Assert.Contains("cargo: build", File.ReadAllText(Path.Combine(h.Dir.Path, ".cscode", "tasks.json")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("lldb")]
    [InlineData("CodeLLDB")]
    public async Task With_the_rust_debugger_set_to_codelldb_a_cargo_package_gets_a_codelldb_launch_json(string setting)
    {
        await using var h = Harness();
        CargoPackage(h);
        h.Settings.Set(DebugService.RustDebuggerSetting, setting);
        Assert.Equal(["lldb", "lldb", "lldb"], await CreateViaF5Async(h));
    }

    [Fact]
    public async Task A_typescript_folder_gets_js_debug_and_a_dotnet_folder_gets_netcoredbg()
    {
        await using (var ts = Harness())
        {
            ts.Dir.WriteFile("tsconfig.json", "{}");
            Assert.All(await CreateViaF5Async(ts), t => Assert.Equal("pwa-node", t));
        }
        await using var cs = Harness();
        cs.Dir.WriteFile("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"/>");
        Assert.All(await CreateViaF5Async(cs), t => Assert.Equal("coreclr", t));
    }

    [Fact]
    public async Task A_folder_that_does_not_tell_lets_the_user_pick_the_debugger()
    {
        await using var h = Harness();
        // the list is rebuilt on the lane as types arrive (the harness registers its "fake" type after construction): read it once complete
        await SessionHarness.Eventually(() => h.Service.DebuggerTypes.Any(t => t.Type == "lldb") && h.Service.DebuggerTypes.Any(t => t.Type == "fake"), 5000, "debugger types");
        var index = h.Service.DebuggerTypes.Select(t => t.Type).ToList().IndexOf("lldb");
        h.Window.QuickPickResponses.Enqueue(index);
        Assert.All(await CreateViaF5Async(h), t => Assert.Equal("lldb", t));
        Assert.Empty(h.Window.QuickPickResponses); // the pick was shown
    }

    [Fact]
    public async Task Cancelling_the_pick_creates_nothing()
    {
        await using var h = Harness();
        h.Window.MessageActionResponses.Enqueue("Create launch.json");
        h.Window.QuickPickResponses.Enqueue(null);
        await h.Commands.ExecuteAsync(KnownCommands.DebugStart);
        Assert.Empty(h.Window.QuickPickResponses);
        Assert.False(Directory.Exists(Path.Combine(h.Dir.Path, ".cscode")));
    }
}
