using CsCode.App.Docking;
using CsCode.Core.Contributions;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Tasks;
using CsCode.Lsp;
using CsCode.Lsp.Manager;
using CsCode.ProjectTemplates.Generators;
using CsCode.Ui.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.App.DotNet;

/// <summary>
/// The shell's .NET commands (docs/CSHARP.md, "Tasks and NuGet"), in the palette under "NuGet:", ".NET:" and "C#:" (creating projects from
/// <c>dotnet new</c> templates belongs to the New Project page, stage 7, whose template listing New Item reuses):
/// <list type="bullet">
/// <item><c>dotnet.addPackage</c>, <c>dotnet.updatePackage</c>, <c>dotnet.removePackage</c>: the project (the active file's, else a pick), the
/// package (a search of the folder's NuGet sources, or the project's references) and the version; the change runs as a task in a terminal
/// (<c>dotnet add package</c> / <c>dotnet remove package</c>), so its output and exit code are where a build's are.</item>
/// <item><c>dotnet.restore</c>: <c>dotnet restore</c> of the active file's project (else the workspace folder) as a task.</item>
/// <item><c>csharp.selectLanguageServer</c>: Roslyn (the default) or csharp-ls (<c>cscode.lsp.serverChoice</c>, user settings).</item>
/// <item><c>dotnet.newItem</c> ("New Item…", and "New .NET Item…" on an Explorer folder of a workspace with a .NET project): a quick pick of
/// the SDK's item templates for the folder's project language (<c>dotnet new list --type item</c>), a name unless the template writes a fixed
/// file (.editorconfig, global.json), the namespace of the folder when the template takes one; a dry run first gives the files to open and
/// asks before replacing one (or restores the project when the template needs it restored), then <c>dotnet new</c> creates them.</item>
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
    public const string NewItem = "dotnet.newItem";
    /// <summary>Context key: a workspace folder has a .NET project or solution near its top (the Explorer's "New .NET Item…" shows then).</summary>
    public const string HasProjectKey = "dotnet.workspaceHasProject";
    private static readonly TimeSpan TemplatesCacheFor = TimeSpan.FromMinutes(5);

    private readonly IServiceProvider _sp;
    private readonly IWindowService _window;
    private readonly DisposableStore _subs = new();
    private (IReadOnlyList<DotnetTemplate> Templates, DateTimeOffset At)? _itemTemplates;
    private readonly Dictionary<string, bool> _takesNamespace = new(StringComparer.Ordinal); // template + language → whether --help lists --namespace

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
        void Reg(string id, string title, string category, Func<CommandInvocation, Task> run) => _subs.Add(commands.Register(id, async inv =>
        {
            try { await run(inv).ConfigureAwait(true); }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                await _window.ShowMessageAsync(MessageSeverity.Error, $"{title}: {ex.Message}").ConfigureAwait(true);
            }
            return null;
        }, new CommandMetadata(id, title, category)));
        Reg(AddPackage, "Add Package...", "NuGet", inv => AddPackageAsync(inv.Cancellation));
        Reg(UpdatePackage, "Update Package...", "NuGet", inv => UpdatePackageAsync(inv.Cancellation));
        Reg(RemovePackage, "Remove Package...", "NuGet", inv => RemovePackageAsync(inv.Cancellation));
        Reg(Restore, "Restore Packages", "NuGet", inv => RestoreAsync(inv.Cancellation));
        Reg(SelectLanguageServer, "Select Language Server...", "C#", inv => SelectLanguageServerAsync(inv.Cancellation));
        Reg(NewItem, "New Item...", ".NET", inv => NewItemAsync(inv.Args.FirstOrDefault(), inv.Cancellation));

        if (_sp.GetService<IContributionRegistry>() is { } contributions)
            _subs.Add(contributions.Contribute(ContributionSources.Builtin, AppContext.BaseDirectory,
                [new MenuContribution(MenuIds.ExplorerContext, NewItem, Group: "1_new@1", When: "explorerResourceIsFolder && " + HasProjectKey) { Title = "New .NET Item..." }]));
        if (_sp.GetService<IWorkspaceService>() is { } workspace && _sp.GetService<IUiDispatcher>() is { } ui)
            _subs.Add(workspace.FoldersChanged.Subscribe(changed => ui.Post(() => _ = UpdateHasProjectAsync())));
        _ = UpdateHasProjectAsync();
    }

    /// <summary>Sets <see cref="HasProjectKey"/> from the workspace folders (read off the UI thread; the key set on the caller's).</summary>
    private async Task UpdateHasProjectAsync()
    {
        if (_sp.GetService<IContextKeyService>() is not { } keys) return;
        var folders = _sp.GetService<IWorkspaceService>()?.Folders.Where(f => f.IsFile).Select(f => f.LocalPath).ToList() ?? [];
        try
        {
            var has = await Task.Run(async () =>
            {
                foreach (var folder in folders)
                    if (await DotNetCli.HasProjectNearTopAsync(Files, folder, CancellationToken.None).ConfigureAwait(false)) return true;
                return false;
            }).ConfigureAwait(true);
            keys.Set(HasProjectKey, has);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { /* the key stays as it was */ }
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

    // ------------------------------------------------------------------ New Item (dotnet new)

    private async Task NewItemAsync(object? target, CancellationToken ct)
    {
        if (await TargetDirectoryAsync(target, ct).ConfigureAwait(true) is not { } directory)
        {
            await _window.ShowMessageAsync(MessageSeverity.Info, "Open a folder first.").ConfigureAwait(true);
            return;
        }
        var project = await DotNetCli.NearestProjectAsync(Files, Path.Combine(directory, "_"), Folder, ct).ConfigureAwait(true);
        var language = DotNetCli.LanguageOf(project);
        if (await ItemTemplatesAsync(directory, ct).ConfigureAwait(true) is not { } all) return;
        var templates = DotNetCli.ItemTemplatesFor(all, language);
        var where = Folder is { } root && directory.StartsWith(root, StringComparison.Ordinal) ? Path.GetRelativePath(root, directory) : directory;
        var picked = await _window.ShowQuickPickAsync([.. templates.Select(t => new QuickPickItem(t.Name, t.ShortName, t.Tags, Tag: t))],
            new QuickPickOptions("New Item", where == "." ? "Choose a template" : $"Choose a template for {where}", MatchOnDescription: true), ct).ConfigureAwait(true);
        if (picked?.Tag is not DotnetTemplate template) return;

        var args = new List<string> { "new", template.ShortName };
        if (!DotNetCli.HasFixedName(template))
        {
            var name = await _window.ShowInputBoxAsync(new InputBoxOptions($"New {template.Name}", $"The name of the new item in {where}", "Name",
                Validate: v => string.IsNullOrWhiteSpace(v) ? "Enter a name." : v.Trim().IndexOfAny(['/', '\\', '\0', ':']) >= 0 ? "A name, not a path." : null), ct).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(name)) return;
            args.AddRange(["--name", WithoutSourceExtension(name.Trim())]);
        }
        if (DotNetCli.LanguagesOf(template).Count > 1) args.AddRange(["--language", language]);
        if (project is not null && await TakesNamespaceAsync(directory, template, language, ct).ConfigureAwait(true))
        {
            string? xml = null;
            try { xml = System.Text.Encoding.UTF8.GetString(await Files.ReadFileAsync(new Uri(project), ct).ConfigureAwait(true)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the project's file name then */ }
            args.AddRange(["--namespace", DotNetCli.NamespaceFor(project, xml, directory)]);
        }

        // a dry run first: the files to open afterwards, and what dotnet new would refuse (replacing a file, a project that is not restored)
        var force = false;
        var (exit, output) = await RunQuietAsync(directory, [.. args, "--dry-run"], ct).ConfigureAwait(true);
        if (exit == 73 && DotNetCli.ParseDryRun(output).Overwritten is { Count: > 0 } existing)
        {
            var replace = await _window.ShowMessageAsync(MessageSeverity.Warning, $"{string.Join(", ", existing)} already exists in {where}. Replace it?",
                [new MessageAction("Replace"), new MessageAction("Cancel", IsCloseAffordance: true)], ct).ConfigureAwait(true);
            if (replace != "Replace") return;
            force = true;
        }
        else if (exit == 100) // a constraint of the template is not met
        {
            var restore = project is not null && output.Contains("is not restored", StringComparison.Ordinal);
            var action = restore ? "Restore and Create" : "Create Anyway";
            var reason = output.ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("Failed to instantiate", StringComparison.Ordinal)) ?? output.Trim();
            if (await _window.ShowMessageAsync(MessageSeverity.Warning, $"{template.Name}: {reason}", [new MessageAction(action), new MessageAction("Cancel", IsCloseAffordance: true)], ct).ConfigureAwait(true) != action) return;
            if (restore && await RunTaskAsync("dotnet restore", Path.GetDirectoryName(project!)!, ["restore", project!], ct).ConfigureAwait(true) == 0) force = false;
            else force = true;
        }
        else if (exit != 0)
        {
            await _window.ShowMessageAsync(MessageSeverity.Error, $"New Item: dotnet new {template.ShortName} failed (exit code {exit}): {output.Trim()}").ConfigureAwait(true);
            return;
        }
        if (force || exit != 0) (exit, output) = await RunQuietAsync(directory, [.. args, "--dry-run", .. (force ? new[] { "--force" } : [])], ct).ConfigureAwait(true);
        var created = DotNetCli.ParseDryRun(output).Created;
        if (exit == 0) (exit, output) = await RunQuietAsync(directory, [.. args, .. (force ? new[] { "--force" } : [])], ct).ConfigureAwait(true);
        if (exit != 0)
        {
            await _window.ShowMessageAsync(MessageSeverity.Error, $"New Item: dotnet new {template.ShortName} failed (exit code {exit}): {output.Trim()}").ConfigureAwait(true);
            return;
        }
        var navigation = _sp.GetService<INavigationService>();
        foreach (var file in created.Select(c => Path.GetFullPath(Path.Combine(directory, c))).Where(File.Exists).Take(5))
            if (navigation is not null) await navigation.OpenAsync(new Uri(file), ct: ct).ConfigureAwait(true);
        _window.SetStatusBarMessage($"New Item: {(created.Count > 0 ? string.Join(", ", created) : template.Name)} created in {where}", TimeSpan.FromSeconds(5));
    }

    /// <summary>The folder a new item goes in: the Explorer's (its folder, or a file's folder), else the active file's, else the workspace folder.</summary>
    private async Task<string?> TargetDirectoryAsync(object? target, CancellationToken ct)
    {
        var uri = target switch { Uri u => u, string s when Uri.TryCreate(s, UriKind.Absolute, out var u) => u, _ => null };
        if (uri is { IsFile: true })
        {
            var stat = await Files.StatAsync(uri, ct).ConfigureAwait(true);
            if (stat is { Type: var type } && (type & FileType.Directory) != 0) return uri.LocalPath;
            if (stat is not null) return Path.GetDirectoryName(uri.LocalPath);
        }
        return ActiveFile is { } file ? Path.GetDirectoryName(file) : Folder;
    }

    /// <summary>The SDK's item templates (<c>dotnet new list --type item</c>), kept for a few minutes; null when listing failed (said).</summary>
    private async Task<IReadOnlyList<DotnetTemplate>?> ItemTemplatesAsync(string directory, CancellationToken ct)
    {
        if (_itemTemplates is { } cached && DateTimeOffset.UtcNow - cached.At < TemplatesCacheFor) return cached.Templates;
        var (exit, output) = await _window.WithProgressAsync("Listing the .NET item templates", true, async (_, token) =>
        {
            using var link = CancellationTokenSource.CreateLinkedTokenSource(ct, token);
            return await Task.Run(() => DotNetCli.RunAsync(directory, ["new", "list", "--type", "item"], link.Token), link.Token).ConfigureAwait(false);
        }).ConfigureAwait(true);
        var templates = exit == 0 ? DotnetTemplates.ParseList(output) : null;
        if (templates is not { Count: > 0 })
        {
            await _window.ShowMessageAsync(MessageSeverity.Error, $"New Item: dotnet new list failed (exit code {exit}): {output.Trim()}").ConfigureAwait(true);
            return null;
        }
        _itemTemplates = (templates, DateTimeOffset.UtcNow);
        return templates;
    }

    /// <summary>Whether the template has a <c>--namespace</c> option (its <c>--help</c>; remembered per template and language).</summary>
    private async Task<bool> TakesNamespaceAsync(string directory, DotnetTemplate template, string language, CancellationToken ct)
    {
        var key = template.ShortName + "\0" + language;
        if (_takesNamespace.TryGetValue(key, out var known)) return known;
        string[] help = DotNetCli.LanguagesOf(template).Count > 1 ? ["new", template.ShortName, "--help", "--language", language] : ["new", template.ShortName, "--help"];
        var (exit, output) = await RunQuietAsync(directory, help, ct).ConfigureAwait(true);
        return _takesNamespace[key] = exit == 0 && DotnetTemplates.HasOption(output, "--namespace");
    }

    private static Task<(int ExitCode, string Output)> RunQuietAsync(string directory, IReadOnlyList<string> arguments, CancellationToken ct) =>
        Task.Run(() => DotNetCli.RunAsync(directory, arguments, ct), ct);

    /// <summary>The name a template is given: without a source extension the user typed (<c>Values.cs</c> → <c>Values</c>; the template adds its own).</summary>
    internal static string WithoutSourceExtension(string name)
    {
        foreach (var extension in new[] { ".cs", ".fs", ".vb", ".razor", ".cshtml", ".proto" })
            if (name.Length > extension.Length && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return name[..^extension.Length];
        return name;
    }

    // ------------------------------------------------------------------ C# server

    /// <summary>Roslyn (the default) or csharp-ls: written into <c>cscode.lsp.serverChoice</c> next to the other groups' choices (Python's).</summary>
    private async Task SelectLanguageServerAsync(CancellationToken ct)
    {
        if (_sp.GetService<IConfigurationService>() is not { } config) return;
        var choice = new Dictionary<string, string>(BuiltinLanguageServers.ReadChoice(config.GetRaw(LspSettingKeys.ServerChoice)), StringComparer.Ordinal);
        var current = choice.GetValueOrDefault(BuiltinLanguageServers.CSharpGroup) ?? "roslyn"; // the group's default
        QuickPickItem Item(string id, string label, string detail) => new(label, id == current ? "current" : null, detail, Tag: id);
        var picked = await _window.ShowQuickPickAsync(
        [
            Item("roslyn", "Roslyn (Microsoft.CodeAnalysis.LanguageServer)", "The default; what VS Code's C# extension runs: the whole solution, analyzers and .editorconfig severities, source-generated files."),
            Item("csharp", "csharp-ls", "Lighter: loads the projects near the open files; compiler diagnostics only (docs/CSHARP.md)."),
        ], new QuickPickOptions("C#: Select Language Server", "Which server should run C# files?"), ct).ConfigureAwait(true);
        if (picked?.Tag is not string id || id == current) return;
        choice[BuiltinLanguageServers.CSharpGroup] = id;
        await config.UpdateAsync(LspSettingKeys.ServerChoice, choice).ConfigureAwait(true);
        _window.SetStatusBarMessage($"C#: {picked.Label} serves C# files from now on", TimeSpan.FromSeconds(5));
    }

    public void Dispose() => _subs.Dispose();
}
