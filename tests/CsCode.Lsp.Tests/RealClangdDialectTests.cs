using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Lsp.Manager;
using CsCode.Lsp.Runtime;
using CsCode.Lsp.Tests.Harness;
using Xunit;

namespace CsCode.Lsp.Tests;

/// <summary>
/// The REAL clangd serves Objective-C++ (<c>objective-cpp</c>, <c>.mm</c>) and CUDA (<c>cuda-cpp</c>, <c>.cu</c>) documents through the built-in
/// definition. Objective-C++ parses as such (the only error is the one in the file); a <c>.cu</c> file on a machine without the CUDA SDK gets clangd's
/// "cannot find CUDA installation" diagnostic (docs/C-CPP.md says how a <c>.clangd</c> quiets it). A C++20 named module (<c>import math;</c>)
/// resolves before anything is built once clangd runs with <c>--experimental-modules-support</c> (the <c>cscode.languageServers</c> args), and
/// not without it. Skips when clangd is not installed.
/// </summary>
[Trait("Category", "RealTools")]
public class RealClangdDialectTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static LspTestEnv Start(params string[] clangdArgs) => new(c =>
    {
        c.Set(LspSettingKeys.InitializeTimeoutMs, 120_000);
        c.Set(LspSettingKeys.ShutdownTimeoutMs, 5_000);
        if (clangdArgs.Length > 0) c.Set(LspSettingKeys.LanguageServers, new Dictionary<string, object> { ["clangd"] = new Dictionary<string, object> { ["args"] = clangdArgs } });
    }, realServers: true);

    private const string ObjectiveCpp = """
        #include <vector>
        __attribute__((objc_root_class))
        @interface Greeter
        - (int)count;
        @end
        @implementation Greeter
        - (int)count { std::vector<int> v{1, 2, 3}; return (int)v.size(); }
        @end
        int main() { undefined_call(); }
        """;

    private const string Cuda = """
        __global__ void add(int n, float *x, float *y) {
          int i = blockIdx.x * blockDim.x + threadIdx.x;
          if (i < n) y[i] = x[i] + y[i];
        }
        """;

    [Fact]
    public async Task Clangd_serves_objective_cpp_and_cuda_documents()
    {
        if (ToolResolver.Resolve("clangd", null, null, null, new LspSettings().SearchDirs) is null) { Assert.Skip("clangd is not installed (not on PATH nor in the cscode.lsp.searchDirs directories)"); return; }
        await using var env = Start();
        env.Dir.WriteFile("compile_flags.txt", "-std=c++20\n");
        var mm = env.Open("src/view.mm", ObjectiveCpp, "objective-cpp");
        await env.WaitForStateAsync("clangd", LanguageServerState.Running, TimeSpan.FromSeconds(90));
        await TestHelpers.EventuallyAsync(() => env.Diagnostics.Get(mm.Uri).Any(d => d.Range.Start.Line == 8 && d.Message.Contains("undefined_call", StringComparison.Ordinal)),
            () => "clangd's error on undefined_call in view.mm\n" + env.Dump(), TimeSpan.FromSeconds(60));
        // Objective-C++ is parsed as such: no error anywhere else (the std::vector inside a method, the @interface)
        Assert.DoesNotContain(env.Diagnostics.Get(mm.Uri), d => d.Severity == DiagnosticSeverity.Error && d.Range.Start.Line != 8);

        var cu = env.Open("src/kernel.cu", Cuda, "cuda-cpp");
        await TestHelpers.EventuallyAsync(() => env.Diagnostics.Get(cu.Uri).Any(d => d.Message.Contains("CUDA", StringComparison.Ordinal)),
            () => "clangd's diagnostics for kernel.cu\n" + env.Dump(), TimeSpan.FromSeconds(60));
        Assert.Single(env.Manager.Servers, s => s.Id == "clangd"); // one clangd for all three dialects
        await env.Manager.StopAllAsync(Ct);
    }

    /// <summary>
    /// A module and its importer, with a compilation database as a build system writes one before building (no module file yet). The
    /// database's clang++ gets <c>-fcxx-modules</c>: Apple's driver otherwise passes <c>-fno-cxx-modules</c> and <c>import</c> is not a keyword.
    /// </summary>
    private static (InMemoryTextDocument Main, string Dir) WriteModules(LspTestEnv env)
    {
        const string math = "export module math;\nexport int add(int a, int b) { return a + b; }\n";
        const string main = "import math;\nint main() {\n  int r = add(1, 2);\n  return r - 3;\n}\n";
        env.Dir.WriteFile("math.cppm", math);
        env.Dir.WriteFile("main.cpp", main);
        var dir = env.Dir.Path;
        var entries = new[] { "math.cppm", "main.cpp" }.Select(f => new Dictionary<string, object>
        {
            ["directory"] = dir,
            ["file"] = Path.Combine(dir, f),
            ["arguments"] = new[] { "clang++", "-std=c++20", "-fcxx-modules", "-c", Path.Combine(dir, f), "-o", Path.Combine(dir, f + ".o") },
        });
        env.Dir.WriteFile("compile_commands.json", System.Text.Json.JsonSerializer.Serialize(entries));
        return (env.Open("main.cpp", main, "cpp"), dir);
    }

    [Fact]
    public async Task A_cpp20_module_resolves_with_clangds_modules_support_before_a_build()
    {
        if (ToolResolver.Resolve("clangd", null, null, null, new LspSettings().SearchDirs) is null) { Assert.Skip("clangd is not installed (not on PATH nor in the cscode.lsp.searchDirs directories)"); return; }
        await using (var without = Start())
        {
            var (doc, _) = WriteModules(without);
            await TestHelpers.EventuallyAsync(() => without.Diagnostics.Get(doc.Uri).Any(d => d.Message.Contains("module 'math' not found", StringComparison.OrdinalIgnoreCase)),
                () => $"clangd without modules support: module 'math' not found (got: {string.Join(" | ", without.Diagnostics.Get(doc.Uri).Select(d => d.Message))})\n" + without.Dump(), TimeSpan.FromSeconds(90));
            await without.Manager.StopAllAsync(Ct);
        }

        await using var env = Start("--experimental-modules-support");
        var (main, _) = WriteModules(env);
        await env.WaitForStateAsync("clangd", LanguageServerState.Running, TimeSpan.FromSeconds(90));
        // clangd answers hover once it built the module itself: add's signature, from the module interface
        Hover? hover = null;
        bool Signature() => hover?.Contents.Any(c => c.Value.Contains("int add(int a, int b)", StringComparison.Ordinal)) == true;
        for (var deadline = DateTime.UtcNow.AddSeconds(90); !Signature() && DateTime.UtcNow < deadline; await Task.Delay(250, Ct))
            hover = await env.Provider<IHoverProvider>(main).ProvideHoverAsync(main, new TextPosition(2, 10), Ct);
        Assert.True(Signature(), $"hover on add: {string.Join(" | ", hover?.Contents.Select(c => c.Value) ?? [])}\n" + env.Dump());
        Assert.DoesNotContain(env.Diagnostics.Get(main.Uri), d => d.Severity == DiagnosticSeverity.Error);
        await env.Manager.StopAllAsync(Ct);
    }
}
