using CsCode.Core.Projects;
using CsCode.Core.Python;
using CsCode.ProjectTemplates.Generators;

namespace CsCode.ProjectTemplates.Engine;

/// <summary>
/// What runs once a new project has opened in this window, through the language stages' services when they are registered: a Python
/// project's <c>.venv</c> becomes its folder's interpreter (<see cref="IPythonEnvironmentService"/>). Each step reports to the progress and
/// never throws. A project opened in a new window gets the interpreter from its <c>.vscode/settings.json</c>
/// (<c>python.defaultInterpreterPath</c>, which the environment service reads) instead.
/// </summary>
public sealed class ProjectFollowUps(IPythonEnvironmentService? python = null)
{
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
