using System.Text.Json;
using CsCode.Core.Lifecycle;

namespace CsCode.Core.Contributions;

// Typed contribution points: languages, grammars, colour themes, snippets, keybindings, menus, debuggers.
//
// CsCode has NO extension system (no plugin host, no marketplace). These points are fed by two sources only:
//   * built-ins shipped with the app            (SourceId = ContributionSources.Builtin)
//   * user files under IAppPaths.UserDataDir     (SourceId = ContributionSources.User): themes/*.json (VS Code theme JSON format),
//     grammars/ (TextMate grammars + a small languages.json), snippets/*.json, keybindings.json, settings.json
// Paths in records are ABSOLUTE. Consumers never parse those files themselves; the module that owns a file kind
// (Editor: themes/grammars/snippets/languages, Platform: keybindings, Dap: debuggers) loads it and contributes here.

public static class ContributionSources
{
    public const string Builtin = "cscode.builtin";
    public const string User = "cscode.user";
}

public sealed record Contributed<T>(T Value, string SourceId, string SourceDirectory) where T : class;

public interface IContributionSet<T> where T : class
{
    IReadOnlyList<Contributed<T>> Items { get; }
    IEventSource<IReadOnlyList<Contributed<T>>> Changed { get; }
}

public interface IContributionRegistry
{
    IContributionSet<T> Of<T>() where T : class;
    IDisposable Contribute<T>(string sourceId, string sourceDirectory, IEnumerable<T> items) where T : class;
    /// <summary>Removes everything a source contributed (e.g. the user reloads their themes folder).</summary>
    void RemoveSource(string sourceId);
}

public sealed class ContributionRegistry : IContributionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<Type, object> _sets = new();

    public IContributionSet<T> Of<T>() where T : class => Set<T>();

    public IDisposable Contribute<T>(string sourceId, string sourceDirectory, IEnumerable<T> items) where T : class
    {
        var added = items.Select(v => new Contributed<T>(v, sourceId, sourceDirectory)).ToList();
        var set = Set<T>();
        set.Add(added);
        return Disposable.Create(() => set.Remove(added));
    }

    public void RemoveSource(string sourceId)
    {
        object[] all;
        lock (_gate) all = _sets.Values.ToArray();
        foreach (var s in all) ((ISet)s).RemoveSource(sourceId);
    }

    private ContributionSet<T> Set<T>() where T : class
    {
        lock (_gate)
        {
            if (!_sets.TryGetValue(typeof(T), out var s)) _sets[typeof(T)] = s = new ContributionSet<T>();
            return (ContributionSet<T>)s;
        }
    }

    private interface ISet { void RemoveSource(string sourceId); }

    private sealed class ContributionSet<T> : IContributionSet<T>, ISet where T : class
    {
        private readonly object _gate = new();
        private readonly Emitter<IReadOnlyList<Contributed<T>>> _changed = new();
        private Contributed<T>[] _items = [];

        public IReadOnlyList<Contributed<T>> Items => Volatile.Read(ref _items);
        public IEventSource<IReadOnlyList<Contributed<T>>> Changed => _changed;

        public void Add(IReadOnlyCollection<Contributed<T>> added)
        {
            if (added.Count == 0) return;
            lock (_gate) _items = [.. _items, .. added];
            _changed.Fire(Items);
        }

        public void Remove(IReadOnlyCollection<Contributed<T>> removed)
        {
            bool changed;
            lock (_gate)
            {
                var next = _items.Where(i => !removed.Contains(i)).ToArray();
                changed = next.Length != _items.Length;
                _items = next;
            }
            if (changed) _changed.Fire(Items);
        }

        public void RemoveSource(string sourceId)
        {
            bool changed;
            lock (_gate)
            {
                var next = _items.Where(i => i.SourceId != sourceId).ToArray();
                changed = next.Length != _items.Length;
                _items = next;
            }
            if (changed) _changed.Fire(Items);
        }
    }
}

public sealed record LanguageContribution(
    string Id,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> Extensions,
    IReadOnlyList<string> Filenames,
    IReadOnlyList<string> FilenamePatterns,
    string? FirstLine = null,
    string? ConfigurationPath = null,
    IReadOnlyList<string>? MimeTypes = null);

public sealed record GrammarContribution(
    string? LanguageId,
    string ScopeName,
    string Path,
    IReadOnlyDictionary<string, string>? EmbeddedLanguages = null,
    IReadOnlyDictionary<string, string>? TokenTypes = null,
    IReadOnlyList<string>? InjectTo = null);

/// <param name="UiTheme">vs | vs-dark | hc-black | hc-light (VS Code theme JSON convention).</param>
public sealed record ColorThemeContribution(string Id, string Label, string UiTheme, string Path);

public sealed record SnippetContribution(string LanguageId, string Path);

public sealed record KeybindingContribution(string Command, string Key, string? Mac = null, string? Linux = null, string? Win = null, string? When = null, JsonElement? Args = null);

/// <summary>
/// One item of a menu (VS Code's <c>menus</c> contribution point). <paramref name="Group"/> follows VS Code: <c>"name@order"</c>; groups become
/// separator-delimited sections (<c>navigation</c> first, then by name), items sort by order within a group. <paramref name="When"/> false hides
/// the item; a command nobody registered hides it too; the command's <see cref="Platform.CommandMetadata.Enablement"/> disables it.
/// </summary>
public sealed record MenuContribution(string MenuId, string Command, string? Group = null, string? When = null, string? Alt = null)
{
    // ---- additive members (clipboard and context menus wave) ----

    /// <summary>Menu text in the host's wording (Visual Studio's for CsCode; <c>_</c> marks the access key). Default: the command's title.</summary>
    public string? Title { get; init; }

    /// <summary>Icon key (a Codicon name, see CsCode.Ui's icon catalogue), or null.</summary>
    public string? Icon { get; init; }

    /// <summary>When set, the item opens the submenu with this id (declared by a <see cref="SubmenuContribution"/>); <see cref="Command"/> is then only a key.</summary>
    public string? Submenu { get; init; }

    /// <summary>Arguments for the command. Null: the menu's context (the clicked element, e.g. the document's URI for <c>editor/context</c>).</summary>
    public IReadOnlyList<object?>? Args { get; init; }

    /// <summary>
    /// Optional asynchronous enablement (a language-server round trip, a clipboard query): the item opens disabled and is enabled when this
    /// returns true; it is never awaited on the UI thread. Receives the menu's context arguments; cancelled when the menu closes.
    /// </summary>
    public Func<IReadOnlyList<object?>, CancellationToken, ValueTask<bool>>? EnablementCheck { get; init; }

    /// <summary>Optional check mark (a `when` clause over context keys).</summary>
    public string? Toggled { get; init; }
}

/// <summary>Declares a submenu (VS Code's <c>submenus</c>): its items are the <see cref="MenuContribution"/>s whose MenuId is <paramref name="Id"/>.</summary>
public sealed record SubmenuContribution(string Id, string Title, string? Icon = null);

/// <summary>Menu ids hosts build context menus from (VS Code's names where VS Code has the menu).</summary>
public static class MenuIds
{
    /// <summary>The editor's text area (context: the document's URI).</summary>
    public const string EditorContext = "editor/context";
    /// <summary>A document tab (context: the document's URI when it is a file).</summary>
    public const string EditorTitleContext = "editor/title/context";
    /// <summary>An Explorer node (context: the node's URI).</summary>
    public const string ExplorerContext = "explorer/context";
    /// <summary>An Error List row (context: the diagnostic's URI).</summary>
    public const string ProblemsContext = "problems/context";
    /// <summary>The Output window.</summary>
    public const string OutputContext = "output/context";
    /// <summary>A Find Results / Find in Files row.</summary>
    public const string SearchContext = "search/context";
    /// <summary>A tool window's title bar or tab (context: the tool window id).</summary>
    public const string ToolWindowTitleContext = "view/title/context";
    /// <summary>The status bar (context: the item id).</summary>
    public const string StatusBarContext = "statusBar/context";
}

/// <summary>A debug adapter the user configured (setting `cscode.debuggers`) — enough to launch it and offer launch.json snippets.</summary>
public sealed record DebuggerContribution(
    string Type,
    string Label,
    string? Program = null,
    string? Runtime = null,
    IReadOnlyList<string>? Args = null,
    IReadOnlyDictionary<string, string>? PlatformPrograms = null,
    IReadOnlyList<string>? Languages = null,
    JsonElement? ConfigurationAttributes = null,
    JsonElement? InitialConfigurations = null,
    JsonElement? ConfigurationSnippets = null,
    JsonElement? Variables = null);

/// <summary>Languages in which the gutter breakpoint toggle is enabled.</summary>
public sealed record BreakpointLanguageContribution(string Language);
