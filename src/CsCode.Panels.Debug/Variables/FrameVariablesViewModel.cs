using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CsCode.Core.Debugging;
using CsCode.Panels.Debug.Infrastructure;

namespace CsCode.Panels.Debug.Variables;

/// <summary>Chooses which of a frame's scopes the Locals and Autos windows show.</summary>
public static partial class ScopeSelector
{
    [GeneratedRegex(@"^\s*(locals?|arguments?|args|parameters?)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LocalsName();

    /// <summary>
    /// The frame's "own" variables: scopes the adapter hints as <c>locals</c>/<c>arguments</c> (or names like them), else the first cheap scope.
    /// Expensive scopes (globals, registers: the adapter says a fetch is slow) are never loaded automatically.
    /// </summary>
    public static IReadOnlyList<DebugScope> ForLocals(IReadOnlyList<DebugScope> scopes)
    {
        var cheap = scopes.Where(s => !s.Expensive && s.VariablesReference > 0).ToList();
        if (cheap.Count == 0) return [];
        var preferred = cheap.Where(s => s.PresentationHint is "locals" or "arguments" || LocalsName().IsMatch(s.Name)).ToList();
        return preferred.Count > 0 ? preferred : [cheap[0]];
    }
}

/// <summary>
/// Base of the windows that show the variables of the focused stack frame (Locals, Autos). Follows the debugger: reloads on every stop and
/// whenever another frame is picked in the Call Stack, and drops everything (cancelling in-flight loads) the moment the debuggee resumes.
/// </summary>
public abstract class FrameVariablesViewModel : ObservableObject, IDisposable
{
    private readonly DebugFocusTracker _tracker;
    private IDebugSession? _loadedSession;
    private long _loadedEpoch = -1;
    private int? _loadedFrame;
    private DebugFocus _focus;
    private int _disposed;

    protected FrameVariablesViewModel(DebugPanelServices services)
    {
        Services = services;
        Tree = new VariablesTreeViewModel(services, "Not debugging");
        Tree.RefreshRequested = Reload;
        _tracker = new DebugFocusTracker(services, OnFocus);
    }

    protected DebugPanelServices Services { get; }

    public VariablesTreeViewModel Tree { get; }

    /// <summary>Starts following the debugger (call once, after construction).</summary>
    protected void StartTracking() => _tracker.Start();

    private void OnFocus(DebugFocus focus)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _focus = focus;
        if (!focus.IsStopped)
        {
            ForgetLoaded();
            Tree.Invalidate(StatusFor(focus));
            return;
        }
        if (!focus.HasFrame)
        {
            ForgetLoaded();
            Tree.Invalidate(focus.LocationPending ? "Loading…" : "No stack frame");
            return;
        }
        if (ReferenceEquals(focus.Session, _loadedSession) && focus.Epoch == _loadedEpoch && focus.FrameId == _loadedFrame) return;
        _ = LoadAsync(focus);
    }

    internal static string StatusFor(DebugFocus focus) => focus.Session is null ? "Not debugging"
        : focus.State == DebugSessionState.Initializing ? "Starting…"
        : focus.State == DebugSessionState.Terminated ? "Not debugging"
        : "Running…";

    private void ForgetLoaded()
    {
        _loadedSession = null;
        _loadedEpoch = -1;
        _loadedFrame = null;
    }

    private void Reload()
    {
        if (!_focus.HasFrame) return;
        ForgetLoaded();
        _ = LoadAsync(_focus);
    }

    /// <summary>Loads the focused frame; completes when the first rows (and the restored expansion) are in place.</summary>
    internal async Task LoadAsync(DebugFocus focus)
    {
        var session = focus.Session!;
        _loadedSession = session;
        _loadedEpoch = focus.Epoch;
        _loadedFrame = focus.FrameId;
        var token = Tree.BeginLoad(session, focus.Epoch);
        Tree.SetStatus("Loading…");
        try
        {
            await LoadFrameAsync(session, focus, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (token.Generation == Tree.Generation) Tree.SetStatus(ex is InvalidOperationException ? "Not available" : "Cannot load variables: " + ex.Message);
        }
    }

    /// <summary>Fills <see cref="Tree"/> for the focused frame. Must call service methods through <see cref="UiAsync.OnUi{T}"/> and stop when the token is stale.</summary>
    protected abstract Task LoadFrameAsync(IDebugSession session, DebugFocus focus, LoadToken token);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _tracker.Dispose();
        Tree.Dispose();
        _loadedSession = null;
        _focus = default;
    }
}

/// <summary>Locals window: the variables of the focused frame's Locals/Arguments scopes, paged and lazily expanded.</summary>
public sealed class LocalsViewModel : FrameVariablesViewModel
{
    public LocalsViewModel(DebugPanelServices services) : base(services) => StartTracking();

    protected override async Task LoadFrameAsync(IDebugSession session, DebugFocus focus, LoadToken token)
    {
        var scopes = await Services.Ui.OnUi(session.GetScopesAsync(focus.FrameId!.Value, token.Cancellation), token.Cancellation);
        if (token.Generation != Tree.Generation) return;
        await Tree.ShowScopesAsync(token, ScopeSelector.ForLocals(scopes));
    }
}
