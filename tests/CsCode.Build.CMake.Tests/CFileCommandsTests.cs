using Xunit;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Build.CMake.Tests;

/// <summary>C without CMake: Build and Run Active File and Create compile_flags.txt (<see cref="CFileCommands"/>).</summary>
public sealed class CFileCommandsTests
{
    private sealed record Rig(ServiceProvider Provider, CFileCommands Commands, RecordingNavigationService Navigation, RecordingWindowService Window,
        DiagnosticService Diagnostics) : IDisposable
    {
        public void Dispose()
        {
            Commands.Dispose();
            Provider.Dispose();
        }
    }

    private static Rig Make(string folder, ICMakeProcesses? processes = null)
    {
        var documents = new DocumentManager();
        var navigation = new RecordingNavigationService(documents);
        var window = new RecordingWindowService();
        var diagnostics = new DiagnosticService();
        var provider = new ServiceCollection().AddSingleton<INavigationService>(navigation).AddSingleton<IWindowService>(window)
            .AddSingleton<IDiagnosticService>(diagnostics).AddSingleton<IWorkspaceService>(new TestWorkspace(folder))
            .AddSingleton<IOutputService>(new InMemoryOutputService()).BuildServiceProvider();
        return new Rig(provider, new CFileCommands(provider, processes ?? new CMakeProcesses(), Environment.GetEnvironmentVariable), navigation, window, diagnostics);
    }

    [Fact]
    public async Task Create_compile_flags_writes_c_defaults_in_the_active_files_folder_and_keeps_an_existing_file()
    {
        using var dir = new TempDirectory();
        using var rig = Make(dir.Path);
        rig.Navigation.ActiveDocument = new EditorDocumentView(new InMemoryTextDocument(new Uri(dir.WriteFile("src/main.c", "int main(void) { return 0; }\n")), "c", ""));

        var path = await rig.Commands.CreateCompileFlagsAsync();

        Assert.Equal(dir.Combine(CFileCommands.CompileFlagsFile), path);
        Assert.Equal("-xc\n-std=c17\n-Wall\n-Wextra\n-I.\n-Iinclude\n", File.ReadAllText(path!));
        Assert.Equal(new Uri(path!), Assert.Single(rig.Navigation.Opened).Uri);

        File.WriteAllText(path!, "-xc\n-DMINE\n");
        dir.WriteFile("compile_commands.json", "[]");
        await rig.Commands.CreateCompileFlagsAsync();
        Assert.Equal("-xc\n-DMINE\n", File.ReadAllText(path!)); // the user's flags stay
        Assert.Contains(rig.Window.Messages, m => m.Message.Contains("compile_commands.json", StringComparison.Ordinal));
    }

    [Fact]
    public void The_program_is_built_next_to_its_source_with_debug_info()
    {
        var command = CFileCommands.CompileCommand("/usr/bin/clang", "/w/src/hello.c");
        Assert.Equal(("/usr/bin/clang", "/w/src"), (command.FileName, command.WorkingDirectory));
        Assert.Equal(["-g", "-O0", "-Wall", "/w/src/hello.c", "-o", "/w/src/hello"], command.Arguments);
    }

    [Fact]
    public async Task Build_and_run_says_so_outside_a_c_file()
    {
        using var dir = new TempDirectory();
        using var rig = Make(dir.Path, new ScriptedProcesses());
        rig.Navigation.ActiveDocument = new EditorDocumentView(new InMemoryTextDocument(new Uri(dir.WriteFile("notes.md", "")), "markdown", ""));
        Assert.Null(await rig.Commands.BuildAndRunAsync());
        Assert.Contains(rig.Window.Messages, m => m.Message.Contains("open a saved C file", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", CMakeCSample.Category)]
    public async Task A_real_build_reports_its_error_then_builds_a_program_that_runs()
    {
        using var dir = new TempDirectory();
        using var rig = Make(dir.Path);
        Assert.SkipWhen(rig.Commands.Compiler() is null, "no C compiler (clang, cc or gcc) on PATH");
        var file = dir.WriteFile("hello.c", "#include <stdio.h>\nint main(void) {\n    puts(\"hello\");\n    return missing;\n}\n");
        rig.Navigation.ActiveDocument = new EditorDocumentView(new InMemoryTextDocument(new Uri(file), "c", File.ReadAllText(file)));

        Assert.Null(await rig.Commands.BuildAndRunAsync(run: false));
        var problem = Assert.Single(rig.Diagnostics.Get(new Uri(TempDirectory.PhysicalPath(file))).Concat(rig.Diagnostics.Get(new Uri(file))).DistinctBy(d => d.Message),
            d => d.Severity == DiagnosticSeverity.Error);
        Assert.Contains("missing", problem.Message, StringComparison.Ordinal);
        Assert.Equal(3, problem.Range.Start.Line);
        Assert.Contains(rig.Window.Messages, m => m.Message.Contains("did not build: 1 error", StringComparison.Ordinal));

        File.WriteAllText(file, "#include <stdio.h>\nint main(void) {\n    puts(\"hello\");\n    return 3;\n}\n");
        var program = await rig.Commands.BuildAndRunAsync(run: false);
        Assert.Equal(dir.Combine("hello"), program);
        Assert.Empty(rig.Diagnostics.Get(new Uri(TempDirectory.PhysicalPath(file))).Concat(rig.Diagnostics.Get(new Uri(file)))); // the next build clears them
        using var run = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(program!) { RedirectStandardOutput = true })!;
        Assert.Equal("hello", (await run.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken)).Trim());
        await run.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, run.ExitCode);
    }
}
