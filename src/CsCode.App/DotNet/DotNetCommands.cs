using System.Text.Json;
using CsCode.App.Docking;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.App.DotNet;

/// <summary>
/// The shell's .NET commands (docs/CSHARP.md, "Tasks and NuGet"), in the palette under "NuGet:" and "C#:" (creating projects and items from
/// <c>dotnet new</c> templates belongs to the New Project page, stage 7):
/// <list type="bullet">
/// <item><c>dotnet.addPackage</c>, <c>dotnet.updatePackage</c>, <c>dotnet.removePackage</c>: the project (the active file's, else a pick), the
/// package (a search of the folder's NuGet sources, or the project's references) and the version; the change runs as a task in a terminal
/// (<c>dotnet add package</c> / <c>dotnet remove package</c>), so its output and exit code are where a build's are.</item>
/// <item><c>dotnet.restore</c>: <c>dotnet restore</c> of the active file's project (else the workspace folder) as a task.</item>
/// <item><c>csharp.selectLanguageServer</c>: Roslyn (the default) or csharp-ls (<c>cscode.lsp.serverChoice</c>, user settings).</item>
/// </list>
/// Everything slow (processes, HTTP, JSON) runs off the UI thread; the window service's picks are awaited, never blocked on.
/// </summary>
public sealed class DotNetCommands : IDisposable
{
    public const string AddPackage = "dotnet.addPackage";
    public const string UpdatePackage = "dotnet.updatePackage";
    public const string RemovePackage = "dotnet.removePackage";
    public const string Restore = "dotnet.restore";
    public const string SelectLanguageServer = "csharp.selectLanguageServer";
    /// <summary>Mirrors CsCode.Lsp's <c>cscode.lsp.serverChoice</c> (the App reads settings by key).</summary>
    internal const string ServerChoiceKey = "cscode.lsp.serverChoice";

    private readonly IServiceProvider _sp;
    private readonly IWindowService _window;
    private readonly DisposableStore _subs = new();

    private DotNetCommands(IServiceProvider sp, IWindowService window)
    {
        _sp = sp;
        _window = window;
    }

    public static DotNetCommands? TryCreate(IServiceProvider sp) =>
        sp.GetService<IWindowService>() is { } window && sp.GetService<ICommandService>() is not null ? new DotNetCommands(sp, window) : null;

    public void Start()
    {
        var commands = _sp.GetRequiredService<ICommandService>();
        void Reg(string id, string title, string category, Func<CancellationToken, Task> run) => _subs.Add(commands.Register(id, async inv =>
        {
            try { await run(inv.Cancellation).ConfigureAwait(true); }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                await _window.ShowMessageAsync(MessageSeverity.Error, $"{title}: {ex.Message}").ConfigureAwait(true);
            }
            return null;
        }, new CommandMetadata(id, title, category)));
        Reg(AddPackage, "Add Package...", "NuGet", AddPackageAsync);
        Reg(UpdatePackage, "Update Package...", "NuGet", UpdatePackageAsync);
        Reg(RemovePackage, "Remove Package...", "NuGet", RemovePackageAsync);
        Reg(Restore, "Restore Packages", "NuGet", RestoreAsync);
        Reg(SelectLanguageServer, "Select Language Server...", "C#", SelectLanguageServerAsync);
    }

    // ------------------------------------------------------------------ context

    private string? Folder => _sp.GetService<IWorkspaceService>()?.Folders.FirstOrDefault(f => f.IsFile)?.LocalPath;

    private IFileSystem Files => _sp.GetRequiredService<IFileSystem>();

    private string? ActiveFile => _sp.GetService<DocumentWell>()?.Active is EditorTab { Uri.IsFile: true } tab ? tab.Uri.LocalPath : null;

    /// <summary>The project to change: the active file's (or the active project file itself), else the only one, else a pick. Null: none or dismissed.</summary>
    private async Task<string?> ChooseProjectAsync(string action, CancellationToken ct)
    {
        var folder = Folder;
        if (ActiveFile is { } file)
        {
            if (file.EndsWith("proj", StringComparison.OrdinalIgnoreCase)) return file;
            if (await DotNetCli.NearestProjectAsync(Files, file, folder, ct).ConfigureAwait(true) is { } nearest) return nearest;
        }
        if (folder is null) { await _window.ShowMessageAsync(MessageSeverity.Info, "Open a folder with a .NET project first.").ConfigureAwait(true); return null; }
        var projects = await DotNetCli.FindProjectsAsync(Files, folder, ct).ConfigureAwait(true);
        if (projects.Count == 0) { await _window.ShowMessageAsync(MessageSeverity.Info, $"There is no .NET project in {Path.GetFileName(folder)}.").ConfigureAwait(true); return null; }
        if (projects.Count == 1) return projects[0];
        var picked = await _window.ShowQuickPickAsync(
            [.. projects.Select(p => new QuickPickItem(Path.GetFileName(p), Path.GetRelativePath(folder, Path.GetDirectoryName(p)!), Tag: p))],
            new QuickPickOptions(action, "Which project?"), ct).ConfigureAwait(true);
        return picked?.Tag as string;
    }

    /// <summary>Runs <c>dotnet</c> as a task in a terminal named after it (the Tasks output channel when the shell has no terminal); its exit code.</summary>
    private async Task<int> RunTaskAsync(string label, string directory, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        if (_sp.GetService<ITaskTerminalHost>() is { } terminals)
        {
            var run = await terminals.StartAsync(new TaskTerminalRequest(label, DotNetCli.Find(), arguments, directory,
                new Dictionary<string, string?> { ["DOTNET_NOLOGO"] = "1", ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1" }, new TaskPresentation())
            { CommandLine = DotNetCli.CommandLine(arguments) }, ct).ConfigureAwait(true);
            return (await run.Completion.ConfigureAwait(true)).ExitCode;
        }
        var channel = _sp.GetService<IOutputService>()?.GetOrCreate("Tasks");
        channel?.AppendLine("> " + DotNetCli.CommandLine(arguments));
        var (exit, output) = await Task.Run(() => DotNetCli.RunAsync(directory, arguments, ct), ct).ConfigureAwait(true);
        channel?.AppendLine(output);
        return exit;
    }

    // ------------------------------------------------------------------ NuGet

    private async Task AddPackageAsync(CancellationToken ct)
    {
        if (await ChooseProjectAsync("NuGet: Add Package", ct).ConfigureAwait(true) is not { } project) return;
        var query = await _window.ShowInputBoxAsync(new InputBoxOptions("NuGet: Add Package", "Search the package sources", "Package name or keywords"), ct).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(query)) return;
        var package = await PickPackageAsync(Path.GetDirectoryName(project)!, query.Trim(), ct).ConfigureAwait(true);
        if (package is null || await PickVersionAsync(package, null, ct).ConfigureAwait(true) is not { } version) return;
        await RunPackageChangeAsync($"Add {package.Id} {version}", project, ["add", project, "package", package.Id, "--version", version], ct).ConfigureAwait(true);
    }

    private async Task UpdatePackageAsync(CancellationToken ct)
    {
        if (await ChooseProjectAsync("NuGet: Update Package", ct).ConfigureAwait(true) is not { } project) return;
        if (await PickReferenceAsync(project, "NuGet: Update Package", ct).ConfigureAwait(true) is not { } reference) return;
        var found = await SearchAsync(Path.GetDirectoryName(project)!, "packageid:" + reference.Id, prerelease: true, ct).ConfigureAwait(true);
        var package = found.FirstOrDefault(p => string.Equals(p.Id, reference.Id, StringComparison.OrdinalIgnoreCase));
        if (package is null) { await _window.ShowMessageAsync(MessageSeverity.Warning, $"{reference.Id} was not found on the project's package sources.").ConfigureAwait(true); return; }
        if (await PickVersionAsync(package, reference.Resolved ?? reference.Requested, ct).ConfigureAwait(true) is not { } version) return;
        await RunPackageChangeAsync($"Update {package.Id} to {version}", project, ["add", project, "package", package.Id, "--version", version], ct).ConfigureAwait(true);
    }

    private async Task RemovePackageAsync(CancellationToken ct)
    {
        if (await ChooseProjectAsync("NuGet: Remove Package", ct).ConfigureAwait(true) is not { } project) return;
        if (await PickReferenceAsync(project, "NuGet: Remove Package", ct).ConfigureAwait(true) is not { } reference) return;
        await RunPackageChangeAsync($"Remove {reference.Id}", project, ["remove", project, "package", reference.Id], ct).ConfigureAwait(true);
    }

    private async Task RestoreAsync(CancellationToken ct)
    {
        var target = ActiveFile is { } file ? await DotNetCli.NearestProjectAsync(Files, file, Folder, ct).ConfigureAwait(true) : null;
        var directory = target is null ? Folder : Path.GetDirectoryName(target);
        if (directory is null) { await _window.ShowMessageAsync(MessageSeverity.Info, "Open a folder with a .NET project first.").ConfigureAwait(true); return; }
        await RunTaskAsync("dotnet restore", directory, target is null ? ["restore"] : ["restore", target], ct).ConfigureAwait(true);
    }

    private async Task RunPackageChangeAsync(string what, string project, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var exit = await RunTaskAsync("NuGet: " + what, Path.GetDirectoryName(project)!, arguments, ct).ConfigureAwait(true);
        if (exit != 0) await _window.ShowMessageAsync(MessageSeverity.Error, $"NuGet: {what} in {Path.GetFileName(project)} failed (exit code {exit}): see the terminal.").ConfigureAwait(true);
        else _window.SetStatusBarMessage($"NuGet: {what} in {Path.GetFileName(project)}", TimeSpan.FromSeconds(5));
    }

    private async Task<IReadOnlyList<NuGetPackage>> SearchAsync(string directory, string query, bool prerelease, CancellationToken ct)
    {
        var skipped = new List<string>();
        var found = await _window.WithProgressAsync($"Searching NuGet for '{query}'", true, async (_, token) =>
        {
            using var link = CancellationTokenSource.CreateLinkedTokenSource(ct, token);
            return await Task.Run(async () =>
            {
                var sources = await DotNetCli.SourcesAsync(directory, link.Token).ConfigureAwait(false);
                return await DotNetCli.SearchAsync(sources, query, prerelease, s => { lock (skipped) skipped.Add(s); }, link.Token).ConfigureAwait(false);
            }, link.Token).ConfigureAwait(false);
        }).ConfigureAwait(true);
        if (skipped.Count > 0) _sp.GetService<IOutputService>()?.GetOrCreate("NuGet").AppendLine("Not searched: " + string.Join("; ", skipped));
        return found;
    }

    private async Task<NuGetPackage?> PickPackageAsync(string directory, string query, CancellationToken ct)
    {
        var found = await SearchAsync(directory, query, prerelease: false, ct).ConfigureAwait(true);
        if (found.Count == 0) { await _window.ShowMessageAsync(MessageSeverity.Info, $"No package matches '{query}' on the project's package sources.").ConfigureAwait(true); return null; }
        var picked = await _window.ShowQuickPickAsync(
            [.. found.Select(p => new QuickPickItem(p.Id, $"{p.Version} · {DotNetCli.Downloads(p.Downloads)} downloads", p.Description, Tag: p))],
            new QuickPickOptions("NuGet: Add Package", "Choose a package", MatchOnDetail: true), ct).ConfigureAwait(true);
        return picked?.Tag as NuGetPackage;
    }

    private async Task<string?> PickVersionAsync(NuGetPackage package, string? current, CancellationToken ct)
    {
        var items = package.Versions.Take(200).Select(v => new QuickPickItem(v,
            v == package.Version ? "latest" + (v == current ? ", installed" : "") : v == current ? "installed" : null, Tag: v)).ToArray();
        var picked = await _window.ShowQuickPickAsync(items, new QuickPickOptions($"{package.Id}: version", current is null ? "Choose a version" : $"Installed: {current}"), ct).ConfigureAwait(true);
        return picked?.Tag as string;
    }

    private async Task<PackageReference?> PickReferenceAsync(string project, string title, CancellationToken ct)
    {
        var (exit, output) = await Task.Run(() => DotNetCli.RunAsync(Path.GetDirectoryName(project)!, ["list", project, "package", "--format", "json"], ct), ct).ConfigureAwait(true);
        var references = exit == 0 ? await Task.Run(() => DotNetCli.ParsePackageList(output), ct).ConfigureAwait(true) : [];
        if (references.Count == 0)
        {
            await _window.ShowMessageAsync(MessageSeverity.Info, exit == 0 ? $"{Path.GetFileName(project)} has no package references." : $"dotnet list package failed: {output.Trim()}").ConfigureAwait(true);
            return null;
        }
        var picked = await _window.ShowQuickPickAsync([.. references.Select(r => new QuickPickItem(r.Id, r.Resolved ?? r.Requested, Tag: r))],
            new QuickPickOptions(title, Path.GetFileName(project)), ct).ConfigureAwait(true);
        return picked?.Tag as PackageReference;
    }

    // ------------------------------------------------------------------ C# server

    private async Task SelectLanguageServerAsync(CancellationToken ct)
    {
        if (_sp.GetService<IConfigurationService>() is not { } config) return;
        var current = config.GetRaw(ServerChoiceKey) is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("csharp", out var c) ? c.GetString() : null;
        current ??= "roslyn"; // the group's default (BuiltinLanguageServers)
        QuickPickItem Item(string id, string label, string detail) => new(label, id == current ? "current" : null, detail, Tag: id);
        var picked = await _window.ShowQuickPickAsync(
        [
            Item("roslyn", "Roslyn (Microsoft.CodeAnalysis.LanguageServer)", "The default; what VS Code's C# extension runs: the whole solution, analyzers and .editorconfig severities, source-generated files."),
            Item("csharp", "csharp-ls", "Lighter: loads the projects near the open files; compiler diagnostics only (docs/CSHARP.md)."),
        ], new QuickPickOptions("C#: Select Language Server", "Which server should run C# files?"), ct).ConfigureAwait(true);
        if (picked?.Tag is not string id || id == current) return;
        await config.UpdateAsync(ServerChoiceKey, new Dictionary<string, string> { ["csharp"] = id }).ConfigureAwait(true);
        _window.SetStatusBarMessage($"C#: {picked.Label} serves C# files from now on", TimeSpan.FromSeconds(5));
    }

    public void Dispose() => _subs.Dispose();
}
