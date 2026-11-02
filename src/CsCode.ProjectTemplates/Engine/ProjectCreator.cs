using CsCode.Core.Projects;

namespace CsCode.ProjectTemplates.Engine;

/// <summary>How a creation ended.</summary>
public enum ProjectCreationStatus { Created, Failed, Cancelled }

/// <summary>The outcome of <see cref="ProjectCreator.CreateAsync"/>: the result when created, else what went wrong and what was cleaned up.</summary>
public sealed record ProjectCreation(ProjectCreationStatus Status, ProjectResult? Result, string? Error = null)
{
    public bool Succeeded => Status == ProjectCreationStatus.Created;
}

/// <summary>
/// Runs a generator the way the New Project page needs it: checks the target folder (missing or empty), runs the generator, then the Git step
/// (<c>git init</c> unless the generator's tool made the repository, <c>git add -A</c>, a first commit), all off the caller's thread. When the
/// generator fails or is cancelled, what this run created is removed, and only when the project folder did not exist before: the folder and any
/// of its parents this run had to create. An empty folder the user chose is left as it is.
/// </summary>
public sealed class ProjectCreator(IToolRunner runner)
{
    /// <summary>The first commit's message.</summary>
    public const string FirstCommitMessage = "Initial commit";

    /// <summary>Null when <paramref name="directory"/> can take a new project (it does not exist, or is an empty folder), else why not. Does file I/O.</summary>
    public static string? ValidateTarget(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return "Choose a location.";
        if (!Path.IsPathFullyQualified(directory)) return "The location must be an absolute path.";
        try
        {
            if (File.Exists(directory)) return $"{directory} is a file.";
            if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()) return $"{directory} already exists and is not empty.";
            // A parent that is a file makes the folder impossible.
            for (var dir = Path.GetDirectoryName(directory); dir is not null; dir = Path.GetDirectoryName(dir))
            {
                if (Directory.Exists(dir)) break;
                if (File.Exists(dir)) return $"{dir} is a file.";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return $"{directory} cannot be used: {ex.Message}";
        }
        return null;
    }

    public async Task<ProjectCreation> CreateAsync(IProjectGenerator generator, ProjectRequest request, IProjectGenerationProgress progress, CancellationToken ct)
    {
        await Task.Yield(); // the caller (the page's command) returns at once; everything below runs on the pool
        var target = Path.TrimEndingDirectorySeparator(request.Directory);
        if (ValidateTarget(target) is { } invalid) return new ProjectCreation(ProjectCreationStatus.Failed, null, invalid);
        if (generator.ValidateName(request.Name) is { } badName) return new ProjectCreation(ProjectCreationStatus.Failed, null, badName);

        // The folders this run creates: the project folder when missing, and each missing parent (outermost last).
        var created = new List<string>();
        for (var dir = target; dir is not null && !Directory.Exists(dir); dir = Path.GetDirectoryName(dir)) created.Add(dir);
        var existedBefore = created.Count == 0;

        try
        {
            Directory.CreateDirectory(target);
            progress.Step($"Creating {generator.DisplayName} project '{request.Name}' in {target}");
            var result = await generator.GenerateAsync(request with { Directory = target }, progress, ct).ConfigureAwait(false);
            var warnings = new List<string>(result.Warnings);
            if (request.CreateGitRepository) await GitAsync(target, progress, warnings, ct).ConfigureAwait(false);
            progress.Step("Done");
            return new ProjectCreation(ProjectCreationStatus.Created, result with { Warnings = warnings });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            progress.Step("Cancelled");
            Cleanup(created, existedBefore, target, progress);
            return new ProjectCreation(ProjectCreationStatus.Cancelled, null, "Cancelled.");
        }
        // Whatever broke (a tool, the disk, a generator's own bug), the half-made project must not stay behind.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            progress.Output("error: " + ex.Message);
            progress.Step("Failed: " + ex.Message);
            Cleanup(created, existedBefore, target, progress);
            return new ProjectCreation(ProjectCreationStatus.Failed, null, ex.Message);
        }
    }

    /// <summary>
    /// <c>git init</c> (unless there is a repository already: cargo's <c>--vcs git</c>), <c>git add -A</c> and the first commit. A missing git or a
    /// commit git refuses (no <c>user.name</c>/<c>user.email</c>) is a warning: the project itself is fine.
    /// </summary>
    private async Task GitAsync(string dir, IProjectGenerationProgress progress, List<string> warnings, CancellationToken ct)
    {
        if (ToolLocator.Find("git") is not { } git)
        {
            Warn("git was not found, so no repository was created. " + Tools.Git.ForCurrentPlatform);
            return;
        }
        progress.Step("Creating the Git repository");
        if (!Directory.Exists(Path.Combine(dir, ".git")))
        {
            var init = await RunAsync(git, ["init"]).ConfigureAwait(false);
            if (!init.Succeeded)
            {
                Warn("git init failed: " + Tail(init.Output));
                return;
            }
        }
        var add = await RunAsync(git, ["add", "-A"]).ConfigureAwait(false);
        if (!add.Succeeded)
        {
            Warn("git add failed: " + Tail(add.Output));
            return;
        }
        var commit = await RunAsync(git, ["commit", "-q", "-m", FirstCommitMessage]).ConfigureAwait(false);
        if (!commit.Succeeded)
            Warn("The repository was created but the first commit failed (set git's user.name and user.email, then commit): " + Tail(commit.Output));

        Task<ToolResult> RunAsync(string tool, string[] args)
        {
            var run = new ToolRun(tool, args, dir);
            progress.Output("> " + run);
            return runner.RunAsync(run, progress.Output, ct);
        }

        void Warn(string message)
        {
            warnings.Add(message);
            progress.Warning(message);
        }
    }

    private static string Tail(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "(no output)" : string.Join(' ', lines.TakeLast(3));
    }

    private static void Cleanup(List<string> created, bool existedBefore, string target, IProjectGenerationProgress progress)
    {
        if (existedBefore)
        {
            progress.Output($"{target} existed before this run: nothing was removed.");
            return;
        }
        foreach (var dir in created)
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                // The project folder goes with everything in it; a parent only while it is empty (something else may have appeared there meanwhile).
                if (dir == target) DeleteTree(dir);
                else if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
                else break;
                progress.Output("Removed " + dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                progress.Output($"Could not remove {dir}: {ex.Message}");
                break;
            }
        }
    }

    /// <summary>Deletes a tree whose files a tool may have made read-only (git's objects).</summary>
    private static void DeleteTree(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(dir, recursive: true);
    }
}
