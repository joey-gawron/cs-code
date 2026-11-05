using System.Text.Json;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Lsp.CSharp;
using CsCode.Lsp.Manager;
using CsCode.Lsp.Runtime;
using CsCode.Lsp.Tests.Harness;
using Xunit;

namespace CsCode.Lsp.Tests;

/// <summary>
/// The C# servers without running them: the Roslyn definition and its launch line, the choice between csharp-ls and Roslyn, how each server's
/// library and generated locations are recognised, and the names their virtual documents get (docs/CSHARP.md).
/// </summary>
public class CSharpServerTests
{
    [Fact]
    public void Roslyn_is_a_built_in_alternative_to_csharp_ls_that_runs_only_when_chosen()
    {
        var roslyn = BuiltinLanguageServers.Find(CSharpServers.RoslynId)!;
        Assert.Equal("roslyn-language-server", roslyn.Definition.Command);
        Assert.Equal(["--stdio", "--logLevel", "Information", "--extensionLogDirectory", "${cscodeLogsDir}/roslyn", "--telemetryLevel", "off", "--clientProcessId", "${cscodePid}"],
            roslyn.Definition.Args);
        Assert.Equal(RootStrategy.Outermost, roslyn.Roots);
        Assert.Equal("Microsoft.CodeAnalysis.LanguageServer", Assert.Single(roslyn.Alternatives).Command);
        Assert.Equal(["--features", "metadata-uris"], BuiltinLanguageServers.Find(CSharpServers.CSharpLsId)!.Definition.Args);

        var none = BuiltinLanguageServers.ReadChoice(null);
        Assert.True(BuiltinLanguageServers.EnabledByDefault(CSharpServers.RoslynId, none)); // Roslyn is C#'s default (docs/CSHARP.md §3)
        Assert.False(BuiltinLanguageServers.EnabledByDefault(CSharpServers.CSharpLsId, none));
        var chosen = BuiltinLanguageServers.ReadChoice(JsonSerializer.SerializeToElement(new { csharp = "csharp" }));
        Assert.False(BuiltinLanguageServers.EnabledByDefault(CSharpServers.RoslynId, chosen));
        Assert.True(BuiltinLanguageServers.EnabledByDefault(CSharpServers.CSharpLsId, chosen));
        // a choice that names no member of the group falls back to the default
        var bogus = BuiltinLanguageServers.ReadChoice(JsonSerializer.SerializeToElement(new { csharp = "omnisharp" }));
        Assert.True(BuiltinLanguageServers.EnabledByDefault(CSharpServers.RoslynId, bogus));
        Assert.False(BuiltinLanguageServers.EnabledByDefault(CSharpServers.CSharpLsId, bogus));
        Assert.True(BuiltinLanguageServers.EnabledByDefault("rust-analyzer", chosen));
    }

    [Fact]
    public void An_explicit_enabled_setting_still_wins_over_the_group_choice()
    {
        var choice = BuiltinLanguageServers.ReadChoice(null);
        var effective = DefinitionOverrides.Apply(BuiltinLanguageServers.All,
            JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["csharp"] = new { enabled = true }, ["roslyn"] = new { args = new[] { "--stdio" } } }),
            id => BuiltinLanguageServers.EnabledByDefault(id, choice));
        Assert.True(effective["csharp"].Enabled);
        Assert.True(effective["roslyn"].Enabled); // tuned, not switched off: keeps its default
    }

    [Fact]
    public void The_roslyn_arguments_expand_the_logs_directory_and_cscodes_process_id()
    {
        var args = LaunchVariables.ExpandArguments(BuiltinLanguageServers.RoslynArgs, "/w", "/home/me/.cscode/logs/");
        Assert.Contains("/home/me/.cscode/logs/roslyn", args);
        Assert.Contains(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), args);
        Assert.Contains(Path.Combine(Path.GetTempPath(), "cscode-logs") + "/roslyn", LaunchVariables.ExpandArguments(BuiltinLanguageServers.RoslynArgs, "/w", null));
    }

    [Fact]
    public async Task The_fallback_roslyn_command_gets_the_expanded_arguments_too()
    {
        // roslyn-language-server missing, Microsoft.CodeAnalysis.LanguageServer (Nix's roslyn-ls) found: it was started with a literal
        // ${cscodePid}, printed its usage on stdout and exited
        await using var env = new LspTestEnv(builtins: true);
        env.Open("a.cs", "class A { }", language: "csharp");
        await env.Connections.WaitForServerAsync();
        var request = env.Connections.Requests.Single(r => r.Definition.Id == CSharpServers.RoslynId);
        var args = Assert.Single(request.Alternatives!).Args;
        Assert.DoesNotContain(args, a => a.Contains("${", StringComparison.Ordinal));
        Assert.Contains(Path.Combine(env.Paths.LogsDir, "roslyn"), args);
        Assert.Contains(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), args);
        Assert.Equal(request.Definition.Args, args);
    }

    [Fact]
    public void Library_and_generated_locations_are_recognised_per_server_and_named_after_the_type()
    {
        var csharpLs = new CSharpLsMetadataSource();
        Assert.Equal((VirtualDocumentKinds.Decompiled, "String.cs"), csharpLs.Classify("csharp:/w/App/App.csproj/decompiled/System.String.cs"));
        Assert.Null(csharpLs.Classify("file:///w/App/Program.cs"));

        var roslyn = new RoslynDocumentSource();
        Assert.Equal((VirtualDocumentKinds.Decompiled, "String.cs"),
            roslyn.Classify("file:///var/folders/x/T/MetadataAsSource/1228a553/DecompilationMetadataAsSourceFileProvider/d96b2bc1/String.cs"));
        Assert.Equal((VirtualDocumentKinds.Source, "JsonSerializer.cs"),
            roslyn.Classify("file:///tmp/MetadataAsSource/1/PdbSourceDocumentMetadataAsSourceFileProvider/2/JsonSerializer.cs"));
        Assert.Equal((VirtualDocumentKinds.Generated, "RegexGenerator.g.cs"),
            roslyn.Classify("roslyn-source-generated://4692038f/RegexGenerator.g.cs?documentId=4ed6&hintName=RegexGenerator.g.cs"));
        Assert.Null(roslyn.Classify("file:///w/App/Program.cs"));

        Assert.Equal("List.cs", CSharpServers.TypeFileName("System.Collections.Generic.List`1.cs"));
        Assert.Equal("Program.cs", CSharpServers.TypeFileName("Program.cs"));
    }

    [Fact]
    public void Roslyn_opens_the_one_solution_at_its_root_or_the_projects_below_it()
    {
        using var dir = new CsCode.TestDoubles.TempDirectory();
        dir.WriteFile("src/App/App.csproj", "<Project />");
        dir.WriteFile("src/App/obj/Generated.csproj", "<Project />"); // build output: skipped
        dir.WriteFile("tests/App.Tests/App.Tests.csproj", "<Project />");
        Assert.Empty(RoslynSession.FindSolutions(dir.Path));
        Assert.Equal([dir.Combine("src", "App", "App.csproj"), dir.Combine("tests", "App.Tests", "App.Tests.csproj")], RoslynSession.FindProjects(dir.Path));
        dir.WriteFile("App.slnx", "<Solution />");
        dir.WriteFile("Other.sln", "");
        Assert.Equal([dir.Combine("App.slnx"), dir.Combine("Other.sln")], RoslynSession.FindSolutions(dir.Path));
    }

    [Fact]
    public void Organize_usings_spans_the_using_directives_and_not_using_statements()
    {
        var text = string.Join('\n',
            "// header",
            "global using System;",
            "using static System.Math;",
            "using Ints = System.Collections.Generic.List<int>;",
            "using global::System.Text;",
            "",
            "namespace Sample",
            "{",
            "    using System.Linq;",
            "    class C",
            "    {",
            "        void M()",
            "        {",
            "            using var a = Open();",
            "            using (var b = Open()) { }",
            "            using System.IO.Stream c = Open();",
            "        }",
            "    }",
            "}");
        Assert.Equal(new TextRange(new TextPosition(1, 0), new TextPosition(8, 22)), RoslynSourceActions.UsingsRange(text));
        Assert.Null(RoslynSourceActions.UsingsRange("namespace Sample;\nclass C { void M() { using var s = Open(); } }\n"));
    }

    [Fact]
    public void Fix_all_takes_analyzer_warnings_and_errors_in_document_order_one_id_at_a_time()
    {
        Assert.True(RoslynSourceActions.IsTarget(DiagnosticSeverity.Warning, "IDE0090"));
        Assert.True(RoslynSourceActions.IsTarget(DiagnosticSeverity.Error, "CA1822"));
        Assert.False(RoslynSourceActions.IsTarget(DiagnosticSeverity.Information, "IDE0028")); // a suggestion stays one
        Assert.False(RoslynSourceActions.IsTarget(DiagnosticSeverity.Warning, "CS0219"));      // the compiler's own, as dotnet format leaves them
        Assert.False(RoslynSourceActions.IsTarget(DiagnosticSeverity.Warning, "BC42024"));
        Assert.False(RoslynSourceActions.IsTarget(DiagnosticSeverity.Warning, null));

        var report = JsonDocument.Parse("""
            {"kind":"full","items":[
              {"range":{"start":{"line":9,"character":4},"end":{"line":9,"character":8}},"severity":2,"code":"IDE0059","message":"a"},
              {"range":{"start":{"line":3,"character":20},"end":{"line":3,"character":23}},"severity":2,"code":"IDE0090","message":"b"},
              {"range":{"start":{"line":2,"character":1},"end":{"line":2,"character":2}},"severity":3,"code":"IDE0028","message":"c"},
              {"range":{"start":{"line":1,"character":0},"end":{"line":1,"character":2}},"severity":2,"code":"CS0219","message":"d"},
              {"range":{"start":{"line":3,"character":2},"end":{"line":3,"character":5}},"severity":2,"code":"IDE0090","message":"e"}]}
            """).RootElement;
        var first = RoslynSourceActions.NextTarget(report, new HashSet<string>())!.Value;
        Assert.Equal(("IDE0090", "e"), (first.Code, first.Diagnostic.GetProperty("message").GetString()));
        Assert.Equal("IDE0059", RoslynSourceActions.NextTarget(report, new HashSet<string> { "IDE0090" })!.Value.Code);
        Assert.Null(RoslynSourceActions.NextTarget(report, new HashSet<string> { "IDE0090", "IDE0059" }));
        Assert.Null(RoslynSourceActions.NextTarget(null, new HashSet<string>()));
    }

    [Fact]
    public void The_source_actions_answer_their_own_kind_and_every_kind_above_it()
    {
        Assert.True(RoslynSourceActions.Asks(["source"], RoslynSourceActions.OrganizeKind));
        Assert.True(RoslynSourceActions.Asks(["source.organizeImports"], RoslynSourceActions.OrganizeKind));
        Assert.True(RoslynSourceActions.Asks(["source.fixAll.roslyn"], RoslynSourceActions.FixAllKind));
        Assert.False(RoslynSourceActions.Asks(["source.fixAll.ruff"], RoslynSourceActions.FixAllKind));
        Assert.False(RoslynSourceActions.Asks(["quickfix"], RoslynSourceActions.OrganizeKind));
        Assert.False(RoslynSourceActions.Asks(["source.organize"], RoslynSourceActions.OrganizeKind));
    }

    [Fact]
    public async Task A_servers_library_locations_become_bounded_virtual_documents_served_by_that_server()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync();
        var external = env.Manager.External;
        external.AddSource("fake", new FakeSource());
        var client = Assert.Single(env.Manager.RunningClients("fake"));

        var uri = external.ToVirtual(client, "fake-lib:/System.String.cs")!;
        Assert.Equal("String.cs [decompiled]", VirtualDocumentUri.Title(uri));
        Assert.Same(uri, external.ToVirtual(client, "fake-lib:/System.String.cs")); // one name per location
        Assert.Null(external.ToVirtual(client, doc.Uri.AbsoluteUri));               // a workspace file stays what it is
        Assert.Equal("fake-lib:/System.String.cs", external.ServerUriOf(uri));
        Assert.Equal("text of fake-lib:/System.String.cs", (await env.VirtualDocuments.GetAsync(uri, TestContext.Current.CancellationToken))!.Text);

        for (var i = 0; i < ExternalDocuments.MaxEntries + 10; i++) external.ToVirtual(client, $"fake-lib:/T{i}.cs");
        Assert.Equal(ExternalDocuments.MaxEntries, external.Count);
        Assert.Null(external.Find(uri)); // the oldest name fell out: no longer synced to its server (an open view keeps its text)
        Assert.Null(external.ServerUriOf(uri));
    }

    private sealed class FakeSource : IExternalDocumentSource
    {
        public (string Kind, string FileName)? Classify(string serverUri) =>
            serverUri.StartsWith("fake-lib:", StringComparison.Ordinal) ? (VirtualDocumentKinds.Decompiled, CSharpServers.TypeFileName(ExternalDocuments.LastSegment(serverUri))) : null;

        public ValueTask<string?> FetchAsync(Client.LanguageClient client, string serverUri, CancellationToken ct) => ValueTask.FromResult<string?>("text of " + serverUri);
    }
}
