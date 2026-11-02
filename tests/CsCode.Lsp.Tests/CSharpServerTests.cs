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
