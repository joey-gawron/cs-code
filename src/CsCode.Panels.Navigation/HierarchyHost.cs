using CsCode.Core.Contributions;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Panels.Navigation;

/// <summary>
/// The Hierarchy window's composition point (one per window): owns the <see cref="HierarchyModel"/>, registers VS Code's call and type
/// hierarchy commands (<c>editor.showCallHierarchy</c>, <c>editor.showIncomingCalls</c>, <c>editor.showOutgoingCalls</c>,
/// <c>editor.showTypeHierarchy</c>, <c>editor.showSupertypes</c>, <c>editor.showSubtypes</c>) and the window's own (refresh, toggle direction,
/// clear), and contributes the editor's right-click items in Visual Studio's wording ("View Call Hierarchy"). A command asks the providers
/// of the active editor's document (<see cref="INavigationService"/>) to prepare the symbol at the caret — off the UI thread, bounded — and
/// shows the answer in the window. Every service is optional.
/// </summary>
public sealed class HierarchyHost : IDisposable
{
    public const string ViewId = "hierarchy";
    public const string FocusView = "cscode.hierarchy.focus";
    public const string ShowCallHierarchy = "editor.showCallHierarchy";
    public const string ShowIncomingCalls = "editor.showIncomingCalls";
    public const string ShowOutgoingCalls = "editor.showOutgoingCalls";
    public const string ShowTypeHierarchy = "editor.showTypeHierarchy";
    public const string ShowSupertypes = "editor.showSupertypes";
    public const string ShowSubtypes = "editor.showSubtypes";
    public const string Refresh = "cscode.hierarchy.refresh";
    public const string ToggleDirection = "cscode.hierarchy.toggleDirection";
    public const string Clear = "cscode.hierarchy.clear";
    /// <summary>VS Code's provider context keys (the editor keeps them for the focused document).</summary>
    public const string HasCallHierarchyKey = "editorHasCallHierarchyProvider";
    public const string HasTypeHierarchyKey = "editorHasTypeHierarchyProvider";

    internal const string EmptyHint = "Use View Call Hierarchy or View Type Hierarchy on a symbol in the editor (right-click menu).";
    internal static TimeSpan PrepareTimeout { get; set; } = TimeSpan.FromSeconds(5);

    private readonly IServiceProvider _sp;
    private readonly DisposableStore _store = new();
    private readonly ILanguageFeatureRegistry? _features;
    private readonly INavigationService? _navigation;
    private readonly IWindowService? _window;
    private HierarchyDirection _lastCalls = HierarchyDirection.IncomingCalls;
    private HierarchyDirection _lastTypes = HierarchyDirection.Subtypes;
    private int _request;
    private bool _disposed;

    public HierarchyHost(IServiceProvider services)
    {
        _sp = services;
        _features = services.GetService<ILanguageFeatureRegistry>();
        _navigation = services.GetService<INavigationService>();
        _window = services.GetService<IWindowService>();
        if (services.GetService<ICommandService>() is { } commands) RegisterCommands(commands);
        if (services.GetService<IContributionRegistry>() is { } contributions)
            _store.Add(contributions.Contribute(ContributionSources.Builtin, AppContext.BaseDirectory, MenuItems));
    }

    public HierarchyModel Model { get; } = new();

    /// <summary>The editor's right-click items (Visual Studio's wording; after Find All References).</summary>
    public static IReadOnlyList<MenuContribution> MenuItems { get; } =
    [
        new(MenuIds.EditorContext, ShowCallHierarchy, "2_navigation@6", HasCallHierarchyKey) { Title = "View Call _Hierarchy" },
        new(MenuIds.EditorContext, ShowTypeHierarchy, "2_navigation@7", HasTypeHierarchyKey) { Title = "View T_ype Hierarchy" },
    ];

    private void RegisterCommands(ICommandService commands)
    {
        void Reg(string id, string title, Func<Task> run)
        {
            if (commands.Has(id)) return;
            _store.Add(commands.Register(id, async _ => { await run().ConfigureAwait(true); return null; }, new CommandMetadata(id, title, "Navigation")));
        }
        Reg(FocusView, "Focus on Hierarchy View", ShowViewAsync);
        Reg(ShowCallHierarchy, "Show Call Hierarchy", () => ShowAtCaretAsync(_lastCalls));
        Reg(ShowIncomingCalls, "Show Incoming Calls", () => ShowAtCaretAsync(HierarchyDirection.IncomingCalls));
        Reg(ShowOutgoingCalls, "Show Outgoing Calls", () => ShowAtCaretAsync(HierarchyDirection.OutgoingCalls));
        Reg(ShowTypeHierarchy, "Show Type Hierarchy", () => ShowAtCaretAsync(_lastTypes));
        Reg(ShowSupertypes, "Show Type Hierarchy: Supertypes", () => ShowAtCaretAsync(HierarchyDirection.Supertypes));
        Reg(ShowSubtypes, "Show Type Hierarchy: Subtypes", () => ShowAtCaretAsync(HierarchyDirection.Subtypes));
        Reg(Refresh, "Refresh Hierarchy", RefreshAsync);
        Reg(ToggleDirection, "Hierarchy: Toggle Direction", ToggleDirectionAsync);
        Reg(Clear, "Clear Hierarchy", () => { Model.Clear(); return Task.CompletedTask; });
    }

    /// <summary>
    /// Prepares the hierarchy of the symbol at the active editor's caret and shows it in the window, in <paramref name="direction"/>. Says in
    /// the status bar why nothing is shown (no editor, no provider, no symbol). UI thread.
    /// </summary>
    public async Task<bool> ShowAtCaretAsync(HierarchyDirection direction)
    {
        if (_disposed) return false;
        if (_navigation?.ActiveDocument is not { } doc || _navigation.ActiveCaret is not { } caret)
        {
            Status("Open a file and put the caret on a symbol first");
            return false;
        }
        return await ShowAtAsync(doc, caret, direction).ConfigureAwait(true);
    }

    /// <summary>Prepares at <paramref name="position"/> of <paramref name="document"/> and shows the result (the commands' core; tests call it).</summary>
    public async Task<bool> ShowAtAsync(ITextDocument document, TextPosition position, HierarchyDirection direction)
    {
        if (_disposed) return false;
        var request = ++_request;
        var what = direction.IsCalls() ? "call hierarchy" : "type hierarchy";
        var providers = Providers(document.Uri, document.LanguageId, direction);
        if (providers.Count == 0)
        {
            Status($"No {what} is available for this file (its language server does not offer one)");
            return false;
        }
        foreach (var provider in providers)
        {
            var items = await PrepareAsync(provider, document, position, direction).ConfigureAwait(true);
            if (_disposed || request != _request) return false; // a newer request
            if (items is not { Count: > 0 }) continue;
            Remember(direction);
            var load = Model.SetAsync(provider, items, direction, (document.Uri, document.LanguageId, position));
            await ShowViewAsync().ConfigureAwait(true);
            await load.ConfigureAwait(true);
            return true;
        }
        Status($"No {what} for the symbol at the caret");
        return false;
    }

    private void Remember(HierarchyDirection direction)
    {
        if (direction.IsCalls()) _lastCalls = direction; else _lastTypes = direction;
    }

    /// <summary>Flips the window's direction (incoming ⇄ outgoing, supertypes ⇄ subtypes) and asks again from the same roots.</summary>
    public Task ToggleDirectionAsync()
    {
        if (!Model.HasQuery) return Task.CompletedTask;
        Remember(Model.Direction.Flipped());
        return Model.ToggleDirectionAsync();
    }

    /// <summary>
    /// Refresh: prepares again at the query's position while its document is open (the symbol may have moved or changed), else asks the
    /// same roots for their children again; expanded nodes are expanded again.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (_disposed || Model.Query is not { } q) { await Model.ReloadAsync().ConfigureAwait(true); return; }
        if (_sp.GetService<IDocumentManager>()?.Get(q.Uri) is { } doc && q.Position.Line < doc.LineCount)
        {
            var direction = Model.Direction;
            foreach (var provider in Providers(doc.Uri, doc.LanguageId, direction))
            {
                var items = await PrepareAsync(provider, doc, q.Position, direction).ConfigureAwait(true);
                if (_disposed) return;
                if (items is not { Count: > 0 }) continue;
                await Model.SetAsync(provider, items, direction, (doc.Uri, doc.LanguageId, q.Position), Model.ExpandedPaths()).ConfigureAwait(true);
                return;
            }
        }
        await Model.ReloadAsync().ConfigureAwait(true);
    }

    private IReadOnlyList<ILanguageProvider> Providers(Uri uri, string languageId, HierarchyDirection direction)
    {
        if (_features is null) return [];
        return direction.IsCalls()
            ? _features.GetProviders<ICallHierarchyProvider>(uri, languageId)
            : _features.GetProviders<ITypeHierarchyProvider>(uri, languageId);
    }

    /// <summary>Prepare off the UI thread with a deadline; a failing or slow provider is "no result".</summary>
    private static async Task<IReadOnlyList<HierarchyItem>?> PrepareAsync(ILanguageProvider provider, ITextDocument document, TextPosition position, HierarchyDirection direction)
    {
        try
        {
            return await Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(PrepareTimeout);
                var call = provider switch
                {
                    ICallHierarchyProvider c when direction.IsCalls() => c.PrepareCallHierarchyAsync(document, position, cts.Token).AsTask(),
                    ITypeHierarchyProvider t when !direction.IsCalls() => t.PrepareTypeHierarchyAsync(document, position, cts.Token).AsTask(),
                    _ => Task.FromResult<IReadOnlyList<HierarchyItem>?>(null),
                };
                return await call.WaitAsync(cts.Token).ConfigureAwait(false);
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    internal async Task ShowViewAsync()
    {
        if (_sp.GetService<IViewActivator>() is { } views) await views.ShowViewAsync(ViewId).ConfigureAwait(true);
    }

    /// <summary>Goes to a row's place: its first call site, else the symbol (Enter / double-click: <paramref name="preview"/> false, the editor takes the keyboard).</summary>
    public async Task GoToAsync(HierarchyNode node, bool preview, bool preserveFocus)
    {
        if (_navigation is null) return;
        var target = node.Target;
        await _navigation.OpenAsync(target.Uri, target.Range, preview, preserveFocus).ConfigureAwait(true);
    }

    /// <summary>Goes to the symbol itself (the context menu's Go To Definition), not the call site.</summary>
    public async Task GoToSymbolAsync(HierarchyNode node)
    {
        if (_navigation is null) return;
        await _navigation.OpenAsync(node.Item.Uri, node.Item.SelectionRange).ConfigureAwait(true);
    }

    /// <summary>Makes a row the new root (the context menu's "Show Call Hierarchy" on a node), same provider and direction.</summary>
    public Task RerootAsync(HierarchyNode node) =>
        Model.Provider is { } p ? Model.SetAsync(p, [node.Item], Model.Direction, null) : Task.CompletedTask;

    private void Status(string text) => _window?.SetStatusBarMessage(text, TimeSpan.FromSeconds(4));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _store.Dispose();
        Model.Clear();
    }
}
