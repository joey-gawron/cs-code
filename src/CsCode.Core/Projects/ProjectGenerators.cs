namespace CsCode.Core.Projects;

/// <summary>How the New Project page shows an option: a drop-down of <see cref="ProjectOption.Choices"/>, or a check box.</summary>
public enum ProjectOptionKind { Choice, Toggle }

/// <summary>One value of a <see cref="ProjectOptionKind.Choice"/> option.</summary>
public sealed record ProjectOptionChoice(string Value, string Label, string? Description = null);

/// <summary>
/// A language option of a generator (Rust's edition, C's standard, the C# template). Values travel as strings: a toggle's is
/// <see cref="True"/> or <see cref="False"/>, a choice's is one of its <see cref="Choices"/>' values. Additive Core contract (New Project page).
/// </summary>
public sealed record ProjectOption(string Id, string Label, ProjectOptionKind Kind, string DefaultValue, IReadOnlyList<ProjectOptionChoice> Choices, string? Description = null)
{
    public const string True = "true";
    public const string False = "false";

    /// <summary>
    /// The id of an option that names the version control system the generator's own tool initialises (cargo's <c>--vcs</c>). The page keeps it
    /// and its "Create Git repository" check box in step: unchecking the box chooses <c>none</c>, a choice other than <c>git</c> unchecks it.
    /// </summary>
    public const string VcsOptionId = "vcs";

    public static ProjectOption Toggle(string id, string label, bool defaultValue, string? description = null) =>
        new(id, label, ProjectOptionKind.Toggle, defaultValue ? True : False, [], description);

    public static ProjectOption Choice(string id, string label, string defaultValue, IReadOnlyList<ProjectOptionChoice> choices, string? description = null) =>
        new(id, label, ProjectOptionKind.Choice, defaultValue, choices, description);
}

/// <summary>
/// Whether the tools a generator runs are installed: a one-line <see cref="Summary"/> ("cargo 1.96.1", "zig was not found") and, when not, the
/// platform's <see cref="InstallHint"/> ("Install it with: brew install zig"). CsCode never installs a tool itself.
/// </summary>
public sealed record ProjectToolCheck(bool IsAvailable, string Summary, string? InstallHint = null)
{
    public static ProjectToolCheck Available(string summary) => new(true, summary);
    public static ProjectToolCheck Missing(string summary, string installHint) => new(false, summary, installHint);
}

/// <summary>
/// What to create: the project <see cref="Name"/>, the folder it goes in (<see cref="Directory"/>, absolute; it does not exist or is empty),
/// the generator's option values by id (a missing one takes its default) and whether to create a Git repository with a first commit.
/// </summary>
public sealed record ProjectRequest(string Name, string Directory, IReadOnlyDictionary<string, string> Options, bool CreateGitRepository)
{
    /// <summary>The value of option <paramref name="id"/>, else <paramref name="fallback"/>.</summary>
    public string Option(string id, string fallback) => Options.TryGetValue(id, out var v) && v.Length > 0 ? v : fallback;

    /// <summary>A toggle's value, else <paramref name="fallback"/>.</summary>
    public bool Flag(string id, bool fallback = false) =>
        Options.TryGetValue(id, out var v) ? string.Equals(v, ProjectOption.True, StringComparison.OrdinalIgnoreCase) : fallback;
}

/// <summary>What a generator made: the project folder and the file to open first (relative to it; null: none).</summary>
public sealed record ProjectResult(string Directory, string? MainFile, IReadOnlyList<string> Warnings)
{
    public ProjectResult(string directory, string? mainFile) : this(directory, mainFile, []) { }
}

/// <summary>Where a generator reports what it is doing. Called on any thread.</summary>
public interface IProjectGenerationProgress
{
    /// <summary>A step starts ("Running cargo new"): the page's status line and a heading in the Output channel.</summary>
    void Step(string message);

    /// <summary>A line of a tool's output (the Output channel only).</summary>
    void Output(string line);

    /// <summary>Something went wrong that does not fail the project (pytest could not be downloaded, git has no user name for the commit).</summary>
    void Warning(string message) => Output("warning: " + message);
}

/// <summary>
/// A New Project generator (File ▸ New ▸ Project…): a language's project skeleton, made by its own tool (<c>cargo new</c>, <c>dotnet new</c>,
/// <c>zig init</c>) or written by CsCode (CMake, pyproject), plus the files that make it a good start in CsCode (<c>.vscode/launch.json</c> and
/// <c>tasks.json</c> for F5, <c>.gitignore</c>, <c>README.md</c>, <c>.editorconfig</c>). Registered in DI as <c>IProjectGenerator</c>; the page lists
/// them by <see cref="Order"/>. Every member that may start a process is asynchronous and is called off the UI thread.
/// Additive Core contract (New Project page, docs/NEW-PROJECT.md).
/// </summary>
public interface IProjectGenerator
{
    /// <summary>Stable id ("rust", "cmake-c"): remembered as the last choice.</summary>
    string Id { get; }
    string DisplayName { get; }
    /// <summary>One line under the name in the list.</summary>
    string Description { get; }
    /// <summary>Codicon name of the list icon.</summary>
    string? IconKey { get; }
    /// <summary>The language id of the code it makes (null: none, the Empty project).</summary>
    string? LanguageId { get; }
    /// <summary>Position in the list (ascending).</summary>
    int Order { get; }

    /// <summary>The generator's options. May run its tool (the C# templates come from <c>dotnet new list</c>), so it is slow the first time.</summary>
    ValueTask<IReadOnlyList<ProjectOption>> GetOptionsAsync(CancellationToken ct);

    /// <summary>Whether the tools it runs are installed (and their versions). Never throws.</summary>
    ValueTask<ProjectToolCheck> CheckToolsAsync(CancellationToken ct);

    /// <summary>Null when <paramref name="name"/> is a valid project name for this toolchain, else why not (shown under the Name box).</summary>
    string? ValidateName(string name);

    /// <summary>
    /// Makes the project in <see cref="ProjectRequest.Directory"/> (created when missing). The caller checked the folder, runs the Git step after it
    /// and removes what was created when this fails or is cancelled. Throws <see cref="ProjectGenerationException"/> for a tool that failed.
    /// </summary>
    Task<ProjectResult> GenerateAsync(ProjectRequest request, IProjectGenerationProgress progress, CancellationToken ct);
}

/// <summary>A generator step failed (a tool exited with an error); its output went to the progress.</summary>
public sealed class ProjectGenerationException(string message) : Exception(message);
