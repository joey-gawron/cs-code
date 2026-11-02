using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Python;
using CsCode.Core.Terminal;
using CsCode.Lsp;
using CsCode.Lsp.Manager;
using CsCode.Ui.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CsCode.App.Python;

/// <summary>
/// Python in the shell (docs/PYTHON.md): <b>Python: Select Interpreter</b> (<c>python.setInterpreter</c>; the interpreters found for the active
/// file's folder, "Enter interpreter path…" and a refresh), <b>Python: Clear Workspace Interpreter Setting</b>, <b>Python: Select Language
/// Server…</b> (basedpyright or Pyright), <c>${command:python.interpreterPath}</c>, and the status bar item that shows
/// the active file's interpreter while a Python file is active (a click opens the picker). The interpreter is persisted per workspace folder by
/// <see cref="IPythonEnvironmentService"/>, which feeds it to the language servers, the debugger, the test provider and new terminals.
/// </summary>
public sealed class PythonIntegration : IDisposable
{
    public const string SelectInterpreter = "python.setInterpreter";
    public const string ClearInterpreter = "python.clearWorkspaceInterpreter";
    /// <summary><c>${command:python.interpreterPath}</c> in launch configurations and tasks: the active file's (else the first folder's) interpreter.</summary>
    public const string InterpreterPath = "python.interpreterPath";
    public const string StatusItemId = "python.interpreter";
    /// <summary>Run Python File in Terminal: the active file with its folder's interpreter, in the "Python" terminal.</summary>
    public const string RunFile = "python.execInTerminal";
    /// <summary>Run Selection/Line in Python REPL (Shift+Enter): one REPL terminal per interpreter, reused.</summary>
    public const string RunSelection = "python.execSelectionInTerminal";
    /// <summary>Python: Select Language Server…: basedpyright (the default) or Pyright, as <c>cscode.lsp.serverChoice</c>'s <c>python</c> member.</summary>
    public const string SelectLanguageServer = "python.selectLanguageServer";
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
            _subs.Add(commands.Register(RunFile, async _ =>
            {
                await RunFileAsync().ConfigureAwait(true);
                return null;
            }, new CommandMetadata(RunFile, "Run Python File in Terminal", "Python")));
            _subs.Add(commands.Register(RunSelection, async inv =>
            {
                await RunSelectionAsync(inv.Args is [string text, ..] ? text : null).ConfigureAwait(true);
                return null;
            }, new CommandMetadata(RunSelection, "Run Selection/Line in Python REPL", "Python")));
            _subs.Add(commands.Register(SelectLanguageServer, async _ =>
            {
                await SelectLanguageServerAsync().ConfigureAwait(true);
                return null;
            }, new CommandMetadata(SelectLanguageServer, "Select Language Server...", "Python")));
            _subs.Add(commands.Register(InterpreterPath, async inv =>
            {
                var resource = inv.Args is [Uri uri, ..] ? uri : Navigation?.ActiveDocument?.Uri ?? _sp.GetService<IWorkspaceService>()?.PrimaryFolder;
                return (await _environments.GetActiveAsync(resource).ConfigureAwait(true))?.Executable;
            }));
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

    // ---- type checker ----

    /// <summary>basedpyright (the default) or Pyright: written into <c>cscode.lsp.serverChoice</c> next to the other groups' choices (C#'s).</summary>
    private async Task SelectLanguageServerAsync()
    {
        if (_sp.GetService<IConfigurationService>() is not { } config || Window is not { } window) return;
        var choice = new Dictionary<string, string>(BuiltinLanguageServers.ReadChoice(config.GetRaw(LspSettingKeys.ServerChoice)), StringComparer.Ordinal);
        var current = choice.GetValueOrDefault(BuiltinLanguageServers.PythonGroup) ?? "basedpyright"; // the group's default
        QuickPickItem Item(string id, string label, string detail) => new(label, id == current ? "current" : null, detail, Tag: id);
        var picked = await window.ShowQuickPickAsync(
        [
            Item("basedpyright", "basedpyright", "The default: Pyright's checks plus semantic highlighting, inlay hints and a stricter default rule set."),
            Item("python", "Pyright", "Microsoft's open-source server: types, completion, hover and navigation; no semantic highlighting or inlay hints."),
        ], new QuickPickOptions("Python: Select Language Server", "Which type checker should run on Python files?")).ConfigureAwait(true);
        if (picked?.Tag is not string id || id == current) return;
        choice[BuiltinLanguageServers.PythonGroup] = id;
        await config.UpdateAsync(LspSettingKeys.ServerChoice, choice).ConfigureAwait(true);
        window.SetStatusBarMessage($"Python: {picked.Label} checks Python files from now on", TimeSpan.FromSeconds(5));
    }

    // ---- running code ----

    private ITerminalService? Terminals => _sp.GetService<ITerminalService>();

    /// <summary>The active Python file, saved first, run with its folder's interpreter in a terminal named "Python" (reused while it lives).</summary>
    internal async Task RunFileAsync()
    {
        if (Navigation?.ActiveDocument is not { LanguageId: "python", Uri.IsFile: true } doc || Terminals is not { } terminals)
        {
            Window?.SetStatusBarMessage("Run Python File needs a Python file on disk in the active editor.", TimeSpan.FromSeconds(4));
            return;
        }
        if (doc.IsDirty) await doc.SaveAsync().ConfigureAwait(true);
        if (await _environments.GetActiveAsync(doc.Uri).ConfigureAwait(true) is not { } env)
        {
            await PickAsync(doc.Uri).ConfigureAwait(true);
            return;
        }
        var folder = _sp.GetService<IWorkspaceService>()?.Folders.FirstOrDefault(f => f.IsFile && doc.Uri.LocalPath.StartsWith(f.LocalPath, StringComparison.Ordinal));
        var terminal = terminals.Terminals.FirstOrDefault(t => t.Title == "Python" && !t.HasExited)
            ?? await terminals.CreateAsync(new TerminalCreateOptions { Name = "Python", Cwd = folder?.LocalPath ?? Path.GetDirectoryName(doc.Uri.LocalPath) }).ConfigureAwait(true);
        await terminals.ShowAsync(terminal, preserveFocus: true).ConfigureAwait(true);
        await terminal.SendTextAsync(CommandLine(env.Executable, doc.Uri.LocalPath)).ConfigureAwait(true);
    }

    /// <summary>
    /// The selection (else the caret's line) sent to the Python REPL of the file's interpreter: a terminal running that interpreter, created on first
    /// use and reused. The code is dedented, and an indented last line gets the blank line the REPL needs to run the block.
    /// </summary>
    internal async Task RunSelectionAsync(string? text = null)
    {
        if (Navigation?.ActiveDocument is not { LanguageId: "python" } doc || Terminals is not { } terminals) return;
        text ??= _sp.GetService<Core.Debugging.IActiveSelectionProvider>()?.SelectedText is { Length: > 0 } selected ? selected
            : Navigation.ActiveCaret is { } caret && doc is Core.Text.ITextDocument textDoc ? textDoc.GetLineText(caret.Line) : null;
        if (string.IsNullOrWhiteSpace(text)) return;
        if (await _environments.GetActiveAsync(doc.Uri).ConfigureAwait(true) is not { } env)
        {
            await PickAsync(doc.Uri).ConfigureAwait(true);
            return;
        }
        var title = "Python REPL (" + env.Label + ")";
        var repl = terminals.Terminals.FirstOrDefault(t => t.Title == title && !t.HasExited)
            ?? await terminals.CreateAsync(new TerminalCreateOptions { Name = title, ShellPath = env.Executable, ShellArgs = [], Cwd = Path.GetDirectoryName(doc.Uri.LocalPath) }).ConfigureAwait(true);
        await terminals.ShowAsync(repl, preserveFocus: true).ConfigureAwait(true);
        await repl.SendTextAsync(ForRepl(text), addNewLine: true).ConfigureAwait(true);
    }

    /// <summary>Code as the REPL takes it: common indentation removed, blank lines inside a block dropped (each would end it), a trailing blank line after an indented block.</summary>
    internal static string ForRepl(string code)
    {
        var lines = code.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        var indent = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        var kept = lines.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart()).Where(l => l.Trim().Length > 0).ToList();
        var result = string.Join("\n", kept);
        return kept.Count > 0 && (kept[^1].StartsWith(' ') || kept[^1].StartsWith('\t') || kept[^1].TrimEnd().EndsWith(':')) ? result + "\n" : result;
    }

    /// <summary>A command line for the user's shell: each part quoted when it needs it (single quotes on Unix, double on Windows).</summary>
    internal static string CommandLine(params string[] parts) => string.Join(' ', parts.Select(Quote));

    private static string Quote(string part)
    {
        if (part.Length > 0 && part.All(c => char.IsLetterOrDigit(c) || c is '/' or '.' or '_' or '-' or ':' or '\\' or '+' or '=' or ',')) return part;
        return OperatingSystem.IsWindows() ? "\"" + part.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : "'" + part.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
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
