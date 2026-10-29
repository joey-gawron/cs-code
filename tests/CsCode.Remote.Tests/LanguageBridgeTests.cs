using System.Text;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Remote.Bridge;
using CsCode.Remote.Client;
using CsCode.Remote.Protocol;
using CsCode.Server;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Remote.Tests;

/// <summary>R2 through a real server: document sync, the language bridge against the REAL typescript-language-server, diagnostics mirror, settings mirror.</summary>
public class LanguageBridgeTests
{
    private static CancellationToken Ct => TestEnv.Ct;

    internal static string? FindOnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);

    internal static bool RequireTypeScript()
    {
        if (FindOnPath("typescript-language-server") is null) { Assert.Skip("typescript-language-server is not on PATH (the server resolves the built-in definition there)"); return false; }
        if (FindOnPath("node") is null) { Assert.Skip("node is not on PATH"); return false; }
        return true;
    }

    internal const string UtilTs = "export function greet(name: string): string {\n  return \"hello \" + name;\n}\n";
    internal const string MainTs = "import { greet } from \"./util\";\n\nconst count: number = \"oops\";\nconst message = greet(\"world\");\nconsole.log(message.toUpperCase(), count);\n";

    internal static void WriteProject(TempDirectory dir)
    {
        dir.WriteFile("tsconfig.json", "{\"compilerOptions\":{\"strict\":true,\"target\":\"es2020\",\"module\":\"commonjs\",\"noEmit\":true}}");
        dir.WriteFile("package.json", "{\"name\":\"cscode-remote-it\",\"version\":\"1.0.0\"}");
        dir.WriteFile("src/util.ts", UtilTs);
        dir.WriteFile("src/main.ts", MainTs);
    }

    private static async Task<T> UntilAsync<T>(Func<Task<T?>> query, string what, Func<T, bool>? accept = null, int seconds = 90) where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            var r = await query();
            if (r is not null && (accept?.Invoke(r) ?? true)) return r;
            await Task.Delay(300, Ct);
        }
        throw new TimeoutException("no usable answer through the bridge for: " + what);
    }

    [Fact]
    public async Task TypeScript_diagnostics_completion_hover_and_definition_cross_the_bridge()
    {
        if (!RequireTypeScript()) return;
        await using var host = new RemoteTestHost();
        WriteProject(host.Workspace);
        await host.ConnectAsync();
        await host.OpenWorkspaceAsync();

        var documents = host.Services.GetRequiredService<IDocumentManager>();
        var diagnostics = host.Services.GetRequiredService<IDiagnosticService>();
        var registry = host.Services.GetRequiredService<ILanguageFeatureRegistry>();
        var localManager = host.Services.GetRequiredService<ILanguageServerManager>(); // the in-process client, observing documents from now on
        var util = new InMemoryTextDocument(host.Remote("src/util.ts"), "typescript", UtilTs);
        var main = new InMemoryTextDocument(host.Remote("src/main.ts"), "typescript", MainTs);
        using var u = documents.Register(util); // what the editor does when it opens a tab
        using var m = documents.Register(main);

        // diagnostics: published on the server, mirrored under the connection's owner with the client's ssh: URI
        await TestEnv.WaitUntilAsync(() => diagnostics.Get(main.Uri).Any(d => d.Code == "2322"), TimeSpan.FromSeconds(120), "TS2322 through the mirror\n" + host.Dump());
        var error = diagnostics.Get(main.Uri).First(d => d.Code == "2322");
        Assert.Equal(2, error.Range.Start.Line);
        Assert.Contains(diagnostics.GetAll(), e => e.Uri == main.Uri); // the Error List sees the remote file

        // providers mirrored into the local registry, restricted to ssh: documents
        Assert.NotEmpty(registry.GetProviders<ICompletionProvider>(main));
        Assert.Empty(registry.GetProviders<ICompletionProvider>(new Uri("file:///local/main.ts"), "typescript"));

        // edits sync incrementally (with the editor's versions) and completion sees them
        main.ApplyEdits([TextEdit.Insert(main.PositionAt(main.Length), "\nmessage.")]);
        var end = main.PositionAt(main.Length);
        var completion = registry.GetProviders<ICompletionProvider>(main)[0];
        Assert.Contains(".", completion.TriggerCharacters);
        var list = await UntilAsync(async () => await completion.ProvideCompletionAsync(main, end, new CompletionContext(CompletionTriggerKind.TriggerCharacter, "."), Ct),
            "completion after message.", l => l.Items.Any(i => i.Label == "toUpperCase"));
        var item = list.Items.First(i => i.Label == "toUpperCase");
        var resolved = await completion.ResolveCompletionAsync(item, Ct); // resolved by the server's original instance
        Assert.Equal("toUpperCase", resolved.Label);

        var hover = await UntilAsync(async () => await registry.GetProviders<IHoverProvider>(main)[0].ProvideHoverAsync(main, new TextPosition(3, 18), Ct), "hover on greet");
        Assert.Contains("greet", string.Concat(hover.Contents.Select(c => c.Value)));

        var definition = await UntilAsync(async () => await registry.GetProviders<IDefinitionProvider>(main)[0].ProvideDefinitionAsync(main, new TextPosition(3, 18), DefinitionKind.Definition, Ct),
            "definition of greet", l => l.Count > 0);
        Assert.Equal(util.Uri, definition[0].TargetUri); // the server's file: URI came back as the client's ssh: URI
        Assert.Empty(localManager.Servers); // ssh: documents never start a language server on this machine

        // fixing the error clears the mirrored diagnostic
        var oops = main.GetText().IndexOf("\"oops\"", StringComparison.Ordinal);
        main.ApplyEdits([new TextEdit(new TextRange(main.PositionAt(oops), main.PositionAt(oops + 6)), "5")]);
        await TestEnv.WaitUntilAsync(() => !diagnostics.Get(main.Uri).Any(d => d.Code == "2322"), TimeSpan.FromSeconds(60), "diagnostic cleared after the fix");

        // the connection going away takes its providers and diagnostics with it
        main.ApplyEdits([new TextEdit(new TextRange(main.PositionAt(oops), main.PositionAt(oops + 1)), "\"again\"")]);
        await TestEnv.WaitUntilAsync(() => diagnostics.Get(main.Uri).Any(d => d.Code == "2322"), TimeSpan.FromSeconds(60), "diagnostic back");
        await host.Manager.DisconnectAsync(Ct);
        Assert.Empty(registry.GetProviders<ICompletionProvider>(main));
        Assert.Empty(diagnostics.Get(main.Uri));
    }

    [Fact]
    public async Task The_users_settings_are_mirrored_to_the_server()
    {
        await using var host = new RemoteTestHost();
        var userSettings = Path.Combine(host.ClientHome, "config", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(userSettings)!);
        File.WriteAllText(userSettings, "{ \"editor.tabSize\": 3 }");
        await host.ConnectAsync();
        var serverSettings = Path.Combine(host.ServerDataDir, "config", "settings.json");
        Assert.Equal("{ \"editor.tabSize\": 3 }", File.ReadAllText(serverSettings));

        File.WriteAllText(userSettings, "{ \"editor.tabSize\": 7, \"files.exclude\": { \"**/out\": true } }");
        await TestEnv.WaitUntilAsync(() => File.Exists(serverSettings) && File.ReadAllText(serverSettings).Contains("\"editor.tabSize\": 7"),
            TimeSpan.FromSeconds(20), "settings change mirrored");
    }

    [Fact]
    public async Task Open_documents_resync_in_full_after_the_server_restarts()
    {
        await using var host = new RemoteTestHost();
        await host.ConnectAsync();
        var documents = host.Services.GetRequiredService<IDocumentManager>();
        var doc = new InMemoryTextDocument(host.Remote("a.txt"), "plaintext", "one");
        using var reg = documents.Register(doc);
        var local = new InMemoryTextDocument(new Uri("file:///tmp/not-remote.txt"), "plaintext", "x");
        using var reg2 = documents.Register(local); // a local document is never sent to the remote
        doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 3), " two")]);
        var status = await TestEnv.WaitForAsync(() =>
        {
            var s = host.Manager.Session!.Peer.InvokeAsync<Empty, ServerStatus>(RemoteMethods.Status, Empty.Value, Ct).GetAwaiter().GetResult()!;
            return s.Documents.Count == 1 && s.Documents[0].Version == doc.Version ? s : null;
        }, TimeSpan.FromSeconds(10), "the document on the server");
        Assert.Equal(doc.Uri, status.Documents[0].Uri);
        Assert.Equal("one two".Length, status.Documents[0].Length);

        var firstServer = host.Manager.Session!.Server.ProcessId;
        System.Diagnostics.Process.GetProcessById(firstServer).Kill(entireProcessTree: true); // the server dies
        await TestEnv.WaitUntilAsync(() => host.Manager.Session is { IsOpen: true } s && s.Server.ProcessId != firstServer, TimeSpan.FromSeconds(30), "reconnect to a new server");
        Assert.Equal(Core.Remote.RemoteConnectionState.Connected, host.Manager.Status.State);
        Assert.Contains(host.Output.Text(RemoteConnectionManager.OutputChannel).Split('\n'), l => l.Contains("[Reconnecting]"));

        // the new server got the document in full, at the editor's version, and follows later edits
        doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 0), "zero ")]);
        var after = await TestEnv.WaitForAsync(() =>
        {
            var s = host.Manager.Session!.Peer.InvokeAsync<Empty, ServerStatus>(RemoteMethods.Status, Empty.Value, Ct).GetAwaiter().GetResult()!;
            return s.Documents.Count == 1 && s.Documents[0].Version == doc.Version ? s : null;
        }, TimeSpan.FromSeconds(10), "the document resynced on the new server");
        Assert.Equal("zero one two".Length, after.Documents[0].Length);

        // closing the editor closes it on the server
        reg.Dispose();
        await TestEnv.WaitForAsync(() =>
        {
            var s = host.Manager.Session!.Peer.InvokeAsync<Empty, ServerStatus>(RemoteMethods.Status, Empty.Value, Ct).GetAwaiter().GetResult()!;
            return s.Documents.Count == 0 ? s : null;
        }, TimeSpan.FromSeconds(10), "the document closed on the server");
    }
}

public class UiForwardingTests
{
    private static CancellationToken Ct => TestEnv.Ct;

    [Fact]
    public async Task Messages_quick_picks_input_boxes_progress_navigation_and_output_are_forwarded()
    {
        var mapper = new SshUriMapper(new RemoteAuthority("box"));
        using var pair = new PeerPair(new RpcPeerOptions { Name = "client", Json = RemoteJson.Create(mapper) }, new RpcPeerOptions { Name = "server", Json = RemoteJson.Create() });
        var window = new RecordingWindowService();
        var docs = new DocumentManager();
        var nav = new RecordingNavigationService(docs);
        var output = new InMemoryOutputService();
        new UiForwarding(() => window, () => nav, () => output, "Remote: ").Attach(pair.A);
        var serverWindow = new ClientWindowService(pair.B);
        var serverNav = new ClientNavigationService(pair.B);
        using var serverOutput = new ForwardingOutputService(pair.B);
        pair.Start();

        window.MessageActionResponses.Enqueue("Restart");
        Assert.Equal("Restart", await serverWindow.ShowMessageAsync(MessageSeverity.Error, "server crashed", [new MessageAction("Restart"), new MessageAction("Close", true)], Ct));
        Assert.Contains((MessageSeverity.Error, "server crashed"), window.Messages);

        window.QuickPickResponses.Enqueue(1);
        var tagged = new QuickPickItem("b", Tag: new object());
        var picked = await serverWindow.ShowQuickPickAsync([new QuickPickItem("a"), tagged], new QuickPickOptions("Pick"), Ct);
        Assert.Same(tagged, picked); // the server's own item (with its Tag) comes back

        window.InputResponses.Enqueue("bad");
        window.InputResponses.Enqueue("good");
        var answer = await serverWindow.ShowInputBoxAsync(new InputBoxOptions(Prompt: "name", Validate: v => v == "good" ? null : "must be good"), Ct);
        Assert.Equal("good", answer); // Validate cannot cross the wire: the server re-asks with the error as prompt

        var progress = await serverWindow.WithProgressAsync("Indexing", true, async (r, ct) => { r.Report("half", 50); await Task.Delay(50, ct); return 42; });
        Assert.Equal(42, progress);

        var target = new Uri("file:///home/me/src/a%20b.ts");
        await serverNav.OpenAsync(target, TextRange.At(3, 4), ct: Ct);
        Assert.Equal(mapper.FromRemotePath("/home/me/src/a b.ts"), nav.Opened.Single().Uri); // mapped to the client's ssh: URI
        var applied = await serverNav.ApplyWorkspaceEditAsync(new WorkspaceEdit(new Dictionary<Uri, IReadOnlyList<TextEdit>> { [target] = [TextEdit.Insert(TextPosition.Zero, "x")] }), "Rename", Ct);
        Assert.True(applied);
        Assert.Equal(mapper.FromRemotePath("/home/me/src/a b.ts"), nav.AppliedEdits.Single().Changes.Keys.Single());

        serverOutput.GetOrCreate("TypeScript (LSP)").AppendLine("server log line");
        await TestEnv.WaitUntilAsync(() => output.Text("Remote: TypeScript (LSP)").Contains("server log line"), TimeSpan.FromSeconds(5), "output forwarded");
    }
}

public class MirrorDocumentTests
{
    [Fact]
    public void Client_changes_applied_in_order_give_the_same_text_and_versions_as_the_editor()
    {
        var rng = new Random(42);
        var editor = new InMemoryTextDocument(new Uri("file:///x.ts"), "typescript", "line one\nline two\r\nline three\n");
        var mirror = new MirrorDocument(editor.Uri, "typescript", editor.Version, editor.GetText());
        var events = new List<TextDocumentChangedEventArgs>();
        using var sub = editor.Changed.Subscribe(events.Add);
        for (var i = 0; i < 500; i++)
        {
            var len = editor.Length;
            var a = rng.Next(0, len + 1);
            var b = Math.Min(len, a + rng.Next(0, 6));
            var text = rng.Next(4) switch { 0 => "", 1 => "\n", 2 => "x\r\ny", _ => "abc" };
            editor.ApplyEdits([new TextEdit(new TextRange(editor.PositionAt(a), editor.PositionAt(b)), text)]);
        }
        foreach (var e in events) Assert.True(mirror.ApplyClientChanges(e.Version, e.Changes));
        Assert.Equal(editor.GetText(), mirror.GetText());
        Assert.Equal(editor.Version, mirror.Version);
        Assert.Equal(editor.LineCount, mirror.LineCount);
        for (var l = 0; l < editor.LineCount; l++) Assert.Equal(editor.GetLineText(l), mirror.GetLineText(l));
        // a duplicate (older version) is ignored; edits from the server side are refused
        Assert.True(mirror.ApplyClientChanges(1, [new TextContentChange(TextRange.At(0, 0), 0, 0, "dup")]));
        Assert.Equal(editor.GetText(), mirror.GetText());
        Assert.False(mirror.ApplyEdits([TextEdit.Insert(TextPosition.Zero, "x")]));
    }
}
