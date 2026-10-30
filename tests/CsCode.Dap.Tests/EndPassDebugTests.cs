using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CsCode.Core.Debugging;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Dap.Adapters;
using CsCode.Dap.Client;
using CsCode.Dap.Configuration;
using CsCode.Dap.Service;
using CsCode.Dap.Session;
using CsCode.Dap.Tasks;
using CsCode.Dap.Tests.Fakes;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>Regression tests for the debug findings of the end-pass review (docs: .scratch/notes/end-pass-review.json, area "debug").</summary>
public class EndPassDebugTests
{
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "CsCode.slnx"))) return dir.FullName;
        throw new InvalidOperationException("repository root not found");
    }

    private static JsonDocument ReadJsonc(string path) =>
        JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

    // ------------------------------------------------------------------ debug-1: Create launch.json and an existing .vscode/tasks.json

    private const string UserTasks = """
        {
            // the user's own tasks
            "version": "2.0.0",
            "tasks": [
                { "label": "user-task", "type": "process", "command": "true" },
            ]
        }
        """;

    [Fact]
    public async Task Creating_a_launch_json_keeps_the_users_vscode_tasks_and_adds_the_build_task_to_them()
    {
        await using var h = new ServiceHarness();
        h.Dir.WriteFile("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
        h.Dir.WriteFile(".vscode/tasks.json", UserTasks);
        Assert.Equal(["user-task"], (await h.Tasks.GetTasksAsync(h.Dir.Uri, Ct)).Select(t => t.Label));

        var launch = await h.Configurations.CreateLaunchJsonAsync("coreclr", h.Dir.Uri, Ct);

        Assert.False(File.Exists(Path.Combine(h.Dir.Path, ".cscode", "tasks.json")), "a .cscode/tasks.json would hide the user's tasks");
        Assert.Equal(Path.Combine(h.Dir.Path, ".vscode", "launch.json"), launch.LocalPath); // next to the folder's tasks.json
        Assert.Equal(["user-task", "build"], (await h.Tasks.GetTasksAsync(h.Dir.Uri, Ct)).Select(t => t.Label));
        var text = File.ReadAllText(Path.Combine(h.Dir.Path, ".vscode", "tasks.json"));
        Assert.Contains("// the user's own tasks", text, StringComparison.Ordinal); // the user's text is kept as written
        var result = await h.Tasks.RunAsync("user-task", h.Dir.Uri, Ct);
        Assert.True(result.Succeeded, result.Error);
        Assert.Contains(await h.Configurations.GetLaunchConfigurationsAsync(h.Dir.Uri, Ct), c => c.Name == ".NET: Launch");
    }

    [Fact]
    public async Task A_template_task_whose_label_the_user_has_is_not_added_and_a_second_default_build_task_is_not_made()
    {
        await using var h = new ServiceHarness();
        h.Dir.WriteFile("App.csproj", "<Project/>");
        h.Dir.WriteFile(".vscode/tasks.json", """
            { "version": "2.0.0", "tasks": [ { "label": "build", "type": "shell", "command": "make", "group": { "kind": "build", "isDefault": true } } ] }
            """);
        await h.Configurations.CreateLaunchJsonAsync("coreclr", h.Dir.Uri, Ct);
        var tasks = await h.Tasks.GetTasksAsync(h.Dir.Uri, Ct);
        Assert.Equal("build", Assert.Single(tasks).Label);
        Assert.Contains("\"make\"", File.ReadAllText(Path.Combine(h.Dir.Path, ".vscode", "tasks.json")), StringComparison.Ordinal);
    }

    /// <summary>The user's default build task has another label: the template's "build" is added, as a plain build task (one default per group).</summary>
    [Fact]
    public async Task Next_to_the_users_default_build_task_the_template_build_task_is_added_as_a_plain_build_task()
    {
        await using var h = new ServiceHarness();
        h.Dir.WriteFile("App.csproj", "<Project/>");
        h.Dir.WriteFile(".vscode/tasks.json", """
            { "version": "2.0.0", "tasks": [ { "label": "make all", "type": "shell", "command": "make", "group": { "kind": "build", "isDefault": true } } ] }
            """);
        await h.Configurations.CreateLaunchJsonAsync("coreclr", h.Dir.Uri, Ct);
        var tasks = await h.Tasks.GetTasksAsync(h.Dir.Uri, Ct);
        Assert.Equal(["make all", "build"], tasks.Select(t => t.Label));
        Assert.Equal("make all", Assert.Single(tasks, t => t.IsDefaultBuild).Label);
        Assert.Equal(("build", false), (tasks[1].Group, tasks[1].IsDefault));
    }

    /// <summary>A default TEST task is no default build task: the template's build task stays the folder's default build task.</summary>
    [Fact]
    public async Task A_default_test_task_does_not_keep_the_template_build_task_from_being_the_default_build_task()
    {
        await using var h = new ServiceHarness();
        h.Dir.WriteFile("App.csproj", "<Project/>");
        h.Dir.WriteFile(".vscode/tasks.json", """
            { "version": "2.0.0", "tasks": [ { "label": "test", "type": "shell", "command": "make test", "group": { "kind": "test", "isDefault": true } } ] }
            """);
        await h.Configurations.CreateLaunchJsonAsync("coreclr", h.Dir.Uri, Ct);
        var tasks = await h.Tasks.GetTasksAsync(h.Dir.Uri, Ct);
        Assert.Equal(["test", "build"], tasks.Select(t => t.Label));
        Assert.Equal("build", Assert.Single(tasks, t => t.IsDefaultBuild).Label);
        Assert.Equal(("test", true), (tasks[0].Group, tasks[0].IsDefault));
    }

    [Theory]
    [InlineData("""{ "tasks": [ { "label": "b", "group": { "kind": "build", "isDefault": true } } ] }""", null)] // label present: nothing to add
    [InlineData("""{ "tasks": [ { "label": "x", "group": { "kind": "build", "isDefault": true } } ] }""", "\"build\"")]
    [InlineData("""{ "tasks": [ { "label": "x", "group": { "kind": "BUILD", "isDefault": true } } ] }""", "\"build\"")]
    [InlineData("""{ "tasks": [ { "label": "x", "group": { "kind": "test", "isDefault": true } } ] }""", "{\"kind\":\"build\",\"isDefault\":true}")]
    [InlineData("""{ "tasks": [ { "label": "x", "group": { "kind": "build", "isDefault": false } } ] }""", "{\"kind\":\"build\",\"isDefault\":true}")]
    [InlineData("""{ "tasks": [ { "label": "x", "group": "build" } ] }""", "{\"kind\":\"build\",\"isDefault\":true}")]
    public void A_default_build_task_is_demoted_only_next_to_another_default_build_task(string text, string? group)
    {
        var offered = new JsonObject { ["label"] = "b", ["group"] = new JsonObject { ["kind"] = "build", ["isDefault"] = true } };
        var edit = DebugConfigurationService.AddTasks(text, [offered], out var problem);
        Assert.Null(problem);
        if (group is null) { Assert.Null(edit); return; }
        var added = JsonNode.Parse(edit!.Value.ApplyTo(text))!["tasks"]!.AsArray()[^1]!;
        Assert.Equal(("b", group), (added["label"]!.GetValue<string>(), added["group"]!.ToJsonString()));
    }

    [Fact]
    public void A_tasks_json_that_does_not_parse_is_not_edited_and_says_why()
    {
        Assert.Null(DebugConfigurationService.AddTasks("{ \"tasks\": [ ", [new JsonObject { ["label"] = "b" }], out var problem));
        Assert.Contains("does not parse", problem, StringComparison.Ordinal);
        Assert.Null(DebugConfigurationService.AddTasks("{ \"tasks\": 3 }", [new JsonObject { ["label"] = "b" }], out problem));
        Assert.Contains("not a list", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_any_tasks_json_the_companion_goes_to_cscode_as_before()
    {
        await using var h = new ServiceHarness();
        h.Dir.WriteFile("App.csproj", "<Project/>");
        var launch = await h.Configurations.CreateLaunchJsonAsync("coreclr", h.Dir.Uri, Ct);
        Assert.Equal(Path.Combine(h.Dir.Path, ".cscode", "launch.json"), launch.LocalPath);
        Assert.True(File.Exists(Path.Combine(h.Dir.Path, ".cscode", "tasks.json")));
    }

    [Theory]
    [InlineData("{\n    \"tasks\": [\n        { \"label\": \"a\" }\n    ]\n}\n", "{\n    \"tasks\": [\n        { \"label\": \"a\" },\n        {\n            \"label\": \"b\"\n        }\n    ]\n}\n")]
    [InlineData("{\n  \"tasks\": [\n    { \"label\": \"a\" }, // trailing comma\n  ]\n}", "{\n  \"tasks\": [\n    { \"label\": \"a\" },\n    {\n        \"label\": \"b\"\n    } // trailing comma\n  ]\n}")]
    [InlineData("{\n    \"tasks\": []\n}", "{\n    \"tasks\": [\n        {\n            \"label\": \"b\"\n        }\n    ]\n}")]
    [InlineData("{\n    \"version\": \"2.0.0\" /* no tasks yet */\n}", "{\n    \"version\": \"2.0.0\",\n    \"tasks\": [\n        {\n            \"label\": \"b\"\n        }\n    ] /* no tasks yet */\n}")]
    [InlineData("{}", "{\n    \"tasks\": [\n        {\n            \"label\": \"b\"\n        }\n    ]\n}")]
    public void Appending_to_a_jsonc_array_keeps_the_rest_of_the_text(string text, string expected)
    {
        var edit = JsoncText.AppendToArray(text, "tasks", [new JsonObject { ["label"] = "b" }]);
        Assert.NotNull(edit);
        var result = edit.Value.ApplyTo(text);
        Assert.Equal(expected, result);
        Assert.StartsWith("{", result[edit.Value.ElementOffset..], StringComparison.Ordinal);
        using var _ = JsonDocument.Parse(result, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{ \"tasks\": 3 }")]
    [InlineData("{ \"tasks\": [ { \"label\": ")]
    [InlineData("not json")]
    public void A_text_without_an_editable_array_is_not_edited(string text) =>
        Assert.Null(JsoncText.AppendToArray(text, "tasks", [new JsonObject { ["label"] = "b" }]));

    [Fact]
    public void Strings_and_comments_that_look_like_structure_are_not_taken_for_it()
    {
        const string text = "{\n    \"note\": \"] } [ {\", // ] }\n    /* \"tasks\": [ */\n    \"tasks\": [ { \"label\": \"a]\" } ]\n}";
        var result = JsoncText.AppendToArray(text, "tasks", [new JsonObject { ["label"] = "b" }])!.Value.ApplyTo(text);
        using var doc = JsonDocument.Parse(result, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        Assert.Equal(["a]", "b"], doc.RootElement.GetProperty("tasks").EnumerateArray().Select(t => t.GetProperty("label").GetString()));
        Assert.Equal("] } [ {", doc.RootElement.GetProperty("note").GetString());
    }

    // ------------------------------------------------------------------ debug-2: GDB's launch environment (GdbAdapterFactoryTests)

    // ------------------------------------------------------------------ debug-3: terminated → disconnect, late output kept

    [Fact]
    public async Task After_terminated_the_service_lets_the_session_disconnect_and_keeps_the_output_sent_until_then()
    {
        await using var h = new ServiceHarness(configureHost: host => host.Configure = (a, _) =>
            a.OnAsync("disconnect", async _ =>
            {
                await a.SendOutputAsync("late line\n", "stdout"); // after `terminated`, before the adapter goes
                return null;
            }));
        var session = (DebugSession)await h.StartAsync();
        await h.AdapterFor(0).SendOutputAsync("early line\n", "stdout");
        await h.AdapterFor(0).SendTerminatedAsync();

        await h.AdapterFor(0).WaitForRequestAsync("disconnect"); // spec: the client disconnects after `terminated`
        await session.Disposed.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var history = string.Concat(h.Service.GetOutputHistory().Select(e => e.Output));
        Assert.Contains("early line", history, StringComparison.Ordinal);
        Assert.Contains("late line", history, StringComparison.Ordinal);
        Assert.DoesNotContain(h.Service.GetOutputHistory(), e => ReferenceEquals(e.Session, session)); // relabelled: the history keeps no session alive
        Assert.Empty(h.Service.Sessions);
    }

    // ------------------------------------------------------------------ debug-4: Open launch.json, Add Configuration..., open after create

    private sealed class EditorNavigation : INavigationService
    {
        private readonly Dictionary<Uri, Doc> _docs = new();
        private readonly Emitter<IEditorDocument?> _active = new();
        public List<(Uri Uri, TextRange? Selection)> Opened { get; } = new();
        public List<WorkspaceEdit> Edits { get; } = new();
        public IEditorDocument? ActiveDocument => null;
        public TextPosition? ActiveCaret => null;
        public IEventSource<IEditorDocument?> ActiveDocumentChanged => _active;
        public IReadOnlyList<IEditorDocument> OpenDocuments { get { lock (_docs) return [.. _docs.Values]; } }
        public Doc? Document(Uri uri) { lock (_docs) return _docs.GetValueOrDefault(uri); }

        public ValueTask<IEditorDocument?> OpenAsync(Uri uri, TextRange? selection = null, bool preview = false, bool preserveFocus = false, CancellationToken ct = default)
        {
            lock (_docs)
            {
                Opened.Add((uri, selection));
                if (!_docs.TryGetValue(uri, out var doc))
                {
                    if (!File.Exists(uri.LocalPath)) return ValueTask.FromResult<IEditorDocument?>(null);
                    _docs[uri] = doc = new Doc(uri, File.ReadAllText(uri.LocalPath));
                }
                return ValueTask.FromResult<IEditorDocument?>(doc);
            }
        }

        public ValueTask<bool> ApplyWorkspaceEditAsync(WorkspaceEdit edit, string? label = null, CancellationToken ct = default)
        {
            lock (_docs) Edits.Add(edit);
            foreach (var (uri, edits) in edit.Changes)
                if (Document(uri) is not { } doc || !doc.ApplyEdits(edits)) return ValueTask.FromResult(false);
            return ValueTask.FromResult(true);
        }

        public sealed class Doc(Uri uri, string text) : IEditorDocument
        {
            private readonly InMemoryTextDocument _doc = new(uri, "jsonc", text);
            public Uri Uri => _doc.Uri;
            public string LanguageId => _doc.LanguageId;
            public int Version => _doc.Version;
            public bool IsDirty => _doc.IsDirty;
            public int Length => _doc.Length;
            public int LineCount => _doc.LineCount;
            public bool IsReadOnly => false;
            public bool IsUntitled => false;
            public string EncodingName => "utf-8";
            public string EndOfLine => "\n";
            public string GetText() => _doc.GetText();
            public string GetText(TextRange range) => _doc.GetText(range);
            public string GetLineText(int line) => _doc.GetLineText(line);
            public int OffsetAt(TextPosition position) => _doc.OffsetAt(position);
            public TextPosition PositionAt(int offset) => _doc.PositionAt(offset);
            public TextPosition Validate(TextPosition position) => _doc.Validate(position);
            public bool ApplyEdits(IReadOnlyList<TextEdit> edits) => _doc.ApplyEdits(edits);
            public void SetLanguageId(string languageId) => _doc.SetLanguageId(languageId);
            public Task SaveAsync(CancellationToken ct = default) => File.WriteAllTextAsync(Uri.LocalPath, GetText(), ct);
            public Task SaveAsAsync(Uri target, CancellationToken ct = default) => Task.CompletedTask;
            public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
            public IEventSource<TextDocumentChangedEventArgs> Changed => _doc.Changed;
            public IEventSource<ITextDocument> LanguageIdChanged => _doc.LanguageIdChanged;
            public IEventSource<IEditorDocument> Saved { get; } = new Emitter<IEditorDocument>();
            public IEventSource<IEditorDocument> DirtyChanged { get; } = new Emitter<IEditorDocument>();
        }
    }

    private static ServiceHarness HarnessWithEditor(EditorNavigation nav) => new(navigation: nav, moreBuiltins: p =>
    [
        .. GdbAdapterFactory.Builtins(new GdbAdapterFactory(p)),
        .. ServiceCollectionExtensions.JsDebugAdapters().Select(a => a(new JsDebugAdapterFactory(p))),
    ]);

    [Fact]
    public async Task Both_debug_menu_commands_are_registered()
    {
        await using var h = new ServiceHarness();
        Assert.True(h.Commands.Has(DebugService.ConfigureCommand));
        Assert.True(h.Commands.Has(DebugService.AddConfigurationCommand));
    }

    [Fact]
    public async Task Open_launch_json_opens_the_folders_file()
    {
        var nav = new EditorNavigation();
        await using var h = HarnessWithEditor(nav);
        h.Dir.WriteFile(".vscode/launch.json", "{ \"version\": \"0.2.0\", \"configurations\": [] }");
        await h.Commands.ExecuteAsync(DebugService.ConfigureCommand);
        Assert.Equal(Path.Combine(h.Dir.Path, ".vscode", "launch.json"), Assert.Single(nav.Opened).Uri.LocalPath);
    }

    [Fact]
    public async Task Open_launch_json_without_one_creates_it_for_the_folders_debugger_and_opens_it()
    {
        var nav = new EditorNavigation();
        await using var h = HarnessWithEditor(nav);
        h.Dir.WriteFile("App.csproj", "<Project/>");
        await h.Commands.ExecuteAsync(DebugService.ConfigureCommand);
        var launch = Path.Combine(h.Dir.Path, ".cscode", "launch.json");
        Assert.True(File.Exists(launch));
        Assert.Equal(launch, Assert.Single(nav.Opened).Uri.LocalPath);
        Assert.Contains("coreclr", File.ReadAllText(launch), StringComparison.Ordinal);
    }

    [Fact]
    public async Task F5_create_launch_json_opens_the_new_file_and_an_existing_unusable_launch_json_is_opened_instead_of_offering_create()
    {
        var nav = new EditorNavigation();
        await using var h = HarnessWithEditor(nav);
        h.Dir.WriteFile("App.csproj", "<Project/>");
        h.Window.MessageActionResponses.Enqueue("Create launch.json");
        await h.Commands.ExecuteAsync(KnownCommands.DebugStart);
        var launch = Path.Combine(h.Dir.Path, ".cscode", "launch.json");
        Assert.Equal(launch, Assert.Single(nav.Opened).Uri.LocalPath);

        // a launch.json that does not parse: its error is shown once, and F5 opens it (there is nothing to create)
        File.WriteAllText(launch, "{ \"configurations\": [ { \"type\": ");
        h.Configurations.Invalidate(h.Dir.Uri);
        var messages = h.Window.Messages.Count;
        await h.Commands.ExecuteAsync(KnownCommands.DebugStart);
        Assert.Equal(2, nav.Opened.Count);
        Assert.Equal(launch, nav.Opened[1].Uri.LocalPath);
        Assert.DoesNotContain(h.Window.Messages.Skip(messages), m => m.Message.Contains("Create a launch.json", StringComparison.Ordinal));
        Assert.Empty(h.Service.Sessions);
    }

    [Fact]
    public async Task Add_configuration_appends_the_picked_debuggers_configuration_in_the_editor_and_reveals_it()
    {
        var nav = new EditorNavigation();
        await using var h = HarnessWithEditor(nav);
        const string existing = """
            {
                // mine
                "version": "0.2.0",
                "configurations": [
                    { "name": "Mine", "type": "fake", "request": "launch" }
                ]
            }
            """;
        h.Dir.WriteFile(".cscode/launch.json", existing);
        await SessionHarness.Eventually(() => h.Service.DebuggerTypes.Any(t => t.Type == "fake"), 5000, "the debugger list is complete");
        var types = h.Service.DebuggerTypes.Select(t => t.Type).ToList();
        h.Window.QuickPickResponses.Enqueue(types.IndexOf("gdb")); // the debugger
        h.Window.QuickPickResponses.Enqueue(1); // its second configuration ("GDB: Attach to process")
        await h.Commands.ExecuteAsync(DebugService.AddConfigurationCommand);

        var uri = new Uri(Path.Combine(h.Dir.Path, ".cscode", "launch.json"));
        var doc = nav.Document(uri) ?? throw new Xunit.Sdk.XunitException("launch.json was not opened");
        Assert.Single(nav.Edits); // one undoable edit in the editor (unsaved, like VS Code)
        var text = doc.GetText();
        Assert.StartsWith(existing[..existing.IndexOf("{ \"name\": \"Mine\"", StringComparison.Ordinal)], text, StringComparison.Ordinal); // the user's text as written
        Assert.Contains("// mine", text, StringComparison.Ordinal);
        var file = LaunchJson.Parse(text, h.Dir.Uri, "launch.json");
        Assert.Equal(["Mine", "GDB: Attach to process"], file.Configurations.Select(c => c.Name));
        var reveal = nav.Opened[^1];
        Assert.Equal(uri, reveal.Uri);
        Assert.StartsWith("{", doc.GetLineText(reveal.Selection!.Value.Start.Line)[reveal.Selection.Value.Start.Character..], StringComparison.Ordinal);
        Assert.Contains("\"GDB: Attach to process\"", string.Join("\n", Enumerable.Range(reveal.Selection.Value.Start.Line, 4).Select(doc.GetLineText)), StringComparison.Ordinal);
        Assert.Equal(existing, File.ReadAllText(uri.LocalPath)); // nothing written behind the editor's back
        Assert.False(File.Exists(Path.Combine(h.Dir.Path, ".cscode", "tasks.json"))); // an attach configuration runs no task: none is added
    }

    [Fact]
    public async Task Add_configuration_without_an_editor_writes_the_file()
    {
        await using var h = new ServiceHarness(moreBuiltins: p => [.. GdbAdapterFactory.Builtins(new GdbAdapterFactory(p))]);
        h.Dir.WriteFile(".vscode/launch.json", "{ \"version\": \"0.2.0\", \"configurations\": [] }");
        await SessionHarness.Eventually(() => h.Service.DebuggerTypes.Any(t => t.Type == "fake"), 5000, "the debugger list is complete");
        h.Window.QuickPickResponses.Enqueue(h.Service.DebuggerTypes.Select(t => t.Type).ToList().IndexOf("gdb"));
        h.Window.QuickPickResponses.Enqueue(0);
        await h.Commands.ExecuteAsync(DebugService.AddConfigurationCommand);
        await SessionHarness.Eventually(() => File.ReadAllText(Path.Combine(h.Dir.Path, ".vscode", "launch.json")).Contains("GDB: Launch", StringComparison.Ordinal));
        await SessionHarness.Eventually(() => h.Configurations.GetLaunchConfigurationsAsync(h.Dir.Uri, Ct).AsTask().Result.Count == 1, 5000, "the new configuration is listed");
    }

    /// <summary>Picks <paramref name="type"/>'s configuration named <paramref name="name"/> in Add Configuration's two quick picks.</summary>
    private static async Task AddConfigurationAsync(ServiceHarness h, string type, string name)
    {
        await SessionHarness.Eventually(() => h.Service.DebuggerTypes.Any(t => t.Type == "fake"), 5000, "the debugger list is complete");
        var templates = (await h.Configurations.GetConfigurationTemplatesAsync(type, h.Dir.Uri, Ct)).Configurations.Select(c => c.Name).ToList();
        Assert.Contains(name, templates);
        h.Window.QuickPickResponses.Enqueue(h.Service.DebuggerTypes.Select(t => t.Type).ToList().IndexOf(type));
        if (templates.Count > 1) h.Window.QuickPickResponses.Enqueue(templates.IndexOf(name));
        await h.Commands.ExecuteAsync(DebugService.AddConfigurationCommand);
    }

    private static void CargoPackage(ServiceHarness h)
    {
        h.Dir.WriteFile("Cargo.toml", "[package]\nname = \"sample\"\nversion = \"0.1.0\"\n");
        h.Dir.WriteFile("src/main.rs", "fn main() {}\n");
    }

    /// <summary>
    /// The added configuration's preLaunchTask ('cargo: build') exists nowhere (CsCode detects no tasks): it is added to the folder's own
    /// tasks.json, which keeps its text, so F5 on the new configuration finds it (end-pass review, debug-4).
    /// </summary>
    [Fact]
    public async Task Add_configuration_adds_the_task_its_prelaunch_task_names_to_the_folders_tasks_json()
    {
        var nav = new EditorNavigation();
        await using var h = HarnessWithEditor(nav);
        CargoPackage(h);
        h.Dir.WriteFile(".vscode/launch.json", "{ \"version\": \"0.2.0\", \"configurations\": [] }");
        h.Dir.WriteFile(".vscode/tasks.json", UserTasks);
        Assert.DoesNotContain(await h.Tasks.GetTasksAsync(h.Dir.Uri, Ct), t => t.Label == "cargo: build");

        await AddConfigurationAsync(h, "gdb", "Rust: Debug sample (cargo build)");

        var launch = nav.Document(new Uri(Path.Combine(h.Dir.Path, ".vscode", "launch.json")))!.GetText();
        Assert.Equal("cargo: build", Assert.Single(LaunchJson.Parse(launch, h.Dir.Uri, "launch.json").Configurations).PreLaunchTask);
        var tasks = await h.Tasks.GetTasksAsync(h.Dir.Uri, Ct);
        Assert.Equal(["user-task", "cargo: build"], tasks.Select(t => t.Label));
        Assert.True(tasks[1].IsDefaultBuild);
        Assert.Contains("// the user's own tasks", File.ReadAllText(Path.Combine(h.Dir.Path, ".vscode", "tasks.json")), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(h.Dir.Path, ".cscode", "tasks.json")));

        // the same configuration added again: its task is there now, nothing more is added
        await AddConfigurationAsync(h, "gdb", "Rust: Debug sample (cargo build)");
        Assert.Equal(["user-task", "cargo: build"], (await h.Tasks.GetTasksAsync(h.Dir.Uri, Ct)).Select(t => t.Label));
    }

    [Fact]
    public async Task Add_configuration_without_a_tasks_json_writes_the_templates_next_to_the_launch_json()
    {
        var nav = new EditorNavigation();
        await using var h = HarnessWithEditor(nav);
        h.Dir.WriteFile("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>");
        h.Dir.WriteFile(".vscode/launch.json", "{ \"version\": \"0.2.0\", \"configurations\": [] }");
        await AddConfigurationAsync(h, "coreclr", ".NET: Launch");
        Assert.True(File.Exists(Path.Combine(h.Dir.Path, ".vscode", "tasks.json")));
        var build = Assert.Single(await h.Tasks.GetTasksAsync(h.Dir.Uri, Ct));
        Assert.Equal("build", build.Label);
        Assert.True(build.IsDefaultBuild);
    }

    /// <summary>A tasks.json open in the editor gets the task there (unsaved, like the configuration), not behind the editor's back.</summary>
    [Fact]
    public async Task Add_configuration_adds_the_task_in_the_editor_when_the_tasks_json_is_open()
    {
        var nav = new EditorNavigation();
        await using var h = HarnessWithEditor(nav);
        CargoPackage(h);
        h.Dir.WriteFile(".cscode/launch.json", "{ \"version\": \"0.2.0\", \"configurations\": [] }");
        h.Dir.WriteFile(".cscode/tasks.json", UserTasks);
        var tasksUri = new Uri(Path.Combine(h.Dir.Path, ".cscode", "tasks.json"));
        var tasksDoc = (EditorNavigation.Doc)(await nav.OpenAsync(tasksUri, ct: Ct))!;
        tasksDoc.ApplyEdits([new TextEdit(new TextRange(new TextPosition(0, 0), new TextPosition(0, 0)), "// unsaved\n")]);

        await AddConfigurationAsync(h, "gdb", "Rust: Debug sample (cargo build)");

        Assert.Equal(UserTasks, File.ReadAllText(tasksUri.LocalPath)); // nothing written behind the editor's back
        var text = tasksDoc.GetText();
        Assert.StartsWith("// unsaved\n", text, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        Assert.Equal(["user-task", "cargo: build"], doc.RootElement.GetProperty("tasks").EnumerateArray().Select(t => t.GetProperty("label").GetString()));
    }

    // ------------------------------------------------------------------ debug-5: a start in progress

    private static DebugConfiguration WithPreLaunchTask(ServiceHarness h, string seconds)
    {
        h.Dir.WriteFile(".cscode/tasks.json", $$"""{ "version": "2.0.0", "tasks": [ { "label": "slow", "type": "process", "command": "sleep", "args": ["{{seconds}}"] } ] }""");
        return ServiceHarness.Config("Slow", extra: new Dictionary<string, object?> { ["preLaunchTask"] = "slow" }, folder: h.Dir.Uri);
    }

    [Fact]
    public async Task While_the_prelaunch_task_builds_the_debugger_counts_as_starting_and_a_second_start_of_the_same_configuration_is_ignored()
    {
        await using var h = new ServiceHarness();
        var config = WithPreLaunchTask(h, "1.5");
        var first = h.Service.StartAsync(config, Ct).AsTask();
        await SessionHarness.Eventually(() => h.Tasks.RunningProcessIds.Count == 1, 5000, "the preLaunchTask runs");
        await SessionHarness.Eventually(() => h.Service.State == DebugState.Initializing, 5000, "initializing");
        await SessionHarness.Eventually(() => Equals(h.ContextKeys.Get("inDebugMode"), true) && Equals(h.ContextKeys.Get("debugState"), "initializing"));

        var second = await h.Service.StartAsync(config, Ct); // F5 again (or the palette): no second build, no second debuggee
        Assert.Null(second);
        Assert.Single(h.Tasks.RunningProcessIds);

        Assert.NotNull(await first.WaitAsync(TimeSpan.FromSeconds(20), Ct));
        await Task.Delay(200, Ct);
        Assert.Single(h.Service.Sessions);
        Assert.Single(h.Host.Adapters);
    }

    [Fact]
    public async Task Stop_while_a_session_is_starting_cancels_the_start_and_its_prelaunch_task()
    {
        await using var h = new ServiceHarness();
        var start = h.Service.StartAsync(WithPreLaunchTask(h, "30"), Ct).AsTask();
        await SessionHarness.Eventually(() => h.Tasks.RunningProcessIds.Count == 1, 5000, "the preLaunchTask runs");
        var pid = h.Tasks.RunningProcessIds[0];

        await h.Commands.ExecuteAsync(KnownCommands.DebugStop);

        Assert.Null(await start.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        await SessionHarness.Eventually(() => h.Tasks.RunningProcessIds.Count == 0, 5000, "the preLaunchTask was killed");
        Assert.False(ProcessRuns(pid), "the build still runs");
        await SessionHarness.Eventually(() => h.Service.State == DebugState.Inactive && Equals(h.ContextKeys.Get("inDebugMode"), false));
        Assert.Empty(h.Service.Sessions);
        Assert.Empty(h.Host.Adapters);
        Assert.DoesNotContain(h.Window.Messages, m => m.Message.Contains("preLaunchTask", StringComparison.Ordinal)); // no "Debug Anyway" prompt
    }

    private static bool ProcessRuns(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    [Fact]
    public async Task A_different_configuration_can_start_while_one_is_starting()
    {
        await using var h = new ServiceHarness();
        var slow = h.Service.StartAsync(WithPreLaunchTask(h, "1"), Ct).AsTask();
        await SessionHarness.Eventually(() => h.Tasks.RunningProcessIds.Count == 1, 5000, "the preLaunchTask runs");
        Assert.NotNull(await h.Service.StartAsync(ServiceHarness.Config("Other", folder: h.Dir.Uri), Ct));
        Assert.NotNull(await slow.WaitAsync(TimeSpan.FromSeconds(20), Ct));
        Assert.Equal(2, h.Service.Sessions.Count);
    }

    // ------------------------------------------------------------------ debug-6: settings

    [Fact]
    public async Task Every_setting_the_debugger_reads_is_registered()
    {
        await using var h = new ServiceHarness();
        var registered = h.Settings.Properties.ToDictionary(p => p.Key);
        foreach (var key in DebugService.SettingsRead) Assert.True(registered.ContainsKey(key), $"'{key}' is read but not registered");

        // and no setting key in the module's sources is left out (a new one must be registered, so the Settings editor lists it)
        var literals = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "CsCode.Dap"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), "\"((?:cscode\\.debug|lldb)\\.[A-Za-z][A-Za-z.]*|cscode\\.debuggers)\"").Select(m => m.Groups[1].Value))
            .Distinct().ToList();
        Assert.NotEmpty(literals);
        foreach (var key in literals) Assert.True(registered.ContainsKey(key), $"'{key}' appears in src/CsCode.Dap but is not registered");

        Assert.Equal(OperatingSystem.IsMacOS() ? "lldb" : "gdb", registered[DebugService.RustDebuggerSetting].Default!.Value.GetString()); // the OS's default
        Assert.Equal(["gdb", "lldb", "codelldb"], registered[DebugService.RustDebuggerSetting].Schema!.Value.GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["commands", "evaluate", "split"], registered[CodeLldbAdapterFactory.ConsoleModeSetting].Schema!.Value.GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.All(registered.Values, p => Assert.False(string.IsNullOrEmpty(p.Description)));
    }

    // ------------------------------------------------------------------ debug-7: .sln / .slnx / F# roots

    private static NetCoreDbgAdapterFactory NetCore(TempDirectory dir, RecordingWindowService? window = null) => new(new DapPlatform { Workspace = new FakeWorkspace(dir.Uri), Window = window });

    private const string WebProject = "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>";
    private const string LibProject = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>";

    [Fact]
    public async Task A_solution_folder_launches_the_executable_project_its_solution_lists()
    {
        using var dir = new TempDirectory("slnroot-");
        dir.WriteFile("MyApp.sln", """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Lib", "src\Lib\Lib.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "Web", "src\Web\Web.csproj", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "src", "src", "{33333333-3333-3333-3333-333333333333}"
            EndProject
            """);
        dir.WriteFile("src/Lib/Lib.csproj", LibProject);
        dir.WriteFile("src/Web/Web.csproj", WebProject);
        var f = NetCore(dir);
        var launch = (await f.ProvideInitialAsync(dir.Uri, Ct)).Single(c => !c.IsAttach);
        Assert.Equal("${workspaceFolder}/src/Web/bin/Debug/net9.0/Web.dll", launch.GetString("program"));
        Assert.Equal("${workspaceFolder}/src/Web", launch.GetString("cwd"));
        var (_, tasks) = Assert.Single(await f.ProvideInitialFilesAsync(dir.Uri, Ct));
        using var doc = JsonDocument.Parse(tasks);
        Assert.Equal("${workspaceFolder}/src/Web/Web.csproj", doc.RootElement.GetProperty("tasks")[0].GetProperty("args")[1].GetString());
    }

    [Fact]
    public async Task An_slnx_solution_and_an_assembly_name_are_honoured()
    {
        using var dir = new TempDirectory("slnxroot-");
        dir.WriteFile("All.slnx", """
            <Solution>
              <Folder Name="/src/">
                <Project Path="src/Tool/Tool.csproj" />
              </Folder>
            </Solution>
            """);
        dir.WriteFile("src/Tool/Tool.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFrameworks>net8.0;net10.0</TargetFrameworks><AssemblyName>my-tool</AssemblyName></PropertyGroup></Project>");
        var launch = (await NetCore(dir).ProvideInitialAsync(dir.Uri, Ct)).Single(c => !c.IsAttach);
        Assert.Equal("${workspaceFolder}/src/Tool/bin/Debug/net8.0/my-tool.dll", launch.GetString("program"));
    }

    [Fact]
    public async Task Without_a_solution_projects_two_levels_down_are_found_and_with_several_programs_the_user_picks_one()
    {
        using var dir = new TempDirectory("nested-");
        dir.WriteFile("src/Api/Api.csproj", WebProject);
        dir.WriteFile("src/Cli/Cli.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        dir.WriteFile("src/Lib/Lib.csproj", LibProject);
        dir.WriteFile("tests/Api.Tests/Api.Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><IsTestProject>true</IsTestProject></PropertyGroup></Project>");
        dir.WriteFile("src/Api/bin/Debug/Stale.csproj", LibProject); // never looked into
        var window = new RecordingWindowService();
        window.QuickPickResponses.Enqueue(1);
        var f = NetCore(dir, window);
        var launch = (await f.ProvideInitialAsync(dir.Uri, Ct)).Single(c => !c.IsAttach);
        Assert.Equal("${workspaceFolder}/src/Cli/bin/Debug/net10.0/Cli.dll", launch.GetString("program")); // the second program offered
        var (_, tasks) = Assert.Single(await f.ProvideInitialFilesAsync(dir.Uri, Ct)); // the same project is built (no second question)
        Assert.Contains("${workspaceFolder}/src/Cli/Cli.csproj", tasks, StringComparison.Ordinal);
        Assert.Empty(window.QuickPickResponses);
    }

    [Fact]
    public async Task An_fsharp_root_project_is_used()
    {
        using var dir = new TempDirectory("fsroot-");
        dir.WriteFile("Calc.fsproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        var launch = (await NetCore(dir).ProvideInitialAsync(dir.Uri, Ct)).Single(c => !c.IsAttach);
        Assert.Equal("${workspaceFolder}/bin/Debug/net8.0/Calc.dll", launch.GetString("program"));
    }

    [Fact]
    public async Task F5_in_a_solution_folder_writes_a_launch_json_for_the_solutions_program()
    {
        await using var h = new ServiceHarness();
        h.Dir.WriteFile("MyApp.sln", "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Web\", \"src\\Web\\Web.csproj\", \"{22222222-2222-2222-2222-222222222222}\"\nEndProject\n");
        h.Dir.WriteFile("src/Web/Web.csproj", WebProject);
        h.Window.MessageActionResponses.Enqueue("Create launch.json");
        await h.Commands.ExecuteAsync(KnownCommands.DebugStart);
        var launch = Path.Combine(h.Dir.Path, ".cscode", "launch.json");
        await SessionHarness.Eventually(() => File.Exists(launch), 5000, "launch.json created");
        Assert.Contains("${workspaceFolder}/src/Web/bin/Debug/net9.0/Web.dll", File.ReadAllText(launch), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ debug-8: the folder's own tsc

    [Fact]
    public async Task The_tsc_build_task_uses_the_projects_own_compiler_when_it_has_one()
    {
        using var dir = new TempDirectory("localtsc-");
        dir.WriteFile("tsconfig.json", "{ \"compilerOptions\": { \"outDir\": \"out\", \"sourceMap\": true } }");
        dir.WriteFile("src/index.ts", "console.log(1);\n");
        var platform = new DapPlatform { Workspace = new FakeWorkspace(dir.Uri) };
        var f = new JsDebugAdapterFactory(platform);
        Assert.Equal("tsc", Command(await f.ProvideInitialFilesAsync(dir.Uri, Ct)));

        dir.WriteFile("node_modules/typescript/bin/tsc", "#!/usr/bin/env node\n");
        Directory.CreateDirectory(Path.Combine(dir.Path, "node_modules", ".bin"));
        if (OperatingSystem.IsWindows()) dir.WriteFile("node_modules/.bin/tsc.cmd", "@echo off\n");
        else File.CreateSymbolicLink(Path.Combine(dir.Path, "node_modules", ".bin", "tsc"), "../typescript/bin/tsc"); // as npm links it
        Assert.Equal("${workspaceFolder}/node_modules/.bin/" + (OperatingSystem.IsWindows() ? "tsc.cmd" : "tsc"), Command(await f.ProvideInitialFilesAsync(dir.Uri, Ct)));

        static string? Command(IReadOnlyList<(string RelativePath, string Content)> files)
        {
            using var doc = JsonDocument.Parse(Assert.Single(files).Content);
            return doc.RootElement.GetProperty("tasks")[0].GetProperty("command").GetString();
        }
    }

    // ------------------------------------------------------------------ debug-9: the netcoredbg alias

    [Fact]
    public void The_netcoredbg_alias_is_registered_on_the_same_factory_and_provider_as_coreclr()
    {
        var services = new ServiceCollection();
        services.AddCsCodeDap();
        using var sp = services.BuildServiceProvider();
        var builtins = sp.GetServices<BuiltinDebugAdapter>().ToList();
        var coreclr = Assert.Single(builtins, b => b.Type == "coreclr");
        var alias = Assert.Single(builtins, b => b.Type == "netcoredbg");
        Assert.Same(coreclr.Factory, alias.Factory);
        Assert.Same(coreclr.Provider, alias.Provider);
        Assert.Equal(coreclr.Languages, alias.Languages);
    }

    [Fact]
    public async Task Aliases_are_listed_after_the_debuggers_themselves()
    {
        await using var h = new ServiceHarness(moreBuiltins: p =>
        [
            .. NetCoreDbgAdapterFactory.Builtins(new NetCoreDbgAdapterFactory(p)).Where(b => b.IsAlias),
            .. GdbAdapterFactory.Builtins(new GdbAdapterFactory(p)),
            .. ServiceCollectionExtensions.JsDebugAdapters().Select(a => a(new JsDebugAdapterFactory(p))),
        ]);
        await SessionHarness.Eventually(() => h.Service.DebuggerTypes.Any(t => t.Type == "fake"), 5000, "the debugger list is complete");
        var types = h.Service.DebuggerTypes.Select(t => t.Type).ToList();
        Assert.Equal(["netcoredbg", "node", "rust-gdb"], types.TakeLast(3).Order(StringComparer.Ordinal)); // a "Select debugger" pick offers coreclr first
        Assert.True(types.IndexOf("coreclr") < types.IndexOf("netcoredbg"));
    }

    [Fact]
    public async Task A_netcoredbg_configuration_finds_its_adapter()
    {
        await using var h = new ServiceHarness(moreBuiltins: p => NetCoreDbgAdapterFactory.Builtins(new NetCoreDbgAdapterFactory(p) { PathVariable = () => "" }).Where(b => b.Type == "netcoredbg"));
        h.Settings.Set(NetCoreDbgAdapterFactory.SettingPath, "/nonexistent/netcoredbg");
        var config = ServiceHarness.Config("n", "netcoredbg", extra: new Dictionary<string, object?> { ["program"] = "/nonexistent/app.dll" }, folder: h.Dir.Uri);
        Assert.Null(await h.Service.StartAsync(config, Ct));
        // the factory answered (it could not find the binary in this test), not "no debug adapter is available"
        Assert.DoesNotContain(h.Window.Messages, m => m.Message.Contains("No debug adapter is available", StringComparison.Ordinal));
        Assert.Contains(h.Window.Messages, m => m.Message.Contains("netcoredbg was not found", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ T45: a reused debug terminal

    private sealed class ReusedTerminal : CsCode.Core.Terminal.ITerminal
    {
        public int Kills;
        public int Id => 1;
        public string Title => "Launch App";
        public int? ProcessId { get; set; }
        public bool IsTransient => true;
        public bool HasExited { get; set; }
        public int? ExitCode => HasExited ? 0 : null;
        public string? CurrentDirectory => null;
        public ValueTask SendTextAsync(string text, bool addNewLine = true, CancellationToken ct = default) => ValueTask.CompletedTask;
        public void KillProcess() => Interlocked.Increment(ref Kills);
    }

    private sealed class OneTerminalService(ReusedTerminal terminal) : CsCode.Core.Terminal.ITerminalService
    {
        private int _nextPid = 4000;
        public CsCode.Core.Terminal.TerminalCreateOptions? LastOptions;
        public IReadOnlyList<CsCode.Core.Terminal.ITerminal> Terminals => [terminal];
        public CsCode.Core.Terminal.ITerminal? ActiveTerminal => terminal;
        public ValueTask<CsCode.Core.Terminal.ITerminal> CreateAsync(CsCode.Core.Terminal.TerminalCreateOptions? options = null, CancellationToken ct = default)
        {
            LastOptions = options;
            // like TerminalService with ReuseWhenIdle: the idle terminal of that name is restarted with the new program
            terminal.ProcessId = Interlocked.Increment(ref _nextPid);
            terminal.HasExited = false;
            return ValueTask.FromResult<CsCode.Core.Terminal.ITerminal>(terminal);
        }
        public ValueTask ShowAsync(CsCode.Core.Terminal.ITerminal? t = null, bool preserveFocus = false, CancellationToken ct = default) => ValueTask.CompletedTask;
        public void SetActive(CsCode.Core.Terminal.ITerminal t) { }
        public ValueTask SendTextAsync(string text, bool addNewLine = true, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask KillAsync(CsCode.Core.Terminal.ITerminal t, CancellationToken ct = default) => ValueTask.CompletedTask;
        public IEventSource<CsCode.Core.Terminal.ITerminal> Created { get; } = new Emitter<CsCode.Core.Terminal.ITerminal>();
        public IEventSource<CsCode.Core.Terminal.TerminalExitedEventArgs> Exited { get; } = new Emitter<CsCode.Core.Terminal.TerminalExitedEventArgs>();
        public IEventSource<CsCode.Core.Terminal.ITerminal> Removed { get; } = new Emitter<CsCode.Core.Terminal.ITerminal>();
        public IEventSource<CsCode.Core.Terminal.ITerminal> TitleChanged { get; } = new Emitter<CsCode.Core.Terminal.ITerminal>();
        public IEventSource<CsCode.Core.Terminal.ITerminal?> ActiveChanged { get; } = new Emitter<CsCode.Core.Terminal.ITerminal?>();
    }

    /// <summary>
    /// T45: run one's session ends late (js-debug's parent session ends after its program, later still under load) while run two already runs in
    /// the same, reused terminal: ending run one must not kill run two (it got SIGHUP: exit 129, its output missing).
    /// </summary>
    [Fact]
    public async Task Ending_a_session_late_never_kills_the_next_runs_debuggee_in_the_reused_terminal()
    {
        using var lane = new DebugLane("t45");
        var terminal = new ReusedTerminal();
        var service = new OneTerminalService(terminal);
        var runOne = new DebuggeeLauncher(lane, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, (_, _) => { }, () => service);
        var runTwo = new DebuggeeLauncher(lane, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, (_, _) => { }, () => service);
        var args = new CsCode.Dap.Protocol.DapRunInTerminalArguments { Kind = "integrated", Title = "Launch App", Args = ["node", "app.js"] };

        var first = await runOne.LaunchAsync(args, "Launch App", Ct);
        terminal.HasExited = true; // run one's program ended
        var second = await runTwo.LaunchAsync(args, "Launch App", Ct); // the idle terminal is reused
        Assert.NotEqual(first.ProcessId, second.ProcessId);

        runOne.Dispose(); // run one's session ends now
        Assert.Equal(0, terminal.Kills);
        runTwo.Dispose(); // its own session still ends its debuggee
        Assert.Equal(1, terminal.Kills);
    }

    // ------------------------------------------------------------------ T27: CsCode's DOTNET_ROOT_<ARCH> is not handed down

    private static readonly string Arch = DotnetRootFold.ArchRootVariable;

    [Fact]
    public void The_arch_specific_dotnet_root_is_folded_into_dotnet_root()
    {
        var env = new Dictionary<string, string?> { [Arch] = "/sdk/dotnet", ["PATH"] = "/bin" };
        DotnetRootFold.Apply(env);
        Assert.Equal("/sdk/dotnet", env["DOTNET_ROOT"]);
        Assert.False(env.ContainsKey(Arch));

        var same = new Dictionary<string, string?> { [Arch] = "/sdk/dotnet/", ["DOTNET_ROOT"] = "/sdk/dotnet" };
        DotnetRootFold.Apply(same);
        Assert.Equal(("/sdk/dotnet", false), (same["DOTNET_ROOT"], same.ContainsKey(Arch)));

        var split = new Dictionary<string, string?> { [Arch] = "/sdk/dotnet", ["DOTNET_ROOT"] = "/other/runtime" }; // a deliberate split set-up
        DotnetRootFold.Apply(split);
        Assert.Equal(("/other/runtime", "/sdk/dotnet"), (split["DOTNET_ROOT"], split[Arch]));

        var mine = new Dictionary<string, string?> { [Arch] = "/sdk/dotnet" }; // the launch's own env names it: the user's choice
        DotnetRootFold.Apply(mine, [Arch]);
        Assert.Equal("/sdk/dotnet", mine[Arch]);
        Assert.False(mine.ContainsKey("DOTNET_ROOT"));
    }

    [Fact]
    public async Task A_piped_debuggee_does_not_inherit_the_arch_specific_dotnet_root()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix shell");
        var inherited = Environment.GetEnvironmentVariable(Arch);
        Assert.SkipWhen(string.IsNullOrEmpty(inherited), $"this test process has no {Arch} (dotnet test sets it for the test host)");
        using var lane = new DebugLane("t27");
        var output = new ConcurrentQueue<string>();
        using var launcher = new DebuggeeLauncher(lane, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, (_, text) => output.Enqueue(text));
        await launcher.LaunchAsync(new CsCode.Dap.Protocol.DapRunInTerminalArguments
        {
            Kind = "external",
            Args = ["/bin/sh", "-c", $"echo \"arch=${{{Arch}-unset}} root=${{DOTNET_ROOT-unset}}\""],
        }, "t27", Ct);
        await SessionHarness.Eventually(() => string.Concat(output).Contains('\n'), 10_000, "the debuggee's line");
        var line = string.Concat(output);
        Assert.Contains("arch=unset", line, StringComparison.Ordinal);
        Assert.DoesNotContain("root=unset", line, StringComparison.Ordinal);
    }

    /// <summary>The Remote module's mapper as the debugger sees it: it handles <c>ssh://</c> URIs (a remote workspace's).</summary>
    private sealed class SshMapper : IDebugPathMapper
    {
        public string? ToAdapterPath(Uri uri) => uri.Scheme == "ssh" ? uri.AbsolutePath : null;
        public Uri? ToUri(string adapterPath, Uri? workspaceFolder) => null;
    }

    private static readonly Uri RemoteFolder = new("ssh://build-host/home/me/app");

    /// <summary>
    /// A <c>runInTerminal</c> debuggee gets the adapter's environment as it is (cross-branch review C3), in a local terminal as in a remote
    /// workspace's. A local terminal's launch resolver folds CsCode's DOTNET_ROOT_&lt;ARCH&gt; into the environment it inherits, before
    /// <c>terminal.integrated.env</c> and the caller's variables apply (<see cref="TerminalDotnetRootTests"/>): overrides derived from CsCode's
    /// environment and sent as the caller's repeated that and outranked the user's settings and <c>inheritEnv: false</c>. A remote workspace's
    /// terminal starts from the remote host's environment, where this machine's means nothing.
    /// </summary>
    [Fact]
    public async Task A_terminal_debuggee_gets_the_adapters_environment_as_it_is()
    {
        using var lane = new DebugLane("t27-terminal");
        var service = new OneTerminalService(new ReusedTerminal());
        var args = new CsCode.Dap.Protocol.DapRunInTerminalArguments
        {
            Kind = "integrated", Title = "App", Args = ["/home/me/app/bin/App"], Env = new Dictionary<string, string?> { ["ASPNETCORE_ENVIRONMENT"] = "Development" },
        };
        using (var launcher = new DebuggeeLauncher(lane, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, (_, _) => { }, () => service))
            await launcher.LaunchAsync(args, "App", Ct);
        Assert.Equal(args.Env, service.LastOptions!.Env); // nothing added or removed

        using (var launcher = new DebuggeeLauncher(lane, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, (_, _) => { }, () => service))
            await launcher.LaunchAsync(args with { Env = null }, "App", Ct);
        Assert.Null(service.LastOptions!.Env);
    }

    /// <summary>The same through the debug service, for a session of a local and of a remote workspace.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Through_the_debug_service_a_terminal_debuggee_gets_the_adapters_environment_only(bool remote)
    {
        var terminals = new OneTerminalService(new ReusedTerminal());
        await using var h = remote
            ? new ServiceHarness(workspaceFolder: RemoteFolder, pathMapper: new SshMapper(), terminals: terminals)
            : new ServiceHarness(pathMapper: new SshMapper(), terminals: terminals);
        await h.StartAsync(ServiceHarness.Config("App", folder: h.Dir.Uri));
        await h.AdapterFor(0).RequestFromAdapterAsync("runInTerminal", new CsCode.Dap.Protocol.DapRunInTerminalArguments
        {
            Kind = "integrated", Title = "App", Args = ["/home/me/app/bin/App"], Env = new Dictionary<string, string?> { ["X"] = "1" },
        });
        Assert.Equal(["X"], terminals.LastOptions!.Env!.Keys);
    }

    /// <summary>
    /// The same for a task shown in a terminal, of a local and of a remote workspace: the task's own <c>env</c> only (the local terminal's
    /// resolver folds what it inherits; <see cref="TerminalDotnetRootTests"/>).
    /// </summary>
    [Fact]
    public async Task A_task_in_a_terminal_gets_its_own_environment_only()
    {
        using var dir = new TempDirectory("t27-task-");
        dir.WriteFile(".cscode/tasks.json", """{ "version": "2.0.0", "tasks": [ { "label": "build", "type": "process", "command": "make", "options": { "env": { "MODE": "x" } } } ] }""");
        foreach (var workspace in new[] { RemoteFolder, dir.Uri })
        {
            using var lane = new DebugLane("t27-task");
            var host = new RecordingTaskHost();
            using var tasks = new TaskService(lane, new DapPlatform { Workspace = new FakeWorkspace(workspace), PathMappers = () => new SshMapper(), TaskTerminals = () => host });
            Assert.True((await tasks.RunAsync("build", dir.Uri, Ct)).Succeeded);
            var env = Assert.Single(host.Requests).Environment;
            Assert.Equal(["MODE"], env.Keys);
            Assert.Equal("x", env["MODE"]);
        }
    }

    private sealed class RecordingTaskHost : CsCode.Core.Tasks.ITaskTerminalHost
    {
        public readonly ConcurrentQueue<CsCode.Core.Tasks.TaskTerminalRequest> Requests = new();

        public ValueTask<CsCode.Core.Tasks.ITaskTerminalRun> StartAsync(CsCode.Core.Tasks.TaskTerminalRequest request, CancellationToken ct = default)
        {
            Requests.Enqueue(request);
            return ValueTask.FromResult<CsCode.Core.Tasks.ITaskTerminalRun>(new DoneRun());
        }

        private sealed class DoneRun : CsCode.Core.Tasks.ITaskTerminalRun
        {
            public Task<CsCode.Core.Tasks.TaskTerminalExit> Completion { get; } = Task.FromResult(new CsCode.Core.Tasks.TaskTerminalExit(0, false));
            public void Terminate() { }
            public ValueTask RevealAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        }
    }

    // ------------------------------------------------------------------ T53: an adapter (without a quirk) that ignores start and count

    /// <summary>A container of 3 named and 250 indexed children whose adapter honours <c>filter</c> but ignores <c>start</c> and <c>count</c>.</summary>
    private static void IgnoresStartAndCount(CsCode.Dap.Tests.Fakes.FakeDebugAdapter a)
    {
        var named = Enumerable.Range(0, 3).Select(i => new CsCode.Dap.Protocol.DapVariable { Name = "n" + i, Value = "x" }).ToList();
        var indexed = Enumerable.Range(0, 250).Select(i => new CsCode.Dap.Protocol.DapVariable { Name = $"[{i}]", Value = i.ToString(System.Globalization.CultureInfo.InvariantCulture) }).ToList();
        a.On("variables", r =>
        {
            var args = r.Args<CsCode.Dap.Protocol.DapVariablesArguments>();
            return args.VariablesReference != 1 ? new CsCode.Dap.Protocol.DapVariablesResponse()
                : new CsCode.Dap.Protocol.DapVariablesResponse { Variables = args.Filter switch { "indexed" => indexed, "named" => named, _ => [.. named, .. indexed] } };
        });
    }

    private static string Span(IReadOnlyList<DebugVariable> page) => page.Count == 0 ? "" : $"{page[0].Name}..{page[^1].Name} ({page.Count})";

    [Fact]
    public async Task An_adapter_seen_ignoring_start_and_count_gets_its_pages_cut_by_the_session()
    {
        await using var h = await SessionHarness.StartAsync(IgnoresStartAndCount, config: Cfg.Make(type: "user-adapter"));
        var (_, scopes) = await h.StopAndLoadAsync();
        var reference = scopes[0].VariablesReference;
        Assert.Equal("n0..[96] (100)", Span(await h.Session.GetVariablesAsync(reference, 0, 100, Ct)));
        Assert.Equal("[97]..[196] (100)", Span(await h.Session.GetVariablesAsync(reference, 100, 100, Ct))); // the answer began at n0 again: detected
        Assert.Equal("[197]..[249] (53)", Span(await h.Session.GetVariablesAsync(reference, 200, 100, Ct)));
        Assert.Empty(await h.Session.GetVariablesAsync(reference, 300, 100, Ct));
        Assert.Equal(2, h.Adapter.CountOf("variables")); // the whole list was kept: no request for the later pages
    }

    [Fact]
    public async Task A_later_page_that_reveals_the_adapter_ignores_count_is_asked_for_again_as_a_whole_list()
    {
        await using var h = await SessionHarness.StartAsync(IgnoresStartAndCount, config: Cfg.Make(type: "user-adapter"));
        var (_, scopes) = await h.StopAndLoadAsync();
        // the first request is a later page of the indexed children (the Variables window's "more" after a full first page of named ones)
        Assert.Equal("[100]..[199] (100)", Span(await h.Session.GetVariablesAsync(scopes[0].VariablesReference, "indexed", 100, 100, Ct)));
        Assert.Equal("[200]..[249] (50)", Span(await h.Session.GetVariablesAsync(scopes[0].VariablesReference, "indexed", 200, 100, Ct)));
        Assert.Equal("n0..n2 (3)", Span(await h.Session.GetVariablesAsync(scopes[0].VariablesReference, "named", 0, 100, Ct)));
        Assert.Equal(3, h.Adapter.CountOf("variables")); // the later page, the first page to compare it with, the named children
    }

    /// <summary>js-debug answers an array's indexed page with one extra, non-indexed row: more than <c>count</c> alone is not "ignores paging".</summary>
    [Fact]
    public async Task An_answer_with_an_extra_row_that_honours_start_is_kept_as_its_page()
    {
        await using var h = await SessionHarness.StartAsync(a => a.On("variables", r =>
        {
            var args = r.Args<CsCode.Dap.Protocol.DapVariablesArguments>();
            var from = args.Start ?? 0;
            var page = Enumerable.Range(from, Math.Min(args.Count ?? 1000, 1000 - from)).Select(i => new CsCode.Dap.Protocol.DapVariable { Name = $"{i}", Value = "0" });
            return new CsCode.Dap.Protocol.DapVariablesResponse { Variables = [.. page, new CsCode.Dap.Protocol.DapVariable { Name = "[[Prototype]]", Value = "Array" }] };
        }));
        var (_, scopes) = await h.StopAndLoadAsync();
        var reference = scopes[0].VariablesReference;
        for (var start = 0; start < 1000; start += 100)
            Assert.Equal($"{start}..{start + 99} (100)", Span(await h.Session.GetVariablesAsync(reference, "indexed", start, 100, Ct)));
    }

    [Fact]
    public async Task An_adapter_that_honours_paging_keeps_being_asked_for_pages()
    {
        await using var h = await SessionHarness.StartAsync(a =>
            a.VariablesByReference[1] = [.. Enumerable.Range(0, 250).Select(i => new CsCode.Dap.Protocol.DapVariable { Name = $"[{i}]", Value = "0" })]);
        var (_, scopes) = await h.StopAndLoadAsync();
        Assert.Equal("[100]..[199] (100)", Span(await h.Session.GetVariablesAsync(scopes[0].VariablesReference, 100, 100, Ct)));
        Assert.Equal("[0]..[99] (100)", Span(await h.Session.GetVariablesAsync(scopes[0].VariablesReference, 0, 100, Ct)));
        Assert.All(h.Adapter.RequestsOf("variables").Select(r => r.Args<CsCode.Dap.Protocol.DapVariablesArguments>()), a => Assert.Equal(100, a.Count));
    }

    // ------------------------------------------------------------------ debug-10: an adapter's error answer to stackTrace

    [Fact]
    public async Task An_error_answer_to_the_stop_locations_stack_request_is_handled_not_left_unobserved()
    {
        const string message = "Unable to read the stack of thread 1 (end-pass probe)";
        var unobserved = new ConcurrentQueue<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.Flatten().InnerExceptions.Any(x => x.Message == message)) unobserved.Enqueue(e.Exception);
        }
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await using var h = new ServiceHarness(configureHost: host => host.Configure = (a, _) => a.On("stackTrace", _ => throw new FakeAdapterError(message)));
            var session = await h.StartAsync();
            await h.AdapterFor(0).SendStoppedAsync();
            await h.AdapterFor(0).WaitForRequestAsync("stackTrace");
            await SessionHarness.Eventually(() => session.State == DebugSessionState.Stopped);
            await Task.Delay(200, Ct);
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            Assert.Empty(unobserved);
            Assert.Null(h.Service.StopLocation);
            await h.AdapterFor(0).SendEventAsync("continued", new { threadId = 1, allThreadsContinued = true });
            await SessionHarness.Eventually(() => session.State == DebugSessionState.Running); // the session goes on
        }
        finally { TaskScheduler.UnobservedTaskException -= OnUnobserved; }
    }
}
