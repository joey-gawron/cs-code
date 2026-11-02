using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Lsp.CSharp;
using CsCode.Lsp.Manager;
using CsCode.Lsp.Runtime;
using CsCode.Lsp.Tests.Harness;
using Xunit;

namespace CsCode.Lsp.Tests;

/// <summary>
/// Both C# servers through the built-in definitions, the real process factory and CsCode's own client, on a small solution in a temp folder:
/// Roslyn (fetched by tools/fetch-roslyn-ls.sh into .scratch/tools/roslyn-ls; started as <c>dotnet Microsoft.CodeAnalysis.LanguageServer.dll</c>
/// with the built-in arguments) opens the solution, reports itself ready, pulls diagnostics, and answers go to definition into the BCL and into a
/// source generator's output with read-only virtual documents whose text it serves and inside which hover works; csharp-ls (a dotnet tool, found
/// on PATH or in ~/.dotnet/tools) does the same for library code through <c>csharp/metadata</c>. Each test skips with the reason when its
/// server is not installed.
/// </summary>
[Trait("Category", "RealTools")]
public class RealCSharpServerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Project = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
        </Project>
        """;

    private const string Program = """
        using System.Text.RegularExpressions;

        namespace Sample;

        public static partial class Program
        {
            [GeneratedRegex("a+b")]
            private static partial Regex AB();

            public static int Main()
            {
                string s = "hello";
                int wrong = "text";
                Console.WriteLine(AB().IsMatch(s));
                return wrong;
            }
        }
        """;

    /// <summary>Roslyn's server assembly in a fetched .scratch/tools/roslyn-ls (this checkout's, else a parent checkout's), or null.</summary>
    internal static string? RoslynDll()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var tools = Path.Combine(dir.FullName, ".scratch", "tools", "roslyn-ls");
            if (!Directory.Exists(tools)) continue;
            var dll = Directory.EnumerateDirectories(tools).OrderByDescending(d => d, StringComparer.Ordinal)
                .Select(d => Path.Combine(d, "Microsoft.CodeAnalysis.LanguageServer.dll")).FirstOrDefault(File.Exists);
            if (dll is not null) return dll;
        }
        return null;
    }

    private static string? CSharpLs() => ToolResolver.Resolve("csharp-ls", null, null, null, new LspSettings().SearchDirs);

    private static LspTestEnv Start(string server)
    {
        var env = new LspTestEnv(c =>
        {
            c.Set(LspSettingKeys.InteractiveTimeoutMs, 60_000);
            c.Set(LspSettingKeys.BackgroundTimeoutMs, 120_000);
            c.Set(LspSettingKeys.InitializeTimeoutMs, 120_000);
            c.Set(LspSettingKeys.ShutdownTimeoutMs, 5_000);
            c.Set(LspSettingKeys.ServerChoice, new Dictionary<string, string> { [BuiltinLanguageServers.CSharpGroup] = server });
            var env = new Dictionary<string, string> { ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1", ["DOTNET_NOLOGO"] = "1" };
            // every other built-in off (a solution folder also has JSON and XML files); the chosen C# server on
            var overrides = BuiltinLanguageServers.All.Where(d => d.Id is not (CSharpServers.RoslynId or CSharpServers.CSharpLsId))
                .ToDictionary(d => d.Id, _ => (object)new { enabled = false });
            overrides[server] = server == CSharpServers.RoslynId
                ? new { command = "dotnet", args = new[] { RoslynDll()! }.Concat(BuiltinLanguageServers.RoslynArgs).ToArray(), env }
                : new { env };
            c.Set(LspSettingKeys.LanguageServers, overrides);
        }, realServers: true);
        env.Dir.WriteFile("Sample.csproj", Project);
        env.Dir.WriteFile("Sample.slnx", "<Solution>\n  <Project Path=\"Sample.csproj\" />\n</Solution>\n");
        return env;
    }

    /// <summary>The running Roslyn server's session state (<see cref="RoslynExtension"/>).</summary>
    internal static RoslynSession RoslynOf(LspTestEnv env) =>
        Assert.IsType<RoslynSession>(Assert.Single(env.Manager.RunningClients(CSharpServers.RoslynId)).Extension?.State);

    private static TextPosition At(ITextDocument doc, string text, int delta = 0) => doc.PositionAt(doc.GetText().IndexOf(text, StringComparison.Ordinal) + delta);

    private static async Task<LocationLink> DefinitionAsync(LspTestEnv env, ITextDocument doc, TextPosition position, Func<LocationLink, bool> accept, string what,
        DefinitionKind kind = DefinitionKind.Definition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(120);
        IReadOnlyList<LocationLink>? last = null;
        while (DateTime.UtcNow < deadline)
        {
            if (env.Registry.GetProviders<IDefinitionProvider>(doc).FirstOrDefault() is { } provider)
            {
                last = await provider.ProvideDefinitionAsync(doc, position, kind, Ct);
                if (last?.FirstOrDefault(accept) is { } link) return link;
            }
            await Task.Delay(500, Ct);
        }
        Assert.Fail($"no definition for {what} (last: {string.Join(", ", last?.Select(l => l.TargetUri.ToString()) ?? [])})\n{env.Dump()}");
        return null!;
    }

    [Fact]
    public async Task Roslyn_opens_the_solution_reports_ready_and_pulls_diagnostics()
    {
        if (RoslynDll() is null) { Assert.Skip("the Roslyn language server is not fetched: run tools/fetch-roslyn-ls.sh"); return; }
        await using var env = Start(CSharpServers.RoslynId);
        var doc = env.Open("Program.cs", Program, "csharp");
        var info = await env.WaitForStateAsync(CSharpServers.RoslynId, LanguageServerState.Running, TimeSpan.FromSeconds(120));
        Assert.Contains("CSharp", info.ServerName);

        var roslyn = RoslynOf(env);
        var took = await roslyn.Ready.WaitAsync(TimeSpan.FromSeconds(180), Ct);
        Assert.Equal([Path.Combine(env.Root, "Sample.slnx")], roslyn.Opened);
        Assert.True(took > TimeSpan.Zero);

        // CS0029 (cannot convert string to int) on `int wrong = "text";`: Roslyn only answers pull diagnostics
        var line = Program.Split('\n').ToList().FindIndex(l => l.Contains("int wrong", StringComparison.Ordinal));
        await TestHelpers.EventuallyAsync(() => env.Diagnostics.Get(doc.Uri).Any(d => d.Range.Start.Line == line && d.Code == "CS0029"),
            () => "CS0029 on line " + line + "\n" + env.Dump(), TimeSpan.FromSeconds(120));
    }

    [Fact]
    public async Task Roslyn_go_to_definition_into_the_BCL_and_into_generated_code_opens_read_only_virtual_documents()
    {
        if (RoslynDll() is null) { Assert.Skip("the Roslyn language server is not fetched: run tools/fetch-roslyn-ls.sh"); return; }
        await using var env = Start(CSharpServers.RoslynId);
        var doc = env.Open("Program.cs", Program, "csharp");
        await env.WaitForStateAsync(CSharpServers.RoslynId, LanguageServerState.Running, TimeSpan.FromSeconds(120));
        await RoslynOf(env).Ready.WaitAsync(TimeSpan.FromSeconds(180), Ct);

        // `string` → System.String, decompiled
        var bcl = await DefinitionAsync(env, doc, At(doc, "string s"), l => VirtualDocumentUri.IsVirtual(l.TargetUri), "string");
        Assert.Equal("String.cs [decompiled]", VirtualDocumentUri.Title(bcl.TargetUri));
        var text = (await env.VirtualDocuments.GetAsync(bcl.TargetUri, Ct))!.Text;
        Assert.Contains("class String", text);
        Assert.True((await env.VirtualDocuments.StatAsync(bcl.TargetUri, Ct))!.IsReadOnly);

        // opened, it is served by the same server under its own file: URI, so hover works inside it
        var library = new InMemoryTextDocument(bcl.TargetUri, "csharp", text);
        using (env.Documents.Register(library))
        {
            Hover? hover = null;
            await TestHelpers.EventuallyAsync(() =>
            {
                if (env.Registry.GetProviders<IHoverProvider>(library).FirstOrDefault() is not { } p) return false;
                hover = p.ProvideHoverAsync(library, bcl.TargetSelectionRange.Start, Ct).AsTask().GetAwaiter().GetResult();
                return hover is not null && string.Join("\n", hover.Contents.Select(c => c.Value)).Contains("String", StringComparison.Ordinal);
            }, () => "hover inside String.cs\n" + env.Dump(), TimeSpan.FromSeconds(60));
        }

        // the partial method's declaration → its implementation in the Regex generator's output (go to implementation; go to definition
        // answers the declaration itself)
        var generated = await DefinitionAsync(env, doc, At(doc, "AB();", 0), l => VirtualDocumentUri.TryParse(l.TargetUri, out _, out var kind, out _, out _) && kind == VirtualDocumentKinds.Generated,
            "AB", DefinitionKind.Implementation);
        Assert.Equal("RegexGenerator.g.cs [generated]", VirtualDocumentUri.Title(generated.TargetUri));
        Assert.Contains("<auto-generated/>", (await env.VirtualDocuments.GetAsync(generated.TargetUri, Ct))!.Text);
    }

    [Fact]
    public async Task Csharp_ls_go_to_definition_into_the_BCL_opens_its_decompiled_metadata_as_a_virtual_document()
    {
        if (CSharpLs() is null) { Assert.Skip("csharp-ls is not installed (dotnet tool install --global csharp-ls)"); return; }
        await using var env = Start(CSharpServers.CSharpLsId);
        var doc = env.Open("Program.cs", Program, "csharp");
        await env.WaitForStateAsync(CSharpServers.CSharpLsId, LanguageServerState.Running, TimeSpan.FromSeconds(120));
        var bcl = await DefinitionAsync(env, doc, At(doc, "string s"), l => VirtualDocumentUri.IsVirtual(l.TargetUri), "string");
        Assert.Equal("String.cs [decompiled]", VirtualDocumentUri.Title(bcl.TargetUri));
        Assert.StartsWith("csharp:", env.Manager.External.ServerUriOf(bcl.TargetUri));
        var text = (await env.VirtualDocuments.GetAsync(bcl.TargetUri, Ct))!.Text;
        Assert.Contains("class String", text);
    }
}
