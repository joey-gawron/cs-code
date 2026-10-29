using System.Text;
using System.Text.Json;
using CsCode.Core.Contributions;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Panels.Search.Engine;
using CsCode.Panels.Search.Settings;
using CsCode.Panels.Search.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Panels.Search;

/// <summary>
/// The panels' composition point: registers this module's settings and commands once, owns the shared engine services, and hands requests
/// ("find this text", "show setting X") to whichever view is currently open, or parks them for the next view to pick up. Resolve it once at
/// startup (the tool windows depend on it, so enumerating <c>IToolWindow</c>s does that). Commands that the shell already registered are not
/// replaced.
/// </summary>
public sealed class SearchPanelsHost : IDisposable
{
    public const string FindViewId = "search";
    public const string SettingsViewId = "settings";
    public const string KeybindingsViewId = "keybindings";

    public const string FindInFiles = "workbench.action.findInFiles";
    public const string ReplaceInFiles = "workbench.action.replaceInFiles";
    public const string NextResult = "search.action.focusNextSearchResult";
    public const string PreviousResult = "search.action.focusPreviousSearchResult";
    public const string ClearResults = "search.action.clearSearchResults";
    public const string CollapseResults = "search.action.collapseSearchResults";
    public const string FindInFolder = "search.action.findInFolder";
    public const string OpenSettings = "workbench.action.openSettings";
    public const string OpenSettingsJson = "workbench.action.openSettingsJson";
    public const string OpenKeybindings = "workbench.action.openGlobalKeybindings";
    public const string OpenKeybindingsJson = "workbench.action.openGlobalKeybindingsFile";

    private readonly IServiceProvider _sp;
    private readonly Lazy<SearchServices> _services;
    private readonly DisposableStore _store = new();
    private FindInFilesViewModel? _liveFind;
    private FindRequest? _pendingFind;
    private SettingsViewModel? _liveSettings;
    private string? _pendingSettingsQuery;

    public SearchPanelsHost(IServiceProvider services)
    {
        _sp = services;
        _services = new Lazy<SearchServices>(() => SearchServices.From(_sp));
        if (_sp.GetService<IConfigurationService>() is { } config) _store.Add(config.RegisterProperties(SearchSettings.Properties()));
        if (_sp.GetService<ICommandService>() is { } commands) RegisterCommands(commands);
        if (_sp.GetService<IContributionRegistry>() is { } contributions) _store.Add(contributions.Contribute(ContributionSources.Builtin, AppContext.BaseDirectory, DefaultKeybindings));
    }

    /// <summary>
    /// F4 / Shift+F4 step through the results from anywhere (the editor included) while the panel is open. Neither preset keymap binds them,
    /// and the panel's own key handler only sees keys while it has focus; the platform's keybinding service layers built-in contributions
    /// over the preset (and the user's keybindings.json over both, so they can be removed or rebound there).
    /// </summary>
    public static IReadOnlyList<KeybindingContribution> DefaultKeybindings { get; } =
    [
        new(NextResult, "f4", When: SearchViewletVisible),
        new(PreviousResult, "shift+f4", When: SearchViewletVisible),
    ];

    /// <summary>Context key set while the Find in Files view exists.</summary>
    public const string SearchViewletVisible = "searchViewletVisible";

    /// <summary>Shared services (created on first use: the engine and replace service own threads, so they are not built until a view needs them).</summary>
    public SearchServices Services => _services.Value;

    public FindInFilesViewModel? LiveFind => _liveFind;
    public SettingsViewModel? LiveSettings => _liveSettings;

    // ------------------------------------------------------------------------------- view lifetime

    public FindInFilesViewModel CreateFindViewModel()
    {
        var vm = new FindInFilesViewModel(Services);
        _liveFind = vm;
        Services.ContextKeys?.Set(SearchViewletVisible, true);
        if (_pendingFind is { } req)
        {
            _pendingFind = null;
            vm.Apply(req);
        }
        return vm;
    }

    public void ReleaseFindViewModel(FindInFilesViewModel vm)
    {
        if (ReferenceEquals(_liveFind, vm))
        {
            _liveFind = null;
            Services.ContextKeys?.Set(SearchViewletVisible, false);
        }
        vm.Dispose();
    }

    public SettingsViewModel CreateSettingsViewModel()
    {
        var vm = new SettingsViewModel(Services);
        _liveSettings = vm;
        if (_pendingSettingsQuery is { } q)
        {
            _pendingSettingsQuery = null;
            vm.SearchText = q;
        }
        return vm;
    }

    public void ReleaseSettingsViewModel(SettingsViewModel vm)
    {
        if (ReferenceEquals(_liveSettings, vm)) _liveSettings = null;
        vm.Dispose();
    }

    // ----------------------------------------------------------------------------------- commands

    private void RegisterCommands(ICommandService commands)
    {
        void Reg(string id, string title, Func<CommandInvocation, ValueTask<object?>> handler)
        {
            if (commands.Has(id)) return; // the shell (or another module) owns it
            _store.Add(commands.Register(id, handler, new CommandMetadata(id, title)));
        }

        Reg(FindInFiles, "Find in Files", inv => FindAsync(inv, replace: false));
        Reg(ReplaceInFiles, "Replace in Files", inv => FindAsync(inv, replace: true));
        Reg(NextResult, "Focus Next Search Result", async _ => { if (_liveFind is { } f) await f.NextResultAsync(); return null; });
        Reg(PreviousResult, "Focus Previous Search Result", async _ => { if (_liveFind is { } f) await f.PreviousResultAsync(); return null; });
        Reg(ClearResults, "Clear Search Results", _ => { _liveFind?.Clear(); return ValueTask.FromResult<object?>(null); });
        Reg(CollapseResults, "Collapse All Search Results", _ => { _liveFind?.ToggleCollapseAll(); return ValueTask.FromResult<object?>(null); });
        Reg(FindInFolder, "Find in Folder…", FindInFolderAsync);
        Reg(OpenSettings, "Open Settings", async inv =>
        {
            var query = inv.Arg<string>(0);
            if (!string.IsNullOrWhiteSpace(query))
            {
                if (_liveSettings is { } s) s.SearchText = query; else _pendingSettingsQuery = query;
            }
            await ShowAsync(SettingsViewId);
            return null;
        });
        Reg(OpenSettingsJson, "Open Settings (JSON)", async _ => { await OpenJsonFileAsync("settings.json"); return null; });
        Reg(OpenKeybindings, "Open Keyboard Shortcuts", async _ => { await ShowAsync(KeybindingsViewId); return null; });
        Reg(OpenKeybindingsJson, "Open Keyboard Shortcuts (JSON)", async _ => { await OpenJsonFileAsync("keybindings.json"); return null; });
    }

    private async ValueTask<object?> FindAsync(CommandInvocation inv, bool replace)
    {
        var request = ParseRequest(inv) with { ShowReplace = replace };
        if (request.Query is null && SeedFromSelection() is { } seed) request = request with { Query = seed };
        if (_liveFind is { } vm) vm.Apply(request);
        else _pendingFind = request;
        await ShowAsync(FindViewId);
        return null;
    }

    private async ValueTask<object?> FindInFolderAsync(CommandInvocation inv)
    {
        var arg = inv.Arg<string>(0);
        string? include = null;
        if (arg is not null && Uri.TryCreate(arg, UriKind.Absolute, out var uri))
        {
            var rel = new RootSet(_services.Value.Workspace.Folders).ToRelative(uri).Trim('/');
            include = rel.Length == 0 ? "" : "./" + rel;
        }
        var request = new FindRequest { Include = include, TriggerSearch = false };
        if (_liveFind is { } vm) vm.Apply(request); else _pendingFind = request;
        await ShowAsync(FindViewId);
        return null;
    }

    private async Task ShowAsync(string viewId)
    {
        var views = Services.Views;
        if (views is not null) await views.ShowViewAsync(viewId);
    }

    /// <summary>VS Code's argument object (<c>query</c>, <c>replace</c>, <c>filesToInclude</c>, <c>isRegex</c>...) or a plain query string.</summary>
    internal static FindRequest ParseRequest(CommandInvocation inv)
    {
        if (inv.Args.Count == 0 || inv.Args[0] is null) return new FindRequest();
        var raw = inv.Args[0];
        if (raw is string s) return new FindRequest { Query = s };
        JsonElement e;
        try { e = raw is JsonElement je ? je : JsonSerializer.SerializeToElement(raw); }
        catch (NotSupportedException) { return new FindRequest(); }
        if (e.ValueKind == JsonValueKind.String) return new FindRequest { Query = e.GetString() };
        if (e.ValueKind != JsonValueKind.Object) return new FindRequest();

        string? Str(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool? Bool(string name) => e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
        return new FindRequest
        {
            Query = Str("query"),
            Replace = Str("replace"),
            Include = Str("filesToInclude"),
            Exclude = Str("filesToExclude"),
            IsRegex = Bool("isRegex"),
            MatchCase = Bool("isCaseSensitive"),
            WholeWord = Bool("matchWholeWord"),
            PreserveCase = Bool("preserveCase"),
            UseExcludeSettingsAndIgnoreFiles = Bool("useExcludeSettingsAndIgnoreFiles"),
            TriggerSearch = Bool("triggerSearch") ?? false,
        };
    }

    /// <summary>The selected text of the active editor (single line, trimmed), else the word at the caret.</summary>
    internal string? SeedFromSelection()
    {
        var s = Services;
        if (s.Selection?.SelectedText is { Length: > 0 } text && !text.Contains('\n') && !text.Contains('\r'))
            return text.Length > 200 ? text[..200] : text;
        if (s.Navigation is { ActiveDocument: { } doc, ActiveCaret: { } caret })
            return WordAt(doc.GetLineText(caret.Line), caret.Character);
        return null;
    }

    internal static string? WordAt(string line, int column)
    {
        if (line.Length == 0) return null;
        column = Math.Clamp(column, 0, line.Length);
        bool W(int i) => i >= 0 && i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_');
        var start = column;
        while (W(start - 1)) start--;
        var end = column;
        while (W(end)) end++;
        return end > start ? line[start..end] : null;
    }

    private async Task OpenJsonFileAsync(string fileName)
    {
        var s = Services;
        var config = s.Configuration;
        if (config is null || s.Navigation is null) return;
        var dir = Path.GetDirectoryName(config.UserSettingsPath);
        if (dir is null) return;
        var path = fileName == "settings.json" ? config.UserSettingsPath : Path.Combine(dir, fileName);
        var uri = new Uri(new Uri("file:///"), path.StartsWith('/') ? path : "/" + path.Replace('\\', '/'));
        var fs = s.FileSystem ?? s.Workspace.FileSystem;
        try
        {
            if (await fs.StatAsync(uri) is null)
                await fs.WriteFileAsync(uri, Encoding.UTF8.GetBytes(fileName == "settings.json" ? "{\n}\n" : "[\n]\n"), createParents: true);
            await s.Navigation.OpenAsync(uri);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (s.Windows is not null) await s.Windows.ShowMessageAsync(MessageSeverity.Error, $"Could not open {fileName}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _store.Dispose();
        if (_services.IsValueCreated)
        {
            _services.Value.Engine.Dispose();
            _services.Value.Replace.Dispose();
        }
    }
}
