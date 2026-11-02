using System.Diagnostics;
using System.Text.Json;
using CsCode.Core.Platform;
using CsCode.Lsp.Client;
using CsCode.Lsp.Features;
using StreamJsonRpc;

namespace CsCode.Lsp.CSharp;

/// <summary>
/// What CsCode does for Microsoft's Roslyn language server beyond plain LSP (VS Code's C# extension does it in its client,
/// <c>src/lsptoolshost</c>); method names checked against the fetched server, 5.13.0-1.26514.10:
/// <list type="bullet">
/// <item><b>Opening the code.</b> The server loads nothing by itself (unless started with <c>--autoLoadProjects</c>): after <c>initialized</c>
/// the client sends <c>solution/open</c> <c>{solution: uri}</c> with the solution at the server's root, or, with no solution there,
/// <c>project/open</c> <c>{projects: [uri…]}</c> with the projects below it. Several solutions: a quick pick, remembered per workspace
/// (<see cref="SolutionChoiceKey"/>; the command <c>csharp.selectSolution</c> asks again).</item>
/// <item><b>Ready</b> when the server sends <c>workspace/projectInitializationComplete</c> (no params): logged with the time it took, shown in
/// the status bar and awaitable (<see cref="Ready"/>). The loading itself is reported through standard work-done progress.</item>
/// <item><b>Toasts</b> (<c>window/_roslyn_showToast</c>, a showMessage with commands) are shown as messages.</item>
/// </list>
/// One per running Roslyn session, created by the client before its connection listens (custom notifications can be added only then);
/// it lives and dies with that session and holds nothing else.
/// </summary>
internal sealed class RoslynSession
{
    /// <summary>Workspace-scoped state key: the solution file chosen for a server root (root path → solution path).</summary>
    public const string SolutionChoiceKey = "csharp.roslyn.solutions";
    public const string SolutionOpen = "solution/open";
    public const string ProjectOpen = "project/open";
    public const string ProjectInitializationComplete = "workspace/projectInitializationComplete";
    public const string ShowToast = "window/_roslyn_showToast";
    internal const int MaxProjects = 2000;

    private static readonly string[] SkippedDirectories = ["bin", "obj", ".git", "node_modules", ".vs", ".idea", ".scratch"];

    private readonly LanguageClient _client;
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly TaskCompletionSource<TimeSpan> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RoslynSession(LanguageClient client) => _client = client;

    /// <summary>Completes with the time from start to <c>workspace/projectInitializationComplete</c>.</summary>
    public Task<TimeSpan> Ready => _ready.Task;

    /// <summary>What was opened: the solution file, or the project files (empty until <see cref="OnInitialized"/> chose).</summary>
    public IReadOnlyList<string> Opened { get; private set; } = [];

    [JsonRpcMethod(ProjectInitializationComplete)]
    public void OnProjectInitializationComplete()
    {
        var took = Stopwatch.GetElapsedTime(_started);
        if (!_ready.TrySetResult(took)) return;
        _client.Log.Add($"[client] Roslyn: project initialization complete after {took.TotalSeconds:0.0} s");
        _client.Services.Window?.SetStatusBarMessage($"C#: {Describe(Opened)} loaded", TimeSpan.FromSeconds(5));
    }

    [JsonRpcMethod(ShowToast, UseSingleObjectParameterDeserialization = true)]
    public void OnShowToast(JsonElement p)
    {
        var message = p.Str("message");
        if (message is null || _client.Services.Window is not { } window) return;
        var severity = p.Int("messageType") switch { 1 => MessageSeverity.Error, 2 => MessageSeverity.Warning, _ => MessageSeverity.Info };
        _ = window.ShowMessageAsync(severity, "C#: " + message).AsTask();
    }

    /// <summary>After <c>initialized</c> (on the language lane): choose what to open and tell the server.</summary>
    public void OnInitialized() => _ = OpenAsync();

    private async Task OpenAsync()
    {
        try
        {
            var root = _client.RootUri is { IsFile: true } r ? r.LocalPath : _client.WorkspaceFolders.FirstOrDefault(f => f.IsFile)?.LocalPath;
            if (root is null || !Directory.Exists(root))
            {
                _client.Log.Add("[client] Roslyn: no folder to open (an untitled or remote document): nothing loaded");
                return;
            }
            var solution = await ChooseSolutionAsync(root).ConfigureAwait(false);
            if (solution is not null)
            {
                Opened = [solution];
                _client.Log.Add($"[client] Roslyn: {SolutionOpen} {solution}");
                _client.NotifyRaw(SolutionOpen, new { solution = LspMapper.ToUriString(new Uri(solution)) });
                return;
            }
            var projects = FindProjects(root);
            if (projects.Count == 0)
            {
                _client.Log.Add($"[client] Roslyn: no solution or project under {root}: nothing loaded");
                return;
            }
            Opened = projects;
            _client.Log.Add($"[client] Roslyn: {ProjectOpen} with {projects.Count} project(s) under {root}");
            _client.NotifyRaw(ProjectOpen, new { projects = projects.Select(p => LspMapper.ToUriString(new Uri(p))).ToArray() });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            _client.Log.Add("[client] Roslyn: could not choose what to open: " + ex.Message);
        }
    }

    /// <summary>
    /// The solution to open for <paramref name="root"/>: the only one there, the one chosen before for this root, else the user's pick among
    /// them (remembered). Null: none there (the projects are opened instead) or the pick was dismissed with more than one.
    /// </summary>
    private async Task<string?> ChooseSolutionAsync(string root)
    {
        var solutions = FindSolutions(root);
        var state = _client.Services.State;
        var remembered = state?.Get<Dictionary<string, string>>(SolutionChoiceKey, StorageScope.Workspace);
        if (remembered?.GetValueOrDefault(root) == ProjectsChoice) return null;
        if (solutions.Count <= 1) return solutions.FirstOrDefault();
        if (remembered?.GetValueOrDefault(root) is { } known && solutions.Contains(known, StringComparer.Ordinal)) return known;
        if (_client.Services.Window is not { } window) return solutions[0];
        var items = solutions.Select(s => new QuickPickItem(Path.GetFileName(s), Path.GetRelativePath(root, Path.GetDirectoryName(s)!) is "." ? null : Path.GetDirectoryName(s), Tag: s)).ToArray();
        var picked = await window.ShowQuickPickAsync(items, new QuickPickOptions("C#: Open Solution", $"{solutions.Count} solutions in {Path.GetFileName(root)}: which one should the C# server load?")).ConfigureAwait(false);
        var chosen = picked?.Tag as string ?? solutions[0];
        if (state is not null)
        {
            var updated = new Dictionary<string, string>(remembered ?? new(), StringComparer.Ordinal) { [root] = chosen };
            state.Set(SolutionChoiceKey, updated, StorageScope.Workspace);
        }
        return chosen;
    }

    /// <summary>
    /// "C#: Open Solution...": a pick among the solutions at the server's root (and its projects), remembered for the root. True when the choice
    /// changed (the caller restarts the server, which opens it).
    /// </summary>
    public async Task<bool> ChooseAgainAsync(IWindowService window)
    {
        var root = _client.RootUri is { IsFile: true } r ? r.LocalPath : null;
        if (root is null) return false;
        var solutions = await Task.Run(() => FindSolutions(root)).ConfigureAwait(false);
        const string Projects = "\0projects";
        var items = solutions.Select(s => new QuickPickItem(Path.GetFileName(s), Opened.Contains(s) ? "loaded" : null, Tag: s))
            .Append(new QuickPickItem("Every project under " + Path.GetFileName(root.TrimEnd('/')), Opened.Count > 1 || (Opened.Count == 1 && !solutions.Contains(Opened[0])) ? "loaded" : null, Tag: Projects))
            .ToArray();
        var picked = await window.ShowQuickPickAsync(items, new QuickPickOptions("C#: Open Solution", "What should the C# server load?")).ConfigureAwait(false);
        if (picked?.Tag is not string chosen) return false;
        var state = _client.Services.State;
        var remembered = new Dictionary<string, string>(state?.Get<Dictionary<string, string>>(SolutionChoiceKey, StorageScope.Workspace) ?? new(), StringComparer.Ordinal);
        if (chosen == Projects) remembered[root] = ProjectsChoice;
        else remembered[root] = chosen;
        state?.Set(SolutionChoiceKey, remembered, StorageScope.Workspace);
        return chosen == Projects ? Opened.Count == 1 && solutions.Contains(Opened[0]) : !Opened.Contains(chosen);
    }

    /// <summary>The remembered choice that means "open every project, not a solution".</summary>
    internal const string ProjectsChoice = "<projects>";

    /// <summary>The <c>.sln</c> and <c>.slnx</c> files directly in <paramref name="root"/>, sorted.</summary>
    internal static List<string> FindSolutions(string root) =>
        [.. Directory.EnumerateFiles(root).Where(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)];

    /// <summary>The project files (<c>.csproj</c>, <c>.vbproj</c>) below <paramref name="root"/>, build output and tool folders skipped, at most <see cref="MaxProjects"/>.</summary>
    internal static List<string> FindProjects(string root)
    {
        var found = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0 && found.Count < MaxProjects)
        {
            var dir = pending.Pop();
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                    if (f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)) found.Add(f);
                foreach (var d in Directory.EnumerateDirectories(dir))
                    if (!SkippedDirectories.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase) && (File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0) pending.Push(d);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    private static string Describe(IReadOnlyList<string> opened) =>
        opened.Count switch { 0 => "projects", 1 => Path.GetFileName(opened[0]), var n => $"{n} projects" };
}
