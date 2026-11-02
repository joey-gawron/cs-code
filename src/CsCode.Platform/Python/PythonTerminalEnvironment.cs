using CsCode.Core.Platform;
using CsCode.Core.Python;
using CsCode.Core.Terminal;

namespace CsCode.Platform.Python;

/// <summary>
/// <c>python.terminal.activateEnvironment</c> (on by default): a new terminal gets the selected interpreter's directory first on PATH, and for a
/// virtual environment <c>VIRTUAL_ENV</c> (a conda environment: <c>CONDA_PREFIX</c>) as its activate script would set them. Only what is known
/// already is used (never waits for discovery); a terminal of a folder whose interpreter is a global one gets nothing.
/// </summary>
public sealed class PythonTerminalEnvironment(IPythonEnvironmentService environments, IConfigurationService? config, IWorkspaceService? workspace) : ITerminalEnvironmentContributor
{
    public TerminalEnvironmentContribution? Contribute(TerminalCreateOptions options)
    {
        if (config?.Get(PythonSettingKeys.ActivateEnvironment, true) == false) return null;
        var resource = options.Cwd is { Length: > 0 } cwd && Path.IsPathRooted(cwd) ? new Uri(cwd) : workspace?.PrimaryFolder;
        if (environments.GetActive(resource) is not { IsEnvironment: true } env || env.Prefix is null) return null;
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (env.Kind == PythonEnvironmentKind.Conda) variables["CONDA_PREFIX"] = env.Prefix;
        else variables["VIRTUAL_ENV"] = env.Prefix;
        variables["PYTHONHOME"] = null; // an inherited PYTHONHOME breaks a venv's interpreter
        return new TerminalEnvironmentContribution(variables, [env.BinDirectory]);
    }
}
