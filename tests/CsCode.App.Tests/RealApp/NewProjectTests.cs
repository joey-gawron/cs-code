using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using CsCode.App.Menus;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.App.Views;
using CsCode.Core.Debugging;
using CsCode.Core.Platform;
using CsCode.Core.Projects;
using CsCode.Dap.Adapters;
using CsCode.Panels.NewProject;
using CsCode.Panels.NewProject.ViewModels;
using CsCode.Panels.NewProject.Views;
using CsCode.ProjectTemplates.Generators;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>
/// The New Project page in the REAL application (<see cref="RealAppHost"/>: every real module, <c>CSCODE_HOME</c> in a temp directory, headless
/// window): File ▸ New ▸ Project…, the palette command and the Welcome page's link open it; for each installed toolchain a project made on the page
/// opens in the window with its main file, and F5 (its generated launch.json and tasks.json) builds it and stops at a breakpoint in that file,
/// wherever that language's debugger is on main (CodeLLDB for Rust, C, C++ and Zig; netcoredbg for C#; js-debug for JavaScript). Each case skips with its reason when its
/// tool or debugger is missing.
/// </summary>
public class NewProjectTests
{
    private static void Click(Control row, TopLevel root) =>
        row.RaiseEvent(new PointerPressedEventArgs(row, new Pointer(1, PointerType.Mouse, true), root, default, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));

    private static NewProjectView? Page(RealAppHost host) => host.Window.GetVisualDescendants().OfType<NewProjectView>().FirstOrDefault();

    [AvaloniaFact(Timeout = 60_000)]
    public async Task File_New_Project_and_the_palette_command_open_the_page()
    {
        var file = ShellMenus.Build().Single(m => m.Header == "_File");
        var newMenu = file.Children!.Single(m => m.Header == "_New");
        Assert.Equal(["_Project...", "_File"], newMenu.Children!.Select(m => m.Header));
        Assert.Equal(NewProjectHost.OpenCommand, newMenu.Children![0].CommandId);

        await using var host = await RealAppHost.Create().StartAsync();
        var command = host.Get<ICommandService>().All.Single(c => c.Id == NewProjectHost.OpenCommand);
        Assert.Equal(("New Project...", "File"), (command.Title, command.Category)); // "File: New Project..." in the palette
        await host.RunAsync(NewProjectHost.OpenCommand);
        Assert.True(await RealAppHost.WaitFor(() => Page(host) is not null), "the New Project page opened");
        Assert.Equal("New Project", host.Shell.Well.Active?.Title);
        var vm = (NewProjectViewModel)Page(host)!.DataContext!;
        Assert.True(await RealAppHost.WaitFor(() => vm.Initialized.IsCompleted, 30_000), "the tool checks answered");
        Assert.Equal(["empty", "python", "rust", "cmake-c", "cmake-cpp", "dotnet", "zig", "node-ts", "node-js", "static-site"], vm.Generators.Select(g => g.Id));
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task The_Welcome_page_link_opens_the_page()
    {
        await using var host = await RealAppHost.Create().StartAsync();
        await host.RunAsync(ShellMenus.Welcome);
        var welcome = host.Window.GetVisualDescendants().OfType<WelcomeView>().Single();
        var link = welcome.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Create a new project");
        Click((Control)link.GetVisualParent()!, host.Window);
        Assert.True(await RealAppHost.WaitFor(() => Page(host) is not null), "the New Project page opened");
    }

    /// <summary>Screenshots of the page (Rust chosen, with its options) and of the Welcome link, into .scratch/screenshots (never committed).</summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task Screenshots_of_the_page()
    {
        await using var host = await RealAppHost.Create().StartAsync();
        await host.RunAsync(ShellMenus.Welcome);
        Capture(host, "new-project-welcome-link");
        await host.RunAsync(NewProjectHost.OpenCommand);
        Assert.True(await RealAppHost.WaitFor(() => Page(host) is not null));
        var vm = (NewProjectViewModel)Page(host)!.DataContext!;
        Assert.True(await RealAppHost.WaitFor(() => vm.Initialized.IsCompleted, 30_000));
        vm.SelectedGenerator = vm.Generators.Single(g => g.Id == "rust");
        Assert.True(await RealAppHost.WaitFor(() => vm.OptionsLoaded.IsCompleted && vm.Options.Count > 0, 30_000));
        await DevScene.SettleAsync(10);
        Capture(host, "new-project-rust");
        vm.SelectedGenerator = vm.Generators.Single(g => g.Id == "dotnet");
        Assert.True(await RealAppHost.WaitFor(() => vm.OptionsLoaded.IsCompleted && vm.Options.Count > 0, 60_000));
        await DevScene.SettleAsync(10);
        Capture(host, "new-project-csharp");
    }

    private static string Capture(RealAppHost host, string name)
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var frame = host.Window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame rendered");
        var path = Path.Combine(DevHost.ScreenshotDirectory(), name + ".png");
        frame.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        return path;
    }

    /// <summary>A debugger CsCode's adapter factories find through a setting: the copies the other stages fetched under <c>.scratch/tools</c>.</summary>
    private static string? ScratchTool(params string[] relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Path.Combine([dir.FullName, ".scratch", "tools", .. relative]) is var path && File.Exists(path)) return path;
        return null;
    }

    public static TheoryData<string> Generators => ["rust", "cmake-c", "cmake-cpp", "zig", "dotnet", "node-js", "node-ts", "python", "empty", "static-site"];

    [AvaloniaTheory(Timeout = 600_000)]
    [MemberData(nameof(Generators))]
    public async Task A_new_project_opens_with_its_main_file_and_F5_stops_in_it(string generatorId)
    {
        var codelldb = ScratchTool("codelldb", CodeLldbToolchain.PinnedVersion, "extension", "adapter", "codelldb");
        var netcoredbg = ScratchTool("netcoredbg", "netcoredbg");
        var jsDebug = ScratchTool("js-debug", "v1.140.0", "js-debug", "src", "dapDebugServer.js");
        await using var host = RealAppHost.Create(h =>
        {
            var settingsPath = h.Home.Combine("config", "settings.json");
            var settings = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
            if (codelldb is not null) settings[CodeLldbAdapterFactory.SettingPath] = codelldb;
            if (netcoredbg is not null) settings[NetCoreDbgAdapterFactory.SettingPath] = netcoredbg;
            if (jsDebug is not null) settings[JsDebugAdapterFactory.SettingPath] = jsDebug;
            File.WriteAllText(settingsPath, settings.ToJsonString());
        });
        await host.StartAsync();
        await host.RunAsync(NewProjectHost.OpenCommand);
        Assert.True(await RealAppHost.WaitFor(() => Page(host) is not null), "the New Project page opened");
        var vm = (NewProjectViewModel)Page(host)!.DataContext!;
        Assert.True(await RealAppHost.WaitFor(() => vm.Initialized.IsCompleted, 60_000), "the tool checks answered");
        var item = vm.Generators.Single(g => g.Id == generatorId);
        Assert.SkipUnless(item.IsAvailable, $"{item.DisplayName}: {item.ToolSummary}. {item.InstallHint}");
        Assert.SkipWhen(generatorId == "node-ts" && Environment.GetEnvironmentVariable("CSCODE_TEST_DOWNLOADS") != "1",
            "typescript and @types/node download from the npm registry; set CSCODE_TEST_DOWNLOADS=1 to run it (only with the user's go-ahead)");

        // The user picks the generator, a folder (the physical path: netcoredbg matches breakpoints to the PDB's path, which has no /var link) and a name.
        vm.SelectedGenerator = item;
        Assert.True(await RealAppHost.WaitFor(() => vm.OptionsLoaded.IsCompleted, 60_000), "the options loaded");
        vm.Location = TempDirectory.PhysicalPath(host.Files.Path);
        vm.Name = generatorId switch { "dotnet" => "HelloApp", "node-js" or "node-ts" => "hello-app", _ => "hello_app" };
        foreach (var option in vm.Options)
            switch (generatorId, option.Id)
            {
                case ("cmake-cpp", CMakeProjectGenerator.TestsOption): option.Value = "ctest"; break; // nothing downloaded at configure
                case ("python", PythonProjectGenerator.TestsOption): option.Value = ProjectOption.False; break; // pytest comes from PyPI
            }
        Assert.True(await RealAppHost.WaitFor(() => vm.CanCreate, 30_000), $"Create is enabled ({vm.NameError} {vm.LocationError})");
        await vm.CreateAsync();

        // The window had no folder: the project opens here, with its main file, and the page has closed.
        var target = Path.Combine(vm.Location, vm.Name);
        var workspace = host.Get<IWorkspaceService>();
        Assert.True(await RealAppHost.WaitFor(() => workspace.Folders.Count == 1, 30_000), $"the folder opened ({vm.Status})");
        Assert.Equal(Path.TrimEndingDirectorySeparator(target), Path.TrimEndingDirectorySeparator(workspace.Folders[0].LocalPath));
        Assert.True(await RealAppHost.WaitFor(() => host.Shell.Well.ActiveEditor is not null && Page(host) is null, 30_000), "the main file opened and the page closed");
        var main = host.Shell.Well.ActiveEditor!.Uri;
        Assert.True(main.LocalPath.StartsWith(target, StringComparison.Ordinal), main.LocalPath);
        Assert.True(Directory.Exists(Path.Combine(target, ".git")), "the Git repository");
        Assert.True(File.Exists(Path.Combine(target, "README.md")), "the README");

        // F5 where the language's debugger is on main.
        var debugger = generatorId switch
        {
            "rust" or "cmake-c" or "cmake-cpp" or "zig" => codelldb is null ? "CodeLLDB is not in .scratch/tools (tools/fetch-codelldb.sh)" : null,
            "dotnet" => netcoredbg is null ? "netcoredbg is not in .scratch/tools" : null,
            "node-js" or "node-ts" => jsDebug is null ? "js-debug is not in .scratch/tools" : null,
            "python" => "debugpy arrives with the Python stage",
            _ => "an empty project has nothing to debug",
        };
        if (generatorId is "python" or "empty" or "static-site") return; // the project made and opened is what this case checks
        Assert.SkipWhen(debugger is not null, "F5 not checked: " + debugger);
        var debug = host.Get<IDebugService>();
        var lines = await File.ReadAllLinesAsync(main.LocalPath, TestContext.Current.CancellationToken);
        var line = Array.FindIndex(lines, l => l.Contains("println!", StringComparison.Ordinal) || l.Contains("puts(", StringComparison.Ordinal)
            || l.Contains("std::cout", StringComparison.Ordinal) || l.Contains("std.debug.print(", StringComparison.Ordinal)
            || l.Contains("Console.WriteLine", StringComparison.Ordinal) || l.Contains("console.log", StringComparison.Ordinal));
        Assert.True(line >= 0, "the print line of " + main.LocalPath);
        debug.Breakpoints.Add(main, line);
        Assert.True(await RealAppHost.WaitFor(() => debug.Configurations.Selected is not null, 20_000), "launch.json read");
        var start = host.Get<ICommandService>().ExecuteAsync(KnownCommands.DebugStart).AsTask();
        Assert.True(await RealAppHost.WaitFor(() => debug.State == DebugState.Stopped && debug.StopLocation is not null, 300_000),
            $"stopped at the breakpoint (state {debug.State}, configuration {debug.Configurations.Selected?.Name})");
        await start;
        Assert.Equal(main.LocalPath, debug.StopLocation!.Uri?.LocalPath);
        Assert.Equal(line, debug.StopLocation!.Line);
        TestContext.Current.TestOutputHelper?.WriteLine($"{generatorId}: F5 built the project and stopped at {Path.GetFileName(main.LocalPath)}:{line + 1} under {debug.Sessions.FirstOrDefault()?.Configuration.Type}");
        await host.RunAsync(KnownCommands.DebugStop);
        Assert.True(await RealAppHost.WaitFor(() => debug.State == DebugState.Inactive, 30_000), "the session ended");
    }
}
