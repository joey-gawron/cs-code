using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CsCode.App.Docking;
using CsCode.Core.Debugging;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Remote;
using CsCode.Core.Terminal;
using CsCode.Core.Text;
using CsCode.Profiling.Testing;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.MemoryTests.App;

/// <summary>
/// Files for the app scenarios: real sources of this repository when it is reachable (C#, Markdown, JSON, shell — what a user opens), generated
/// text otherwise. <see cref="Big"/> concatenates sources into one large C# file for scrolling.
/// </summary>
public static class AppWorkspace
{
    public static string? RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "CsCode.slnx"))) return dir.FullName;
        return null;
    }

    /// <summary>Copies <paramref name="count"/> files into <paramref name="files"/> (flat, distinct names) and returns their paths.</summary>
    public static IReadOnlyList<string> Seed(TempDirectory files, int count = 20)
    {
        var sources = new List<string>();
        if (RepoRoot() is { } root)
        {
            sources.AddRange(Directory.EnumerateFiles(Path.Combine(root, "src", "CsCode.Core"), "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Take(count - 6));
            sources.AddRange(Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md").Order(StringComparer.Ordinal).Take(3));
            sources.AddRange(new[] { "global.json", "src/CsCode.Profiling/runtimeconfig.recommended.json", "tools/nix-dev-env.sh" }.Select(p => Path.Combine(root, p)).Where(File.Exists));
        }
        var paths = new List<string>();
        for (var i = 0; i < count; i++)
        {
            if (i < sources.Count) paths.Add(files.WriteFile($"f{i:D2}-{Path.GetFileName(sources[i])}", File.ReadAllText(sources[i])));
            else paths.Add(files.WriteFile($"f{i:D2}-generated.cs", Generated(400, i)));
        }
        return paths;
    }

    /// <summary>A C# file of about <paramref name="lines"/> lines (repo sources repeated, or generated).</summary>
    public static string Big(TempDirectory files, int lines = 20_000)
    {
        var sb = new System.Text.StringBuilder();
        var sources = RepoRoot() is { } root ? Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray() : [];
        var count = 0;
        for (var i = 0; count < lines; i++)
        {
            var text = sources.Length > 0 ? File.ReadAllText(sources[i % sources.Length]) : Generated(500, i);
            sb.Append(text).Append('\n');
            count += text.Count(c => c == '\n') + 1;
        }
        return files.WriteFile("big.cs", sb.ToString());
    }

    private static string Generated(int lines, int seed)
    {
        var sb = new System.Text.StringBuilder($"namespace Generated{seed};\n\npublic static class C{seed}\n{{\n");
        for (var i = 0; i < lines; i++) sb.Append("    public static int M").Append(i).Append("(int x) => x * ").Append(i + seed).Append(" + \"text\".Length; // line ").Append(i).Append('\n');
        return sb.Append("}\n").ToString();
    }
}

/// <summary>Base of the app scenarios: the host, settle helpers, closing every tab without prompts.</summary>
public abstract class AppScenario(MemoryAppHost host, string name) : IScenario
{
    protected MemoryAppHost Host { get; } = host;
    public string Name { get; } = name;

    public virtual Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
    public abstract Task RunIterationAsync(int iteration, CancellationToken ct);
    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    protected Task<IEditorDocument?> OpenAsync(string path) => Host.Get<INavigationService>().OpenAsync(new Uri(path)).AsTask();

    protected async Task CloseAllAsync()
    {
        var well = Host.Shell.Well;
        foreach (var tab in well.Tabs.ToArray()) await well.CloseAsync(tab, prompt: false);
        await MemoryAppHost.SettleAsync(3);
    }

    protected static async Task Require(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        if (!await MemoryAppHost.WaitFor(condition, timeoutMs)) throw new InvalidOperationException("scenario step timed out: " + what);
    }
}

/// <summary>Open 20 documents (C#, Markdown, JSON, shell), then close them all.</summary>
public sealed class TabsScenario(MemoryAppHost host, IReadOnlyList<string> files) : AppScenario(host, "tabs-open-close")
{
    public override async Task RunIterationAsync(int iteration, CancellationToken ct)
    {
        foreach (var f in files) await OpenAsync(f);
        await MemoryAppHost.SettleAsync(3);
        await CloseAllAsync();
    }
}

/// <summary>Scroll a 20k-line file to the end and back, a page per frame.</summary>
public sealed class ScrollScenario(MemoryAppHost host, string bigFile) : AppScenario(host, "scroll-big-file")
{
    private readonly Uri _uri = new(bigFile);

    public override Task InitializeAsync(CancellationToken ct) => EditorAsync();

    /// <summary>The big file's editor, (re)opened when another scenario closed it in the meantime.</summary>
    private async Task<AvaloniaEdit.TextEditor> EditorAsync()
    {
        await OpenAsync(bigFile);
        AvaloniaEdit.TextEditor? editor = null;
        await Require(() => (editor = Host.Shell.Well.EditorTabs.FirstOrDefault(t => t.Uri == _uri)?.LiveView?.GetVisualDescendants()
            .OfType<AvaloniaEdit.TextEditor>().FirstOrDefault()) is { Document: not null }, "the editor view");
        return editor!;
    }

    public override async Task RunIterationAsync(int iteration, CancellationToken ct)
    {
        var editor = await EditorAsync();
        var lines = editor.Document.LineCount;
        for (var line = 1; line < lines; line += Math.Max(1, lines / 40)) { editor.ScrollToLine(line); await MemoryAppHost.SettleAsync(1); }
        for (var line = lines; line > 1; line -= Math.Max(1, lines / 40)) { editor.ScrollToLine(line); await MemoryAppHost.SettleAsync(1); }
        editor.ScrollToLine(1);
    }

    public override async ValueTask DisposeAsync() => await CloseAllAsync();
}

/// <summary>Open a file, type 100 characters as the platform delivers them (TextInput as dispatcher jobs), close it discarding the edits.</summary>
public sealed class TypingScenario(MemoryAppHost host, string file) : AppScenario(host, "typing")
{
    public override async Task RunIterationAsync(int iteration, CancellationToken ct)
    {
        await OpenAsync(file);
        var area = await TextAreaAsync(Host);
        for (var i = 0; i < 100; i++)
        {
            var ch = (char)('a' + i % 26);
            Dispatcher.UIThread.Post(() => area.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = ch.ToString() }), DispatcherPriority.Input);
            if (i % 10 == 9) await MemoryAppHost.SettleAsync(1);
        }
        await MemoryAppHost.SettleAsync(3);
        await CloseAllAsync();
    }

    public static async Task<AvaloniaEdit.Editing.TextArea> TextAreaAsync(MemoryAppHost host)
    {
        AvaloniaEdit.Editing.TextArea? area = null;
        await Require(() => (area = host.Shell.Well.EditorTabs.LastOrDefault()?.LiveView?.GetVisualDescendants().OfType<AvaloniaEdit.Editing.TextArea>().FirstOrDefault()) is not null, "the text area");
        area!.Focus();
        await Require(() => area.IsKeyboardFocusWithin, "editor focus");
        return area;
    }
}

/// <summary>Switch the colour theme through the setting a user changes (dark → light → blue → dark).</summary>
public sealed class ThemeScenario(MemoryAppHost host) : AppScenario(host, "theme-switch")
{
    private static readonly string[] Themes = ["vs2022-light", "vs2022-blue", "vs2022-dark"];

    public override async Task RunIterationAsync(int iteration, CancellationToken ct)
    {
        var config = Host.Get<IConfigurationService>();
        foreach (var id in Themes)
        {
            await config.UpdateAsync(SettingKeys.WorkbenchColorTheme, id, ct: ct);
            await Require(() => Host.Shell.Theme?.AppliedThemeId == id, "theme " + id);
            await MemoryAppHost.SettleAsync(2);
        }
    }
}

/// <summary>Show and hide the tool windows (Output, Error List, Find Results, Explorer).</summary>
public sealed class ToolWindowScenario(MemoryAppHost host) : AppScenario(host, "tool-windows")
{
    private static readonly string[] Ids = ["output", "errorlist", "findresults", "explorer"];

    public override async Task RunIterationAsync(int iteration, CancellationToken ct)
    {
        var tools = Host.Shell.Tools;
        foreach (var id in Ids)
        {
            tools.Show(id);
            await MemoryAppHost.SettleAsync(2);
            tools.Hide(id);
            await MemoryAppHost.SettleAsync(1);
        }
    }
}

/// <summary>
/// A language server's lifecycle: open a file it serves (it starts), close the file, stop the server. The minimal stdio server below (python3)
/// answers initialize/shutdown/exit and logs nothing, so the scenario measures the client side; a budget run uses a real server instead.
/// </summary>
public sealed class LanguageServerScenario(MemoryAppHost host, string serverId, string file) : AppScenario(host, "language-server-" + serverId)
{
    public const string FakeId = "memfake";
    public const string FakeExtension = ".memfake";

    public const string FakeServerScript = """
        import json, sys
        inp, out = sys.stdin.buffer, sys.stdout.buffer
        def read():
            length = None
            while True:
                line = inp.readline()
                if not line:
                    return None
                line = line.strip()
                if not line:
                    if length is not None:
                        break
                    continue
                key, _, value = line.decode('ascii').partition(':')
                if key.strip().lower() == 'content-length':
                    length = int(value)
            return json.loads(inp.read(length))
        def send(obj):
            body = json.dumps(obj).encode()
            out.write(b'Content-Length: ' + str(len(body)).encode() + b'\r\n\r\n' + body)
            out.flush()
        while True:
            msg = read()
            if msg is None:
                sys.exit(0)
            method = msg.get('method')
            if method == 'initialize':
                send({'jsonrpc': '2.0', 'id': msg['id'], 'result': {'capabilities': {'textDocumentSync': 1}}})
            elif method == 'shutdown':
                send({'jsonrpc': '2.0', 'id': msg['id'], 'result': None})
            elif method == 'exit':
                sys.exit(0)
        """;

    /// <summary>The user settings that define the fake server for <c>*.memfake</c> files.</summary>
    public static Dictionary<string, object?> FakeServerSettings(string python, string script) => new()
    {
        ["cscode.languageServers"] = new Dictionary<string, object>
        {
            [FakeId] = new { command = python, args = new[] { script }, selector = new[] { new { pattern = "**/*" + FakeExtension } } },
        },
    };

    public override async Task RunIterationAsync(int iteration, CancellationToken ct)
    {
        var manager = Host.Get<ILanguageServerManager>();
        await OpenAsync(file);
        // The first open starts it; after an explicit stop the manager waits for an explicit start (as VS Code's "restart server").
        if (iteration > 0) await manager.StartAsync(serverId, ct);
        if (!await MemoryAppHost.WaitFor(() => manager.Servers.Any(s => s.Id == serverId && s.State == LanguageServerState.Running), 60_000))
            throw new InvalidOperationException($"{serverId} did not start: servers [{string.Join(", ", manager.Servers.Select(s => $"{s.Id}={s.State} {s.LastError}"))}], " +
                $"definitions [{string.Join(", ", manager.Definitions.Select(d => d.Id))}]");
        await CloseAllAsync();
        await manager.StopAsync(serverId, ct);
        await Require(() => !manager.Servers.Any(s => s.Id == serverId && s.State is LanguageServerState.Running or LanguageServerState.Starting or LanguageServerState.Stopping),
            serverId + " stopped", 30_000);
    }
}

/// <summary>Create a terminal (the sandboxed bash), run a command, kill it.</summary>
public sealed class TerminalScenario(MemoryAppHost host) : AppScenario(host, "terminal")
{
    public override async Task RunIterationAsync(int iteration, CancellationToken ct)
    {
        var terminals = Host.Get<ITerminalService>();
        var t = await terminals.CreateAsync(ct: ct);
        await Require(() => t.ProcessId is not null, "the shell");
        await t.SendTextAsync("echo memory-scenario-" + iteration, ct: ct);
        await MemoryAppHost.SettleAsync(5);
        await terminals.KillAsync(t, ct);
        await Require(() => !terminals.Terminals.Contains(t), "the terminal removed", 20_000);
    }
}

/// <summary>
/// A debug session with the real js-debug adapter and node: set a breakpoint, F5, stop at it, Shift+F5. Needs node and js-debug
/// (<c>CSCODE_JSDEBUG</c> or tools/fetch-js-debug.sh); <see cref="SkipReason"/> says what is missing.
/// </summary>
public sealed class DebugScenario(MemoryAppHost host) : AppScenario(host, "debug-session")
{
    public const string Program = """
        let total = 0;
        for (let i = 0; i < 1000; i++) {
          total += i; // @loop
        }
        console.log("total " + total);
        """;

    public const string LaunchJson = """
        { "version": "0.2.0", "configurations": [ { "name": "Node: main", "type": "pwa-node", "request": "launch", "program": "${workspaceFolder}/main.js", "skipFiles": ["<node_internals>/**"] } ] }
        """;

    public static string? SkipReason() => JsDebugToolchain.FindNode() is null ? "node is not on PATH" : JsDebugToolchain.FindJsDebug() is null ? "js-debug not found (CSCODE_JSDEBUG)" : null;

    /// <summary>Seeds the workspace folder: main.js and a launch.json.</summary>
    public static void Seed(TempDirectory files)
    {
        files.WriteFile("main.js", Program);
        files.WriteFile(".vscode/launch.json", LaunchJson);
    }

    /// <summary>The settings pointing at js-debug (never the download offer).</summary>
    public static Dictionary<string, object?> Settings() => new() { [CsCode.Dap.Adapters.JsDebugAdapterFactory.SettingPath] = JsDebugToolchain.FindJsDebug() };

    public override async Task InitializeAsync(CancellationToken ct)
    {
        var debug = Host.Get<IDebugService>();
        await Require(() => debug.Configurations.Selected?.Name == "Node: main", "launch.json read");
        debug.Breakpoints.Add(new Uri(Host.Files.Combine("main.js")), 2);
    }

    public override async Task RunIterationAsync(int iteration, CancellationToken ct)
    {
        var debug = Host.Get<IDebugService>();
        var commands = Host.Get<ICommandService>();
        var start = commands.ExecuteAsync(KnownCommands.DebugStart).AsTask();
        await Require(() => debug.State == DebugState.Stopped && debug.StopLocation is not null, "stopped at the breakpoint", 90_000);
        await start;
        await MemoryAppHost.SettleAsync(5);
        await commands.ExecuteAsync(KnownCommands.DebugStop);
        await Require(() => debug.State == DebugState.Inactive && debug.Sessions.Count == 0, "debugging stopped", 30_000);
        await CloseAllAsync();
    }
}

/// <summary>
/// A remote session through the loopback transport (a real cscode-server child process behind an <c>ssh://</c> authority, no sshd): connect, open
/// a remote file, close it, disconnect.
/// </summary>
public sealed class RemoteScenario(MemoryAppHost host) : AppScenario(host, "remote-session")
{
    public static string ServerAssembly => Path.Combine(AppContext.BaseDirectory, CsCode.Remote.Transport.LocalServer.AssemblyFile);

    /// <summary>Registers the loopback transport (call from <see cref="MemoryAppHostOptions.Configure"/>).</summary>
    public static void Configure(MemoryAppHost host, IServiceCollection services, TempDirectory serverData, string marker)
    {
        var home = serverData.Combine("home");
        Directory.CreateDirectory(home);
        var launch = CsCode.Remote.Transport.LocalServer.Launch(ServerAssembly, serverData.Path, marker, environment: new Dictionary<string, string?>
        {
            ["HOME"] = home, ["XDG_CACHE_HOME"] = Path.Combine(home, ".cache"), ["XDG_CONFIG_HOME"] = Path.Combine(home, ".config"), ["SSH_AUTH_SOCK"] = null,
        });
        services.AddSingleton(new CsCode.Remote.RemoteOptions { TransportFactory = (_, _) => new CsCode.Remote.Transport.LoopbackTransport(launch), ServerMarker = marker });
    }

    public override async Task RunIterationAsync(int iteration, CancellationToken ct)
    {
        var manager = Host.Get<IRemoteConnectionManager>();
        await manager.ConnectAsync("tester@box", ct);
        await Require(() => manager.Status.IsConnected, "connected", 30_000);
        var file = Host.Files.WriteFile("remote.txt", "remote " + iteration + "\n");
        var uri = manager.ToUri(file) ?? throw new InvalidOperationException("no remote uri");
        await Host.Get<INavigationService>().OpenAsync(uri, ct: ct);
        await MemoryAppHost.SettleAsync(5);
        await CloseAllAsync();
        await manager.DisconnectAsync(ct);
        await Require(() => manager.Status.State == RemoteConnectionState.Disconnected, "disconnected", 30_000);
    }
}
