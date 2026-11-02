using System.Reflection;
using System.Text;
using System.Text.Json;
using CsCode.Core.Debugging;
using CsCode.Core.Languages;
using CsCode.Core.Tasks;
using CsCode.Core.Text;
using CsCode.Lsp.Extensions;
using CsCode.Lsp.Extensions.RustAnalyzer;
using CsCode.Lsp.Tests.Harness;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Lsp.Tests;

/// <summary>
/// rust-analyzer's extension against the real rust-analyzer (the one on PATH; 1.96.1 here) on a small cargo package: server status, every
/// command, hover actions, the Run/Debug lenses and runnables. Skips when rust-analyzer or cargo is missing. One server for the class.
/// </summary>
[Trait("Category", "RealTools")]
public sealed class RealRustAnalyzerTests(RealRustAnalyzerTests.Fixture f) : IClassFixture<RealRustAnalyzerTests.Fixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(120);

    public const string MainRs = """
        mod util; mod ssr;

        macro_rules! twice {
            ($e:expr) => { $e + $e };
        }

        fn main() {
            let v = vec![1, 2, 3];
            let x = twice!(v.len());
            println!("{} {}", x,
                util::helper());
        }
        """;

    public const string UtilRs = """
        pub fn helper() -> i32 {
            42
        }

        #[cfg(test)]
        mod tests {
            #[test]
            fn it_works() { assert_eq!(super::helper(), 42); }
        }
        """;

    public sealed class Fixture : IAsyncLifetime
    {
        public LspTestEnv Env { get; private set; } = null!;
        public InMemoryTextDocument Main { get; private set; } = null!;
        public InMemoryTextDocument Util { get; private set; } = null!;
        /// <summary>The module structural search replace edits (the other files stay as written for the other tests).</summary>
        public InMemoryTextDocument Ssr { get; private set; } = null!;
        public RecordingTerminals Terminals { get; } = new();
        public List<DebugConfiguration> Launched { get; } = [];
        public string? SkipReason { get; private set; }

        public async ValueTask InitializeAsync()
        {
            if (FindOnPath("rust-analyzer") is null || FindOnPath("cargo") is null)
            {
                SkipReason = "rust-analyzer or cargo is not on PATH";
                return;
            }
            Env = new LspTestEnv(c =>
            {
                c.Set(LspSettingKeys.InteractiveTimeoutMs, 30_000);
                c.Set(LspSettingKeys.BackgroundTimeoutMs, 60_000);
                c.Set(LspSettingKeys.InitializeTimeoutMs, 120_000);
                c.Set(LspSettingKeys.ShutdownTimeoutMs, 5_000);
            }, realServers: true);
            Env.TaskTerminals = Terminals;
            Env.Debug = RecordingDebug.Create(Launched);
            Env.Dir.WriteFile("Cargo.toml", "[package]\nname = \"demo\"\nversion = \"0.1.0\"\nedition = \"2021\"\n");
            Env.Dir.WriteFile("examples/hello.rs", "fn main() { println!(\"hello\"); }\n");
            Main = Env.Open("src/main.rs", MainRs, "rust");
            Util = Env.Open("src/util.rs", UtilRs, "rust");
            Ssr = Env.Open("src/ssr.rs", "pub fn f(a: i32) -> i32 { foo(a) + foo(2) }\nfn foo(a: i32) -> i32 { a }\nfn bar(a: i32) -> i32 { a }\n", "rust");
            // ready when rust-analyzer says it is quiescent (its experimental/serverStatus, shown in the status bar)
            await TestHelpers.EventuallyAsync(() => Env.StatusBar.Get(ServerStatusDisplay.ItemId)?.Text == "✓ rust-analyzer",
                () => $"rust-analyzer quiescent; status item: {Env.StatusBar.Get(ServerStatusDisplay.ItemId)?.Text ?? "none"}; servers: "
                      + string.Join(", ", Env.Manager.Servers.Select(s => $"{s.Id} {s.State} {s.LastError}")), Long);
        }

        public async ValueTask DisposeAsync()
        {
            if (Env is not null) await Env.DisposeAsync();
        }
    }

    private LspTestEnv Env => f.Env;

    private void NeedsServer() => Assert.SkipWhen(f.SkipReason is not null, f.SkipReason ?? "");

    private void Focus(InMemoryTextDocument doc, int line, int character)
    {
        Env.Navigation.ActiveDocument = new EditorDocumentView(doc);
        Env.Navigation.ActiveCaret = new TextPosition(line, character);
    }

    private Task RunAsync(string command, params object?[] args) => Env.Commands.ExecuteAsync(command, args).AsTask().Within(Long);

    private string ReadOnlyText(Uri uri) => Encoding.UTF8.GetString(Env.ReadOnlyDocuments.ReadFileAsync(uri, Ct).AsTask().Result);

    private (Uri Uri, TextRange? Selection) LastOpened() { lock (Env.Navigation.Opened) return Env.Navigation.Opened[^1]; }

    [Fact]
    public void Server_status_reaches_the_status_bar_with_its_menu()
    {
        NeedsServer();
        var item = Env.StatusBar.Get(ServerStatusDisplay.ItemId)!;
        Assert.Equal(RustAnalyzerExtension.StatusMenuCommand, item.CommandId);
        Assert.Contains("ready", item.Tooltip);
    }

    [Fact]
    public async Task Expand_macro_opens_the_expansion_as_a_read_only_rust_document()
    {
        NeedsServer();
        Focus(f.Main, 8, 14); // inside twice!(…)
        await RunAsync(RustAnalyzerExtension.ExpandMacroCommand);
        var (uri, _) = LastOpened();
        Assert.Equal("lsp-readonly:/rust-analyzer/expand-macro/twice!.rs", uri.ToString());
        Assert.Contains("(v.len()) + (v.len())", ReadOnlyText(uri));
        Assert.True((await Env.ReadOnlyDocuments.StatAsync(uri, Ct))!.IsReadOnly);
    }

    [Fact]
    public async Task Syntax_tree_hir_and_mir_open_as_read_only_text()
    {
        NeedsServer();
        Focus(f.Main, 7, 8);
        await RunAsync(RustAnalyzerExtension.ViewSyntaxTreeCommand);
        var tree = LastOpened().Uri;
        Assert.EndsWith("/syntax-tree/main.rast", tree.ToString());
        Assert.StartsWith("SOURCE_FILE@0..", ReadOnlyText(tree));
        Assert.Contains("  FN@", ReadOnlyText(tree));
        await RunAsync(RustAnalyzerExtension.ViewHirCommand);
        Assert.StartsWith("fn main()", ReadOnlyText(LastOpened().Uri));
        await RunAsync(RustAnalyzerExtension.ViewMirCommand);
        Assert.Contains("let _0: ()", ReadOnlyText(LastOpened().Uri));
    }

    [Fact]
    public async Task Matching_brace_parent_module_and_cargo_toml_move_the_caret_or_open_the_file()
    {
        NeedsServer();
        Focus(f.Main, 6, 10); // the { of fn main
        await RunAsync(RustAnalyzerExtension.MatchingBraceCommand);
        Assert.Equal((f.Main.Uri, (TextRange?)new TextRange(new TextPosition(11, 0), new TextPosition(11, 0))), LastOpened());

        Focus(f.Util, 0, 4);
        await RunAsync(RustAnalyzerExtension.ParentModuleCommand);
        Assert.Equal(f.Main.Uri, LastOpened().Uri);

        await RunAsync(RustAnalyzerExtension.OpenCargoTomlCommand);
        Assert.Equal(new Uri(Path.Combine(Env.Root, "Cargo.toml")).LocalPath, LastOpened().Uri.LocalPath);
    }

    [Fact]
    public async Task Join_lines_and_structural_search_replace_edit_through_the_workspace_edit_path()
    {
        NeedsServer();
        var (doc, _) = Env.OpenWithToken("src/scratch.rs", "fn scratch() -> i32 {\n    1 +\n        2\n}\n", "rust");
        await TestHelpers.EventuallyAsync(() => Env.Manager.Servers.Any(s => s.Id == "rust-analyzer"), "routed");
        Focus(doc, 1, 4);
        var before = Env.Navigation.AppliedEdits.Count;
        await RunAsync(RustAnalyzerExtension.JoinLinesCommand);
        await TestHelpers.EventuallyAsync(() => Env.Navigation.AppliedEdits.Count > before, "join lines edit", Long);
        Assert.Contains("1 + 2", doc.GetText());

        Env.Window.InputResponses.Enqueue("foo($a) ==>> bar($a)");
        Focus(f.Ssr, 0, 30);
        before = Env.Navigation.AppliedEdits.Count;
        await RunAsync(RustAnalyzerExtension.SsrCommand);
        Assert.Single(Env.Navigation.AppliedEdits.Skip(before));
        Assert.StartsWith("pub fn f(a: i32) -> i32 { bar(a) + bar(2) }", f.Ssr.GetText());
    }

    [Fact]
    public async Task Open_docs_opens_the_web_documentation()
    {
        NeedsServer();
        Focus(f.Main, 7, 13); // vec!
        await RunAsync(RustAnalyzerExtension.OpenDocsCommand);
        lock (Env.ProgressWindow.Events) Assert.Contains(Env.ProgressWindow.Events, e => e.StartsWith("external:https://doc.rust-lang.org/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Hover_on_main_has_trusted_run_and_debug_actions()
    {
        NeedsServer();
        var hover = await Env.Provider<IHoverProvider>(f.Main).ProvideHoverAsync(f.Main, new TextPosition(6, 4), Ct).Within(Long);
        var actions = Assert.Single(hover!.Contents, c => c.IsTrusted);
        Assert.Contains("](command:rust-analyzer.runSingle?", actions.Value);
        Assert.Contains("](command:rust-analyzer.debugSingle?", actions.Value);
    }

    [Fact]
    public async Task Lenses_show_run_and_debug_on_main_and_none_on_tests()
    {
        NeedsServer();
        var main = await Env.Provider<ICodeLensProvider>(f.Main).ProvideCodeLensesAsync(f.Main, Ct).Within(Long);
        Assert.Contains(main!, l => l.Command?.CommandId == RustAnalyzerExtension.RunSingleCommand && l.Range.Start.Line == 6);
        Assert.Contains(main!, l => l.Command?.CommandId == RustAnalyzerExtension.DebugSingleCommand && l.Range.Start.Line == 6);
        var util = await Env.Provider<ICodeLensProvider>(f.Util).ProvideCodeLensesAsync(f.Util, Ct).Within(Long);
        Assert.DoesNotContain(util ?? [], l => l.Command?.CommandId is RustAnalyzerExtension.RunSingleCommand or RustAnalyzerExtension.DebugSingleCommand);
    }

    [Fact]
    public async Task The_run_lens_runs_cargo_in_a_task_terminal_and_debug_launches_through_cargo()
    {
        NeedsServer();
        var lens = (await Env.Provider<ICodeLensProvider>(f.Main).ProvideCodeLensesAsync(f.Main, Ct).Within(Long))!
            .First(l => l.Command?.CommandId == RustAnalyzerExtension.RunSingleCommand).Command!;
        await RunAsync(lens.CommandId, [.. lens.Arguments!.Cast<object?>()]);
        var run = Assert.Single(f.Terminals.Requests);
        Assert.Equal("cargo", Path.GetFileName(run.FileName));
        Assert.Equal(["run", "--package", "demo", "--bin", "demo"], run.Arguments);
        Assert.Equal(TempDirectory.PhysicalPath(Env.Root), TempDirectory.PhysicalPath(run.WorkingDirectory!));

        await RunAsync(RustAnalyzerExtension.DebugSingleCommand, [.. lens.Arguments!.Cast<object?>()]);
        var launch = Assert.Single(f.Launched);
        Assert.Equal(OperatingSystem.IsMacOS() ? "lldb" : "gdb", launch.Type);
        var cargo = launch.Properties["cargo"];
        Assert.Equal(["build", "--package", "demo", "--bin", "demo"], cargo.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal("demo", cargo.GetProperty("filter").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Run_quick_pick_lists_the_runnables_of_the_file()
    {
        NeedsServer();
        Focus(f.Util, 7, 10);
        f.Terminals.Requests.Clear();
        Env.Window.QuickPickResponses.Enqueue(0); // the first: the tests module around the caret
        await RunAsync(RustAnalyzerExtension.RunCommand);
        var run = Assert.Single(f.Terminals.Requests);
        Assert.Equal("test", run.Arguments[0]);
        Assert.Contains("--", run.Arguments);
        Assert.Contains(run.Arguments, a => a.StartsWith("util::tests", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reload_workspace_and_rebuild_proc_macros_succeed_and_the_server_settles_again()
    {
        NeedsServer();
        await RunAsync(RustAnalyzerExtension.ReloadWorkspaceCommand);
        await RunAsync(RustAnalyzerExtension.RebuildProcMacrosCommand);
        await TestHelpers.EventuallyAsync(() => Env.StatusBar.Get(ServerStatusDisplay.ItemId)?.Text == "✓ rust-analyzer", "quiescent again", Long);
        Assert.DoesNotContain("failed", Env.Output.Text("Rust (rust-analyzer) (LSP)"), StringComparison.Ordinal);
    }

    private static string? FindOnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);

    /// <summary>Task terminals that record what they are asked to run (nothing runs).</summary>
    public sealed class RecordingTerminals : ITaskTerminalHost
    {
        public List<TaskTerminalRequest> Requests { get; } = [];

        public ValueTask<ITaskTerminalRun> StartAsync(TaskTerminalRequest request, CancellationToken ct = default)
        {
            lock (Requests) Requests.Add(request);
            return ValueTask.FromResult<ITaskTerminalRun>(new Run());
        }

        private sealed class Run : ITaskTerminalRun
        {
            public Task<TaskTerminalExit> Completion => Task.FromResult(new TaskTerminalExit(0, false));
            public void Terminate() { }
            public ValueTask RevealAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        }
    }

    /// <summary>An <see cref="IDebugService"/> that records the configurations started (and nothing else).</summary>
    public class RecordingDebug : DispatchProxy
    {
        private List<DebugConfiguration> _launched = null!;

        public static IDebugService Create(List<DebugConfiguration> launched)
        {
            var proxy = DispatchProxy.Create<IDebugService, RecordingDebug>();
            ((RecordingDebug)(object)proxy)._launched = launched;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IDebugService.StartAsync)) throw new NotSupportedException(targetMethod?.Name);
            lock (_launched) _launched.Add((DebugConfiguration)args![0]!);
            return new ValueTask<IDebugSession?>((IDebugSession?)null);
        }
    }
}
