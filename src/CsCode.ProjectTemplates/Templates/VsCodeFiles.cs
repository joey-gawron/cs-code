using System.Text.Json.Nodes;

namespace CsCode.ProjectTemplates.Templates;

/// <summary>
/// <c>.vscode/launch.json</c> and <c>tasks.json</c> in VS Code's format, so F5 builds and debugs the new project at once. Every configuration's
/// keys are the ones CsCode's debug adapter factories read (src/CsCode.Dap/Adapters): CodeLLDB's <c>lldb</c> (<c>program</c>, <c>args</c>,
/// <c>cwd</c>, <c>sourceLanguages</c>, <c>terminal</c>, the <c>cargo</c> key), netcoredbg's <c>coreclr</c> and the Python stage's <c>debugpy</c>.
/// Tasks are process tasks (no shell re-reads a path with spaces).
/// </summary>
public static class VsCodeFiles
{
    public const string LaunchPath = ".vscode/launch.json";
    public const string TasksPath = ".vscode/tasks.json";
    public const string SettingsPath = ".vscode/settings.json";

    public static JsonObject Launch(params JsonObject[] configurations) => new()
    {
        ["version"] = "0.2.0",
        ["configurations"] = new JsonArray([.. configurations]),
    };

    public static JsonObject Tasks(params JsonObject[] tasks) => new()
    {
        ["version"] = "2.0.0",
        ["tasks"] = new JsonArray([.. tasks]),
    };

    /// <summary>A process task; <paramref name="group"/> "build" or "test" makes it that group's default (Ctrl+Shift+B runs the build one).</summary>
    public static JsonObject ProcessTask(string label, string command, IEnumerable<string> args, string? problemMatcher, string? group = null, string? dependsOn = null, string? cwd = "${workspaceFolder}")
    {
        var task = new JsonObject
        {
            ["label"] = label,
            ["type"] = "process",
            ["command"] = command,
            ["args"] = new JsonArray([.. args.Select(a => (JsonNode)a)]),
        };
        if (cwd is not null) task["options"] = new JsonObject { ["cwd"] = cwd };
        task["problemMatcher"] = problemMatcher is null ? new JsonArray() : problemMatcher;
        if (dependsOn is not null) task["dependsOn"] = dependsOn;
        if (group is not null) task["group"] = new JsonObject { ["kind"] = group, ["isDefault"] = true };
        return task;
    }

    /// <summary>A CodeLLDB launch of a native program built by <paramref name="preLaunchTask"/> (C, C++, Zig; Rust has its own below).</summary>
    public static JsonObject LldbLaunch(string name, string program, string preLaunchTask, string? sourceLanguage) => WithLanguage(new JsonObject
    {
        ["name"] = name,
        ["type"] = "lldb",
        ["request"] = "launch",
        ["program"] = program,
        ["args"] = new JsonArray(),
        ["cwd"] = "${workspaceFolder}",
        ["preLaunchTask"] = preLaunchTask,
        ["terminal"] = "console",
    }, sourceLanguage);

    private static JsonObject WithLanguage(JsonObject config, string? language)
    {
        if (language is not null) config["sourceLanguages"] = new JsonArray(language);
        return config;
    }

    /// <summary>The executable name on this platform (<c>.exe</c> on Windows).</summary>
    public static string Executable(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    /// <summary>The interpreter inside a venv, as launch.json and settings.json name it.</summary>
    public static string VenvPython => OperatingSystem.IsWindows() ? "${workspaceFolder}/.venv/Scripts/python.exe" : "${workspaceFolder}/.venv/bin/python";
}
