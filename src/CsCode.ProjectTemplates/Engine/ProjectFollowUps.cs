using CsCode.Core.Build;
using CsCode.Core.Projects;
using CsCode.Core.Python;
using CsCode.ProjectTemplates.Generators;

namespace CsCode.ProjectTemplates.Engine;

/// <summary>
/// What runs around opening a new project in this window, through the language stages' services when they are registered: a C or C++
/// project is configured with its debug preset as soon as the workspace sees it (<see cref="ICMakeService.ConfigureWhenOpenedAsync"/>, asked
/// for just before the folder opens, so the CMake integration neither asks whether to configure nor offers a preset), and a Python project's
/// <c>.venv</c> becomes its folder's interpreter (<see cref="IPythonEnvironmentService"/>). Each step reports to the progress and never throws.
/// A project opened in a new window gets the interpreter from its <c>.vscode/settings.json</c> (<c>python.defaultInterpreterPath</c>, which the
/// environment service reads) instead, and the CMake integration's own configure-on-open there.
/// </summary>
public sealed class ProjectFollowUps(IPythonEnvironmentService? python = null, ICMakeService? cmake = null)
{
    /// <summary>
    /// Call just before the folder opens in this window: asks for what must be in place when the workspace first sees the project. The task
    /// completes when that is done (a C or C++ project configured), reported to the progress; it never faults.
    /// </summary>
    public Task BeforeOpenAsync(string generatorId, ProjectResult result, IProjectGenerationProgress progress) =>
        cmake is not null && generatorId is CMakeProjectGenerator.CId or CMakeProjectGenerator.CppId
            ? ConfigureAsync(cmake, result.Directory, progress)
            : Task.CompletedTask;

    private static async Task ConfigureAsync(ICMakeService cmake, string directory, IProjectGenerationProgress progress)
    {
        try
        {
            // the request is on record before the first await: the folder opens after this returns
            var configuring = cmake.ConfigureWhenOpenedAsync(directory, CMakeProjectGenerator.DebugPreset);
            progress.Step($"Configuring CMake (preset {CMakeProjectGenerator.DebugPreset}) once the folder opens");
            var project = await configuring.ConfigureAwait(false);
            if (project is { State: CMakeProjectState.Configured })
                progress.Step($"Configured CMake: {project.Targets.Count} target{(project.Targets.Count == 1 ? "" : "s")} in {project.BuildDirectory}");
            else
                progress.Warning("CMake was not configured (the CMake output has the details); the first build configures it.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            progress.Warning($"CMake was not configured: {ex.Message}");
        }
    }

    /// <summary>Runs the steps for <paramref name="generatorId"/>'s project in <paramref name="result"/>; the folder is already open.</summary>
    public async Task RunAsync(string generatorId, ProjectResult result, IProjectGenerationProgress progress, CancellationToken ct = default)
    {
        try
        {
            if (generatorId == PythonProjectGenerator.GeneratorId) await SelectVenvAsync(result.Directory, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            progress.Warning($"After creating the project: {ex.Message}");
        }
    }

    private async Task SelectVenvAsync(string directory, IProjectGenerationProgress progress, CancellationToken ct)
    {
        var interpreter = Path.Combine(directory, PythonProjectGenerator.VenvInterpreter);
        if (python is null || !File.Exists(interpreter)) return;
        var folder = new Uri(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar);
        await python.SetActiveAsync(folder, interpreter, ct).ConfigureAwait(false);
        var active = await python.GetActiveAsync(folder, ct).ConfigureAwait(false);
        progress.Step($"Selected the interpreter {active?.Label ?? interpreter}");
    }
}
