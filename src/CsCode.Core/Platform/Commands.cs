using System.Text.Json;
using CsCode.Core.Lifecycle;

namespace CsCode.Core.Platform;

/// <summary>
/// Global command table (same model as vscode.commands). Commands are string ids with JSON-friendly arguments so
/// they can be invoked from keybindings, menus, the command palette, LSP code actions and JS extensions alike.
/// </summary>
public interface ICommandService
{
    IDisposable Register(string id, Func<CommandInvocation, ValueTask<object?>> handler, CommandMetadata? metadata = null);
    bool Has(string id);
    IReadOnlyList<CommandMetadata> All { get; }
    ValueTask<object?> ExecuteAsync(string id, params object?[] args);
    IEventSource<string> Registered { get; }
}

/// <summary>Thrown by <see cref="ICommandService.ExecuteAsync"/> for an unknown command id.</summary>
public sealed class CommandNotFoundException(string id) : InvalidOperationException($"Command '{id}' not found")
{
    public string CommandId { get; } = id;
}

public sealed record CommandInvocation(string Id, IReadOnlyList<object?> Args, CancellationToken Cancellation = default)
{
    /// <summary>Argument <paramref name="index"/> converted to <typeparamref name="T"/> (JsonElement-aware); default if absent.</summary>
    public T? Arg<T>(int index)
    {
        if (index >= Args.Count || Args[index] is null) return default;
        var a = Args[index];
        if (a is T t) return t;
        if (a is JsonElement je) return je.Deserialize<T>(CoreJson.Options);
        return (T?)Convert.ChangeType(a, typeof(T));
    }
}

/// <summary>Palette/menu metadata. Registered either with the handler or from an extension's `contributes.commands`.</summary>
public sealed record CommandMetadata(string Id, string? Title = null, string? Category = null, string? Icon = null, string? Enablement = null);

public static class CoreJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}

/// <summary>Well-known command ids shared between shell, editor and contributions. Keep ids VS Code–compatible.</summary>
public static class KnownCommands
{
    public const string CommandPalette = "workbench.action.showCommands";
    public const string QuickOpen = "workbench.action.quickOpen";
    public const string OpenFolder = "workbench.action.files.openFolder";
    public const string OpenFile = "workbench.action.files.openFile";
    public const string Save = "workbench.action.files.save";
    public const string SaveAll = "workbench.action.files.saveAll";
    public const string NewUntitledFile = "workbench.action.files.newUntitledFile";
    public const string CloseActiveEditor = "workbench.action.closeActiveEditor";
    public const string Find = "actions.find";
    public const string Replace = "editor.action.startFindReplace";
    public const string GotoDefinition = "editor.action.revealDefinition";
    public const string FindReferences = "editor.action.goToReferences";
    public const string Rename = "editor.action.rename";
    public const string FormatDocument = "editor.action.formatDocument";
    public const string TriggerSuggest = "editor.action.triggerSuggest";
    public const string TriggerParameterHints = "editor.action.triggerParameterHints";
    public const string QuickFix = "editor.action.quickFix";
    public const string DebugStart = "workbench.action.debug.start";
    public const string DebugRun = "workbench.action.debug.run";
    public const string DebugStop = "workbench.action.debug.stop";
    public const string DebugRestart = "workbench.action.debug.restart";
    public const string DebugContinue = "workbench.action.debug.continue";
    public const string DebugPause = "workbench.action.debug.pause";
    public const string DebugStepOver = "workbench.action.debug.stepOver";
    public const string DebugStepInto = "workbench.action.debug.stepInto";
    public const string DebugStepOut = "workbench.action.debug.stepOut";
    public const string DebugToggleBreakpoint = "editor.debug.action.toggleBreakpoint";
    public const string ToggleTerminal = "workbench.action.terminal.toggleTerminal";
    public const string OpenSettings = "workbench.action.openSettings";
    public const string SelectTheme = "workbench.action.selectTheme";
    public const string InstallExtension = "workbench.extensions.installExtension";
    public const string ShowExtensions = "workbench.view.extensions";
    public const string ReloadWindow = "workbench.action.reloadWindow";
    public const string Quit = "workbench.action.quit";
}

/// <summary>
/// Context keys + `when`-clause evaluation (VS Code syntax: <c>editorTextFocus &amp;&amp; !editorReadonly</c>,
/// <c>resourceExtname == .cs</c>, <c>config.editor.minimap.enabled</c>, <c>debugState == 'stopped'</c>, regex match,
/// <c>in</c>, <c>&lt;</c> <c>&gt;</c>, parentheses).
/// </summary>
public interface IContextKeyService
{
    void Set(string key, object? value);
    object? Get(string key);
    /// <summary>Evaluates a when-clause; null/empty means true. Unknown keys are falsy.</summary>
    bool Evaluate(string? whenClause);
    IEventSource<IReadOnlyCollection<string>> Changed { get; }
    /// <summary>Child scope (e.g. per focused editor/panel) that overlays but does not mutate the parent.</summary>
    IContextKeyService CreateScope();
}
