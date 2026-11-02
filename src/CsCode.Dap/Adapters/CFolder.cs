using System.Text.Json.Nodes;
using CsCode.Core.Platform;
using CsCode.Dap.Configuration;

namespace CsCode.Dap.Adapters;

/// <summary>What the C templates need to know about a folder: a CMake project at its root, and C sources at its top level.</summary>
internal sealed record CFolder(bool HasCMake, bool HasCSources)
{
    /// <summary>
    /// The task the single-file template's preLaunchTask runs: a PROCESS task (the file's path is never re-read by a shell) compiling the active
    /// file with debug information and no optimisation next to it, with <c>$gcc</c> for its errors. clang on macOS (the Command Line Tools'),
    /// cc elsewhere.
    /// </summary>
    public const string BuildTaskLabel = "C: build active file";

    public static string BuildTasksJson => $$"""
        {
            "version": "2.0.0",
            "tasks": [
                {
                    "label": "{{BuildTaskLabel}}",
                    "type": "process",
                    "command": "{{(OperatingSystem.IsMacOS() ? "clang" : "cc")}}",
                    "args": ["-g", "-O0", "-Wall", "${file}", "-o", "${fileDirname}/${fileBasenameNoExtension}"],
                    "options": { "cwd": "${fileDirname}" },
                    "problemMatcher": "$gcc",
                    "group": "build"
                }
            ]
        }

        """;

    /// <summary>Null when the folder has neither a CMakeLists.txt nor a .c file at its top level (or cannot be read).</summary>
    public static async Task<CFolder?> ReadAsync(DapPlatform p, Uri? folder, CancellationToken ct)
    {
        if (folder is not { IsFile: true } || p.Workspace is not { } ws) return null;
        try
        {
            var entries = await ws.FileSystem.ReadDirectoryAsync(folder, ct);
            var cmake = entries.Any(e => e.Type == FileType.File && e.Name == "CMakeLists.txt");
            var c = entries.Any(e => e.Type == FileType.File && e.Name.EndsWith(".c", StringComparison.Ordinal));
            return cmake || c ? new CFolder(cmake, c) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UriFormatException) { return null; }
    }

    /// <summary>
    /// The C launch templates for a debug type: the CMake launch target (<c>${command:cmake.launchTargetPath}</c>, which the CMake integration
    /// builds first, as VS Code's CMake Tools does), and the active file built by <see cref="BuildTaskLabel"/>. <paramref name="label"/> names the
    /// debugger; CodeLLDB is told the source language.
    /// </summary>
    public static IEnumerable<JsonObject> Configurations(CFolder folder, string type, string label, bool sourceLanguages)
    {
        JsonObject With(JsonObject o)
        {
            if (sourceLanguages) o["sourceLanguages"] = new JsonArray("c");
            return o;
        }
        if (folder.HasCMake)
            yield return With(new JsonObject
            {
                ["name"] = $"C: Debug CMake launch target ({label})",
                ["type"] = type,
                ["request"] = "launch",
                ["program"] = "${command:cmake.launchTargetPath}",
                ["args"] = new JsonArray(),
                ["cwd"] = "${workspaceFolder}",
            });
        yield return With(new JsonObject
        {
            ["name"] = $"C: Debug active file ({label})",
            ["type"] = type,
            ["request"] = "launch",
            ["program"] = "${fileDirname}/${fileBasenameNoExtension}",
            ["args"] = new JsonArray(),
            ["cwd"] = "${fileDirname}",
            ["preLaunchTask"] = BuildTaskLabel,
        });
    }
}
