using CsCode.Core.Languages;
using CsCode.Lsp.Manager;
using CsCode.Lsp.Runtime;
using CsCode.Lsp.Tests.Harness;
using Xunit;

namespace CsCode.Lsp.Tests;

/// <summary>
/// The REAL clangd serves Objective-C++ (<c>objective-cpp</c>, <c>.mm</c>) and CUDA (<c>cuda-cpp</c>, <c>.cu</c>) documents through the built-in
/// definition. Objective-C++ parses as such (the only error is the one in the file); a <c>.cu</c> file on a machine without the CUDA SDK gets clangd's
/// "cannot find CUDA installation" diagnostic (docs/C-CPP.md says how a <c>.clangd</c> quiets it). Skips when clangd is not installed.
/// </summary>
[Trait("Category", "RealTools")]
public class RealClangdDialectTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static LspTestEnv Start() => new(c =>
    {
        c.Set(LspSettingKeys.InitializeTimeoutMs, 120_000);
        c.Set(LspSettingKeys.ShutdownTimeoutMs, 5_000);
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
}
