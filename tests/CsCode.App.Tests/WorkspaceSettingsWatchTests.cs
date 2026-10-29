using Avalonia.Headless.XUnit;
using CsCode.Core.Debugging;
using CsCode.Core.Platform;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>
/// Workspace configuration directories created AFTER the folder was opened, through the REAL composition: Platform's settings service and
/// DAP's launch.json service both watch <c>.cscode/</c> and <c>.vscode/</c> through the one shared file watcher, which is where a watch on a
/// directory that did not exist yet used to go dead (and take every later subscriber of that directory with it) until a restart.
/// </summary>
public class WorkspaceSettingsWatchTests
{
    [AvaloniaFact(Timeout = 90_000)]
    public async Task Settings_and_launch_files_created_after_the_folder_was_opened_apply_without_a_restart()
    {
        await Task.Yield();
        using var home = new TempDirectory("cscode-wswatch-");
        var previousHome = Environment.GetEnvironmentVariable("CSCODE_HOME");
        Environment.SetEnvironmentVariable("CSCODE_HOME", home.Path); // never the user's real config/state directories
        try
        {
            string logFile;
            await using (var sp = AppServices.Build([]))
            {
                logFile = Path.Combine(sp.GetRequiredService<IAppPaths>().LogsDir, "cscode.log");
                await CreateConfigurationDirectoriesAfterOpening(sp, home.Combine("project"));
            }
            // Disposing the container flushed the log: the watches waited for the directories instead of failing on them.
            Assert.DoesNotContain("Cannot watch", File.Exists(logFile) ? File.ReadAllText(logFile) : "", StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CSCODE_HOME", previousHome);
        }
    }

    private static async Task CreateConfigurationDirectoriesAfterOpening(IServiceProvider sp, string root)
    {
        Directory.CreateDirectory(root);
        // Off the UI thread, as the app's startup resolves them (Platform reads its files in constructors).
        var (workspace, config, launch) = await Task.Run(() => (
            sp.GetRequiredService<IWorkspaceService>(), sp.GetRequiredService<IConfigurationService>(), sp.GetRequiredService<IDebugConfigurationService>()));
        await workspace.OpenFolderAsync(new Uri(root.TrimEnd('/') + "/"));
        await Task.Delay(300); // DAP arms its watches on its own lane

        // The user creates the directory first and the files a moment later (long after the folder-level events were handled).
        Directory.CreateDirectory(Path.Combine(root, ".cscode"));
        await Task.Delay(600);
        File.WriteAllText(Path.Combine(root, ".cscode", "settings.json"), """{ "editor.tabSize": 3 }""");
        await WaitUntil(() => Task.FromResult(config.Get<int>(SettingKeys.EditorTabSize) == 3), "the new .cscode/settings.json to apply");

        File.WriteAllText(Path.Combine(root, ".cscode", "launch.json"),
            """{ "version": "0.2.0", "configurations": [ { "name": "Later", "type": "coreclr", "request": "launch", "program": "app.dll" } ] }""");
        await WaitUntil(async () => (await launch.GetLaunchConfigurationsAsync()).Any(c => c.Name == "Later"), "the new .cscode/launch.json to load");

        Directory.CreateDirectory(Path.Combine(root, ".vscode"));
        await Task.Delay(600);
        File.WriteAllText(Path.Combine(root, ".vscode", "settings.json"), """{ "editor.fontSize": 19 }""");
        await WaitUntil(() => Task.FromResult(config.Get<int>(SettingKeys.EditorFontSize) == 19), "the new .vscode/settings.json to apply");
    }

    private static async Task WaitUntil(Func<Task<bool>> condition, string what)
    {
        for (var i = 0; i < 500; i++)
        {
            if (await condition()) return;
            await Task.Delay(20);
        }
        Assert.Fail("Timed out waiting for " + what);
    }
}
