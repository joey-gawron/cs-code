using CsCode.Core.Lifecycle;

namespace CsCode.Core.Build;

/// <summary>What a CMake target builds (the File API's <c>type</c>).</summary>
public enum CMakeTargetType { Executable, StaticLibrary, SharedLibrary, ModuleLibrary, ObjectLibrary, InterfaceLibrary, Utility }

/// <summary>Where a project stands: never configured, configuring now, configured (targets known), or its last configure failed.</summary>
public enum CMakeProjectState { NotConfigured, Configuring, Configured, ConfigureFailed }

/// <summary>
/// One target of a configured project, from the CMake File API's <c>codemodel-v2</c> reply (never from parsing <c>CMakeLists.txt</c>). Paths are
/// absolute. <see cref="Dependencies"/> names the project's own targets this one depends on (<c>gtest_main</c> when GoogleTest comes from
/// <c>FetchContent</c>); <see cref="LinkLibraries"/> holds the link line's library fragments as CMake wrote them (an imported
/// <c>GTest::gtest</c> shows here as its library path). <see cref="Sources"/> is capped per target by the provider.
/// </summary>
public sealed record CMakeTarget(
    string Name,
    CMakeTargetType Type,
    IReadOnlyList<string> Artifacts,
    string SourceDirectory,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> LinkLibraries,
    IReadOnlyList<string> Sources)
{
    /// <summary>The program a launch or a test run starts: the first artifact of an executable target, else null.</summary>
    public string? Executable => Type == CMakeTargetType.Executable && Artifacts.Count > 0 ? Artifacts[0] : null;
}

/// <summary>
/// A CMake project of the workspace: a folder with <c>CMakeLists.txt</c> at its root (or at <c>cmake.sourceDirectory</c>), the build directory
/// it is configured into, the presets and build type in use, and the targets of its last successful configure (empty before one).
/// <see cref="Configuration"/> is what <c>--config</c> / <c>ctest -C</c> takes: the build type, or a multi-config generator's chosen configuration.
/// <see cref="CTestPath"/> is the <c>ctest</c> next to the <c>cmake</c> that configured the build (the File API index names it), else null.
/// <see cref="Generation"/> grows with every configure that changed the targets, so a reader can tell a stale snapshot.
/// </summary>
public sealed record CMakeProject(
    string SourceDirectory,
    string BuildDirectory,
    CMakeProjectState State,
    string? ConfigurePreset,
    string? BuildPreset,
    string? Configuration,
    string? Generator,
    IReadOnlyList<CMakeTarget> Targets,
    string? CTestPath = null,
    long Generation = 0)
{
    /// <summary>The source directory's name, for labels.</summary>
    public string Name => Path.GetFileName(SourceDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
}

/// <summary>How a build ended: its exit code (null: cancelled or never started), the errors and warnings the build output reported, and why it could not run.</summary>
public sealed record CMakeBuildResult(int? ExitCode, int Errors = 0, int Warnings = 0, string? Error = null)
{
    public bool Succeeded => ExitCode == 0 && Error is null;
}

/// <summary>A build finished: which project, which targets (empty: all) and how it ended.</summary>
public sealed record CMakeBuildFinished(CMakeProject Project, IReadOnlyList<string> Targets, CMakeBuildResult Result);

/// <summary>
/// The workspace's CMake projects (stage 3, <c>CsCode.Build.CMake</c>; docs/C-CPP.md). The C++ test providers read targets and their
/// executables here, and build through it before a run. Every member is thread-safe; events fire on a background thread. Configure and build
/// output go to the "CMake" output channel and the build's compiler errors to the Error List; neither throws for a tool failure (see the results).
/// </summary>
public interface ICMakeService
{
    /// <summary>The current snapshot of every detected project (configured or not), in workspace-folder order.</summary>
    IReadOnlyList<CMakeProject> Projects { get; }

    /// <summary>A project appeared, went away or changed (state, presets, targets): the new snapshot of <see cref="Projects"/>.</summary>
    IEventSource<IReadOnlyList<CMakeProject>> ProjectsChanged { get; }

    /// <summary>Fires after every build through <see cref="BuildAsync"/> (a test provider re-lists a target's tests after it was built).</summary>
    IEventSource<CMakeBuildFinished> BuildFinished { get; }

    /// <summary>The project whose source or build directory contains <paramref name="path"/> (the deepest), or null.</summary>
    CMakeProject? ProjectFor(string path);

    /// <summary>Configures the project (its configure preset, else the settings' generator, build type and arguments); the new snapshot, or null when it failed or was cancelled.</summary>
    ValueTask<CMakeProject?> ConfigureAsync(CMakeProject project, CancellationToken ct = default);

    /// <summary>
    /// Builds <paramref name="targets"/> (null or empty: the default target, i.e. all), configuring first when the project never was. Errors go to
    /// the Error List; cancelling kills the build's process tree.
    /// </summary>
    ValueTask<CMakeBuildResult> BuildAsync(CMakeProject project, IReadOnlyList<string>? targets = null, CancellationToken ct = default);
}
