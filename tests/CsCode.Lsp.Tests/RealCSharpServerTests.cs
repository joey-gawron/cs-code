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

    private const string ProjectWithoutImplicitUsings = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>disable</ImplicitUsings></PropertyGroup>
        </Project>
        """;

    private static string Untidy(string type) => $$"""
        using System.Text;
        using System.Linq;
        using System;
        using System.Collections.Generic;

        namespace Sample;

        public static class {{type}}
        {
            public static int Count()
            {
                var list = new List<int> { 3, 1 };
                Console.WriteLine(list.Count);
                return list.Count;
            }
        }
        """;

    private const string Fixable = """
        namespace Sample;

        public static class Fixes
        {
            public static int Two()
            {
                System.Collections.Generic.List<int> a = new System.Collections.Generic.List<int>();
                System.Text.StringBuilder b = new System.Text.StringBuilder();
                var unused = 1;
                return a.Count + b.Length;
            }
        }
        """;

    /// <summary>What the will-save participant does for <paramref name="kind"/>: asks every provider, then runs the matching action's command.</summary>
    private static async Task<bool> RunSourceActionAsync(LspTestEnv env, ITextDocument doc, string kind, CancellationToken deadline, Action? beforeCommand = null,
        CodeActionTriggerKind trigger = CodeActionTriggerKind.Automatic)
    {
        var whole = new TextRange(new TextPosition(0, 0), doc.PositionAt(doc.Length));
        var context = new CodeActionContext(env.Diagnostics.Get(doc.Uri), [kind], trigger);
        var offered = new List<CodeAction>();
        foreach (var provider in env.Registry.GetProviders<ICodeActionProvider>(doc))
            offered.AddRange(await provider.ProvideCodeActionsAsync(doc, whole, context, deadline) ?? []);
        if (offered.SingleOrDefault(a => a.Kind is { } k && (k == kind || k.StartsWith(kind + ".", StringComparison.Ordinal))) is not { Command: { } command }) return false;
        beforeCommand?.Invoke();
        await env.Commands.ExecuteAsync(command.CommandId, [.. command.Arguments!.Cast<object?>()]);
        return true;
    }

    [Fact]
    public async Task Roslyn_organizes_usings_and_fixes_analyzer_warnings_through_source_actions_built_from_its_own()
    {
        if (RoslynDll() is null) { Assert.Skip("the Roslyn language server is not fetched: run tools/fetch-roslyn-ls.sh"); return; }
        await using var env = Start(CSharpServers.RoslynId);
        env.Dir.WriteFile("Sample.csproj", ProjectWithoutImplicitUsings);
        env.Dir.WriteFile(".editorconfig", "root = true\n[*.cs]\ncsharp_style_implicit_object_creation_when_type_is_apparent = true\ndotnet_diagnostic.IDE0090.severity = warning\n");
        var untidy = env.Open("Untidy.cs", Untidy("Untidy"), "csharp");
        var late = env.Open("Late.cs", Untidy("Late"), "csharp");
        var asked = env.Open("Asked.cs", Untidy("Asked"), "csharp");
        var fixable = env.Open("Fixes.cs", Fixable, "csharp");
        await env.WaitForStateAsync(CSharpServers.RoslynId, LanguageServerState.Running, TimeSpan.FromSeconds(120));
        await RoslynOf(env).Ready.WaitAsync(TimeSpan.FromSeconds(180), Ct);
        var output = TestContext.Current.TestOutputHelper;

        // Organize Usings: Roslyn's "Remove unnecessary usings", then its "Sort Usings"
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
        {
            var took = System.Diagnostics.Stopwatch.StartNew();
            Assert.True(await RunSourceActionAsync(env, untidy, "source.organizeImports", deadline.Token));
            output?.WriteLine($"organize usings: {took.ElapsedMilliseconds} ms");
        }
        Assert.StartsWith("using System;\nusing System.Collections.Generic;\n\nnamespace Sample;", untidy.GetText().ReplaceLineEndings("\n"));

        // a save whose deadline passed before the command ran: nothing is applied
        using (var expired = new CancellationTokenSource())
        {
            var before = late.GetText();
            Assert.True(await RunSourceActionAsync(env, late, "source.organizeImports", expired.Token, beforeCommand: expired.Cancel));
            Assert.Equal(before, late.GetText());
        }
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
        {
            var took = System.Diagnostics.Stopwatch.StartNew();
            Assert.True(await RunSourceActionAsync(env, late, "source.organizeImports", deadline.Token));
            output?.WriteLine($"organize usings again (warm): {took.ElapsedMilliseconds} ms");
        }
        Assert.StartsWith("using System;\nusing System.Collections.Generic;\n\nnamespace Sample;", late.GetText().ReplaceLineEndings("\n"));

        // Organize Imports (Shift+Alt+O): asked for, not on save, so no deadline comes with it
        Assert.True(await RunSourceActionAsync(env, asked, "source.organizeImports", CancellationToken.None, trigger: CodeActionTriggerKind.Invoked));
        Assert.StartsWith("using System;\nusing System.Collections.Generic;\n\nnamespace Sample;", asked.GetText().ReplaceLineEndings("\n"));

        // Fix All in Document: IDE0090 (raised to a warning) twice in one fix all; CS0219 (the compiler's) and suggestions stay
        await TestHelpers.EventuallyAsync(() => env.Diagnostics.Get(fixable.Uri).Any(d => d.Code == "IDE0090"),
            () => "IDE0090 in Fixes.cs\n" + env.Dump(), TimeSpan.FromSeconds(120));
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
        {
            var took = System.Diagnostics.Stopwatch.StartNew();
            Assert.True(await RunSourceActionAsync(env, fixable, "source.fixAll", deadline.Token));
            output?.WriteLine($"fix all: {took.ElapsedMilliseconds} ms");
        }
        var fixedText = fixable.GetText();
        Assert.Contains("System.Collections.Generic.List<int> a = new();", fixedText);
        Assert.Contains("System.Text.StringBuilder b = new();", fixedText);
        Assert.Contains("var unused = 1;", fixedText);

        // the light bulb (no kind asked) gets none of them
        var bulb = new CodeActionContext(env.Diagnostics.Get(untidy.Uri), null, CodeActionTriggerKind.Invoked);
        foreach (var provider in env.Registry.GetProviders<ICodeActionProvider>(untidy))
            Assert.DoesNotContain(await provider.ProvideCodeActionsAsync(untidy, new TextRange(new TextPosition(0, 0), new TextPosition(0, 0)), bulb, Ct) ?? [],
                a => a.Kind is RoslynSourceActions.OrganizeKind or RoslynSourceActions.FixAllKind);
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
