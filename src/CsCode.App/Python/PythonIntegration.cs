using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Python;
using CsCode.Ui.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CsCode.App.Python;

/// <summary>
/// Python in the shell (docs/PYTHON.md): <b>Python: Select Interpreter</b> (<c>python.setInterpreter</c>; the interpreters found for the active
/// file's folder, "Enter interpreter path…" and a refresh), <b>Python: Clear Workspace Interpreter Setting</b>, and the status bar item that shows
/// the active file's interpreter while a Python file is active (a click opens the picker). The interpreter is persisted per workspace folder by
/// <see cref="IPythonEnvironmentService"/>, which feeds it to the language servers, the debugger, the test provider and new terminals.
/// </summary>
public sealed class PythonIntegration : IDisposable
{
    public const string SelectInterpreter = "python.setInterpreter";
    public const string ClearInterpreter = "python.clearWorkspaceInterpreter";
    public const string StatusItemId = "python.interpreter";
    private const string EnterPath = "enter";
    private const string Refresh = "refresh";

    private readonly IServiceProvider _sp;
    private readonly IPythonEnvironmentService _environments;
    private readonly IUiDispatcher _ui;
    private readonly DisposableStore _subs = new();
    private IDisposable? _item;
    private bool _disposed;

    private PythonIntegration(IServiceProvider sp, IPythonEnvironmentService environments)
    {
        _sp = sp;
        _environments = environments;
        _ui = sp.GetRequiredService<IUiDispatcher>();
    }

    /// <summary>The integration, or null when the composition has no interpreter service.</summary>
    public static PythonIntegration? TryCreate(IServiceProvider sp) =>
        sp.GetService<IPythonEnvironmentService>() is { } environments ? new PythonIntegration(sp, environments) : null;

    private INavigationService? Navigation => _sp.GetService<INavigationService>();
    private IWindowService? Window => _sp.GetService<IWindowService>();

    public void Start()
    {
        if (_sp.GetService<ICommandService>() is { } commands)
        {
            _subs.Add(commands.Register(SelectInterpreter, async inv =>
            {
                await PickAsync(inv.Args is [Uri uri, ..] ? uri : null).ConfigureAwait(true);
                return null;
            }, new CommandMetadata(SelectInterpreter, "Select Interpreter", "Python")));
            _subs.Add(commands.Register(ClearInterpreter, async _ =>
            {
                await _environments.SetActiveAsync(Navigation?.ActiveDocument?.Uri, null).ConfigureAwait(true);
                return null;
            }, new CommandMetadata(ClearInterpreter, "Clear Workspace Interpreter Setting", "Python")));
        }
        if (Navigation is { } nav) _subs.Add(nav.ActiveDocumentChanged.Subscribe(_ => _ui.PostCoalesced("python.status", UpdateStatus)));
        _subs.Add(_environments.ActiveChanged.Subscribe(_ => _ui.PostCoalesced("python.status", UpdateStatus)));
        UpdateStatus();
    }

    /// <summary>The status bar item: the active Python file's interpreter (or "Select Interpreter" when there is none yet). UI thread.</summary>
    internal void UpdateStatus()
    {
        if (_disposed) return;
        var doc = Navigation?.ActiveDocument;
        _item?.Dispose();
        _item = null;
        if (doc is null || doc.LanguageId != "python" || _sp.GetService<IStatusBarService>() is not { } bar) return;
        var env = _environments.GetActive(doc.Uri); // null while discovery runs: ActiveChanged updates the item when it is done
        var text = env is null ? "Select Interpreter" : env.Label;
        var tooltip = env is null ? "No Python interpreter selected: click to choose one" : env.Executable + "\nClick to select another interpreter";
        _item = bar.Set(new StatusBarItemModel(StatusItemId, text, tooltip, SelectInterpreter, StatusBarAlignment.Right, 55));
    }

    /// <summary>The picker: every interpreter found for the folder of <paramref name="resource"/> (default the active file), the current one marked.</summary>
    public async Task PickAsync(Uri? resource = null)
    {
        if (Window is not { } window) return;
        resource ??= Navigation?.ActiveDocument?.Uri ?? _sp.GetService<IWorkspaceService>()?.PrimaryFolder;
        var refresh = false;
        while (true)
        {
            IReadOnlyList<PythonEnvironment> found;
            using (window.SetStatusBarMessage("Looking for Python interpreters…"))
                found = await _environments.DiscoverAsync(resource, refresh).ConfigureAwait(true);
            var current = await _environments.GetActiveAsync(resource).ConfigureAwait(true);
            var items = new List<QuickPickItem>
            {
                new("$(add) Enter interpreter path…", null, "A Python executable anywhere on this machine", Tag: EnterPath),
                new("$(refresh) Refresh", null, "Look for interpreters again", Tag: Refresh),
                new("", IsSeparator: true),
            };
            items.AddRange(found.Select(e => new QuickPickItem("Python " + e.Label, current?.Executable == e.Executable ? "selected" : null, Shorten(e.Executable),
                Picked: current?.Executable == e.Executable, Tag: e)));
            var picked = await window.ShowQuickPickAsync(items, new QuickPickOptions(Title: "Select Interpreter",
                Placeholder: current is null ? "No interpreter selected" : "Selected: " + Shorten(current.Executable), MatchOnDetail: true)).ConfigureAwait(true);
            switch (picked?.Tag)
            {
                case null:
                    return;
                case Refresh:
                    refresh = true;
                    continue;
                case EnterPath:
                    await EnterPathAsync(window, resource).ConfigureAwait(true);
                    return;
                case PythonEnvironment env:
                    await _environments.SetActiveAsync(resource, env.Executable).ConfigureAwait(true);
                    return;
                default:
                    return;
            }
        }
    }

    private async Task EnterPathAsync(IWindowService window, Uri? resource)
    {
        var path = await window.ShowInputBoxAsync(new InputBoxOptions(Title: "Enter interpreter path", Prompt: "The path of a Python executable",
            Placeholder: OperatingSystem.IsWindows() ? @"C:\Python313\python.exe" : "/usr/local/bin/python3")).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path)) return;
        path = path.Trim();
        if (path.StartsWith('~')) path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
        if (await _environments.ProbeAsync(path).ConfigureAwait(true) is not { } env)
        {
            await window.ShowMessageAsync(MessageSeverity.Error, $"'{path}' is not a working Python interpreter.").ConfigureAwait(true);
            return;
        }
        await _environments.SetActiveAsync(resource, env.Executable).ConfigureAwait(true);
        _sp.GetService<ILoggerFactory>()?.CreateLogger<PythonIntegration>().LogInformation("Python interpreter entered by hand: {Path} ({Label})", env.Executable, env.Label);
    }

    private static string Shorten(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length > 1 && path.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? "~" + path[home.Length..] : path;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _item?.Dispose();
        _subs.Dispose();
    }
}
