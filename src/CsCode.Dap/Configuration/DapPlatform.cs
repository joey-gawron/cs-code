using System.Text.Json;
using CsCode.Core.Contributions;
using CsCode.Core.Debugging;
using CsCode.Core.Diagnostics;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Tasks;
using CsCode.Core.Terminal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCode.Dap.Configuration;

/// <summary>
/// The Core services the DAP module consumes, all OPTIONAL: every one is resolved with <c>GetService</c> so the module degrades
/// (no prompts, no persistence, no output channel) instead of failing when the shell/platform pieces are not registered.
/// </summary>
public sealed class DapPlatform
{
    public IWorkspaceService? Workspace { get; init; }
    public IConfigurationService? Configuration { get; init; }
    public IStateStorage? State { get; init; }
    public IWindowService? Window { get; init; }
    public ICommandService? Commands { get; init; }
    public INavigationService? Navigation { get; init; }
    public IOutputService? Output { get; init; }
    public IContextKeyService? ContextKeys { get; init; }
    public IContributionRegistry? Contributions { get; init; }
    public IAppPaths? Paths { get; init; }
    public IActiveSelectionProvider? Selection { get; init; }
    public IMemoryMonitor? Memory { get; init; }
    public ILoggerFactory? LoggerFactory { get; init; }
    /// <summary>The integrated terminal, when one is composed: <c>runInTerminal</c> (kind integrated) runs the debuggee in it. Resolved lazily (the
    /// terminal module is built with the shell's tool windows, after the debug service).</summary>
    public Func<ITerminalService?>? Terminals { get; init; }
    /// <summary>The tasks' terminal presentation, when the integrated terminal is composed: tasks then run in terminals (resolved lazily).</summary>
    public Func<ITaskTerminalHost?>? TaskTerminals { get; init; }
    /// <summary>Where tasks' problem matchers publish (the Error List reads it).</summary>
    public IDiagnosticService? Diagnostics { get; init; }
    /// <summary>The shell's view activator (a task's <c>revealProblems</c> shows the Error List), resolved lazily.</summary>
    public Func<IViewActivator?>? Views { get; init; }
    /// <summary>URIs of a remote workspace ⇄ the paths its adapters use (the Remote module's mapper); resolved lazily, optional.</summary>
    public Func<IDebugPathMapper?>? PathMappers { get; init; }

    public IDebugPathMapper? PathMapper => PathMappers?.Invoke();

    public ILogger Logger(string category) => LoggerFactory?.CreateLogger(category) ?? NullLogger.Instance;

    public static DapPlatform From(IServiceProvider sp) => new()
    {
        Workspace = sp.GetService<IWorkspaceService>(),
        Configuration = sp.GetService<IConfigurationService>(),
        State = sp.GetService<IStateStorage>(),
        Window = sp.GetService<IWindowService>(),
        Commands = sp.GetService<ICommandService>(),
        Navigation = sp.GetService<INavigationService>(),
        Output = sp.GetService<IOutputService>(),
        ContextKeys = sp.GetService<IContextKeyService>(),
        Contributions = sp.GetService<IContributionRegistry>(),
        Paths = sp.GetService<IAppPaths>(),
        Selection = sp.GetService<IActiveSelectionProvider>(),
        Memory = sp.GetService<IMemoryMonitor>(),
        LoggerFactory = sp.GetService<ILoggerFactory>(),
        Terminals = () => sp.GetService<ITerminalService>(),
        TaskTerminals = () => sp.GetService<ITaskTerminalHost>(),
        Diagnostics = sp.GetService<IDiagnosticService>(),
        Views = () => sp.GetService<IViewActivator>(),
        PathMappers = () => sp.GetService<IDebugPathMapper>(),
    };

    /// <summary>Folder to use when a caller passes none: the first workspace folder.</summary>
    public Uri? PrimaryFolder => Workspace?.PrimaryFolder;

    /// <summary>
    /// Whether new terminals run on another machine: the workspace is remote (its primary folder is one the Remote module maps), which is the
    /// rule of the Remote module's terminal backend (RemoteTerminals: every terminal, <c>runInTerminal</c> debuggees and tasks in terminals
    /// included). Such a terminal starts from THAT machine's environment, so nothing derived from CsCode's own environment (the
    /// <see cref="DotnetRootFold"/>) is sent to it.
    /// </summary>
    internal bool TerminalsAreRemote => PrimaryFolder is { IsFile: false } folder && PathMapper?.ToAdapterPath(folder) is not null;

    internal static string NormalizeDir(string path) => path.Length > 1 ? path.TrimEnd('/', '\\') : path;

    /// <summary>Builds the context variable substitution consults. <paramref name="inputs"/> are the launch.json/tasks.json <c>inputs[]</c> definitions.</summary>
    internal VariableContext CreateVariableContext(Uri? folder, IReadOnlyDictionary<string, JsonElement> inputs, Func<CancellationToken, ValueTask<string?>>? defaultBuildTask)
    {
        var folderUri = folder ?? PrimaryFolder;
        var doc = Navigation?.ActiveDocument;
        var window = Window;
        var commands = Commands;
        var mapper = PathMapper;
        // A remote workspace's folders and files resolve to their paths on the remote host (where its adapters and tasks run).
        string? DirOf(Uri u) => u.IsFile ? NormalizeDir(u.LocalPath) : mapper?.ToAdapterPath(u) is { } p ? NormalizeDir(p) : null;
        return new VariableContext
        {
            WorkspaceFolder = folderUri is { } f ? DirOf(f) : null,
            WorkspaceFolderByName = name => Workspace?.Folders.Select(DirOf).OfType<string>()
                .FirstOrDefault(p => string.Equals(Path.GetFileName(p), name, StringComparison.Ordinal)),
            ActiveFile = doc is null ? null : doc.Uri.IsFile ? doc.Uri.LocalPath : mapper?.ToAdapterPath(doc.Uri),
            ActiveLine = doc is not null && Navigation?.ActiveCaret is { } caret ? caret.Line + 1 : null,
            SelectedText = Selection is null ? null : () => Selection.SelectedText,
            Cwd = Environment.CurrentDirectory,
            Config = Configuration is null ? null : key => Configuration.GetRaw(key) is { } raw
                ? (raw.ValueKind == JsonValueKind.String ? raw.GetString() : raw.GetRawText())
                : null,
            Command = commands is null ? null : async (id, ct) => ToText(await commands.ExecuteAsync(id)),
            Input = async (id, ct) => await ResolveInputAsync(id, inputs, window, commands, ct),
            DefaultBuildTask = defaultBuildTask,
        };
    }

    private static string? ToText(object? result) => result switch
    {
        null => null,
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        JsonElement je => je.GetRawText(),
        _ => Convert.ToString(result, System.Globalization.CultureInfo.InvariantCulture),
    };

    private static async ValueTask<string?> ResolveInputAsync(string id, IReadOnlyDictionary<string, JsonElement> inputs, IWindowService? window, ICommandService? commands, CancellationToken ct)
    {
        if (!inputs.TryGetValue(id, out var def))
            throw new VariableResolutionException($"Variable ${{input:{id}}}: no input with id '{id}' is defined in the inputs section.");
        string? Str(string key) => def.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var type = Str("type");
        var description = Str("description");
        switch (type)
        {
            case "promptString":
                if (window is null) throw new VariableResolutionException($"Variable ${{input:{id}}}: prompting needs a window.");
                return await window.ShowInputBoxAsync(new InputBoxOptions(Title: description, Prompt: description, Value: Str("default"),
                    Password: def.TryGetProperty("password", out var pw) && pw.ValueKind == JsonValueKind.True), ct);
            case "pickString":
            {
                if (window is null) throw new VariableResolutionException($"Variable ${{input:{id}}}: prompting needs a window.");
                var items = new List<QuickPickItem>();
                if (def.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
                    foreach (var o in opts.EnumerateArray())
                    {
                        if (o.ValueKind == JsonValueKind.String) items.Add(new QuickPickItem(o.GetString()!, Tag: o.GetString()));
                        else if (o.ValueKind == JsonValueKind.Object && o.TryGetProperty("value", out var val) && val.ValueKind == JsonValueKind.String)
                            items.Add(new QuickPickItem(o.TryGetProperty("label", out var lab) && lab.ValueKind == JsonValueKind.String ? lab.GetString()! : val.GetString()!, Tag: val.GetString()));
                    }
                var picked = await window.ShowQuickPickAsync(items, new QuickPickOptions(Title: description, Placeholder: description), ct);
                return picked?.Tag as string;
            }
            case "command":
            {
                if (commands is null) throw new VariableResolutionException($"Variable ${{input:{id}}}: commands are not available.");
                var cmd = Str("command") ?? throw new VariableResolutionException($"Variable ${{input:{id}}}: the input has no 'command'.");
                object?[] args = def.TryGetProperty("args", out var a) ? [a.Clone()] : [];
                return ToText(await commands.ExecuteAsync(cmd, args));
            }
            default:
                throw new VariableResolutionException($"Variable ${{input:{id}}}: unsupported input type '{type}'.");
        }
    }
}
