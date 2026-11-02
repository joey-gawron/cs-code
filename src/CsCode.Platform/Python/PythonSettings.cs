using System.Text.Json;
using CsCode.Core.Platform;

namespace CsCode.Platform.Python;

/// <summary>The Python environment settings (VS Code's Python extension names).</summary>
public static class PythonSettingKeys
{
    public const string DefaultInterpreterPath = "python.defaultInterpreterPath";
    public const string ActivateEnvironment = "python.terminal.activateEnvironment";

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static IReadOnlyList<ConfigurationProperty> Properties { get; } =
    [
        // Machine scope: it names a program CsCode runs, so a cloned repository's settings cannot choose it (its own .venv is found anyway).
        new(DefaultInterpreterPath, JsonSerializer.SerializeToElement("python"),
            "The interpreter used for a folder until one is picked with Python: Select Interpreter. \"python\" (the default) means the best one found " +
            "(the folder's .venv, venv or env first). ${workspaceFolder} and ~ are expanded. User settings only.",
            Json("""{"type":"string"}""")) { Scope = ConfigurationScope.Machine },
        new(ActivateEnvironment, JsonSerializer.SerializeToElement(true),
            "Puts the selected Python environment first on PATH (and sets VIRTUAL_ENV) in new terminals.",
            Json("""{"type":"boolean"}""")),
    ];
}
