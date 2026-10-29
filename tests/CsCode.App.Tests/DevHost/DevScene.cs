using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CsCode.App.Services;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.App.Tests.Hosting;

/// <summary>Populates a dev host with a believable working session for screenshots and UI tests.</summary>
public static class DevScene
{
    public static async Task BuildAsync(this DevHost host, bool openFolder = true, bool debugging = false)
    {
        host.SeedWorkspace();
        host.RegisterModuleCommands(KnownCommands.Find, KnownCommands.Replace, KnownCommands.GotoDefinition, KnownCommands.FindReferences, KnownCommands.FormatDocument,
            "undo", "redo", "editor.action.clipboardCutAction", "editor.action.clipboardCopyAction", "editor.action.clipboardPasteAction", "editor.action.selectAll",
            "workbench.action.findInFiles", KnownCommands.DebugStart, KnownCommands.DebugRun, KnownCommands.DebugStop, KnownCommands.DebugRestart, KnownCommands.DebugContinue,
            KnownCommands.DebugPause, KnownCommands.DebugStepOver, KnownCommands.DebugStepInto, KnownCommands.DebugStepOut, KnownCommands.DebugToggleBreakpoint,
            "workbench.action.navigateBack", "workbench.action.navigateForward", KnownCommands.ToggleTerminal, "workbench.action.terminal.new");
        host.Commands.Register("editor.action.rename", _ => ValueTask.FromResult<object?>(null), new CommandMetadata("editor.action.rename", "Rename Symbol", "Editor"));
        host.Commands.Register("editor.action.formatDocument2", _ => ValueTask.FromResult<object?>(null), new CommandMetadata("editor.action.formatDocument2", "Format Document", "Editor"));
        host.Commands.Register("debug.demo", _ => ValueTask.FromResult<object?>(null), new CommandMetadata("debug.demo", "Start Debugging", "Debug"));
        host.Commands.Register("git.demo", _ => ValueTask.FromResult<object?>(null), new CommandMetadata("git.demo", "Toggle Line Comment", "Editor"));

        var window = host.Services.GetRequiredService<IWindowService>();
        host.Config.Set(SettingKeys.EditorFontFamily, "DejaVu Sans Mono");
        await host.ShowAsync();

        if (openFolder)
        {
            await host.WorkspaceService.OpenFolderAsync(host.Workspace.Uri);
            await DevHost.WaitFor(() => host.Window.GetVisualDescendants().OfType<ListBoxItem>().Any(i => i.DataContext?.GetType().Name == "ExplorerNode"));
        }

        var nav = host.Services.GetRequiredService<INavigationService>();
        var dir = host.Workspace.Path;
        await nav.OpenAsync(new Uri(Path.Combine(dir, "src/Demo.App/Services/Logger.cs")), preview: false);
        await nav.OpenAsync(new Uri(Path.Combine(dir, "src/Demo.Core/Models/Person.cs")), preview: false);
        await nav.OpenAsync(new Uri(Path.Combine(dir, "src/Demo.App/Program.cs")), preview: false);
        await nav.OpenAsync(new Uri(Path.Combine(dir, "docs/README.md")), preview: true);
        host.Editors.Doc(new Uri(Path.Combine(dir, "src/Demo.App/Program.cs"))).SetDirty(true);

        var diags = host.Services.GetRequiredService<IDiagnosticService>();
        var program = new Uri(Path.Combine(dir, "src/Demo.App/Program.cs"));
        diags.Set(program, "demo", [
            new Diagnostic(TextRange.FromLines(8, 8, 8, 20), "The name 'Consol' does not exist in the current context", DiagnosticSeverity.Error, "CS0103", "csharp"),
            new Diagnostic(TextRange.FromLines(2, 0, 2, 4), "Unused using directive", DiagnosticSeverity.Warning, "IDE0005", "csharp"),
            new Diagnostic(TextRange.FromLines(5, 4, 5, 10), "Class 'Program' can be made static", DiagnosticSeverity.Information, "CA1052", "csharp")]);
        diags.Set(new Uri(Path.Combine(dir, "src/Demo.Core/Models/Person.cs")), "demo", [
            new Diagnostic(TextRange.FromLines(14, 2, 14, 9), "Missing XML comment for publicly visible type or member 'Person.Name'", DiagnosticSeverity.Warning, "CS1591", "csharp"),
            new Diagnostic(TextRange.FromLines(21, 0, 21, 1), "; expected", DiagnosticSeverity.Error, "CS1002", "csharp")]);

        var output = host.Services.GetRequiredService<IOutputService>();
        var build = output.GetOrCreate("Build");
        build.AppendLine("Build started...");
        build.AppendLine("1>------ Build started: Project: Demo.App, Configuration: Debug Any CPU ------");
        build.AppendLine("1>Program.cs(9,9): error CS0103: The name 'Consol' does not exist in the current context");
        build.AppendLine("========== Build: 0 succeeded, 1 failed, 0 up-to-date ==========");
        output.GetOrCreate("Language Server (csharp)").AppendLine("Initialized OmniSharp 1.39");

        if (debugging)
        {
            host.Debug.SetState(Core.Debugging.DebugState.Stopped);
            host.Keys.Set("inDebugMode", true);
            host.Keys.Set("debugState", "stopped");
        }
        for (var i = 0; i < 30; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
    }

    public static async Task SettleAsync(int rounds = 20)
    {
        for (var i = 0; i < rounds; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
    }
}
