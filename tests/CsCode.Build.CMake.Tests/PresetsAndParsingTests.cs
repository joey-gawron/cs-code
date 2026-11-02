using Xunit;
using CsCode.Core.Build;
using CsCode.Core.Languages;
using CsCode.TestDoubles;

namespace CsCode.Build.CMake.Tests;

public sealed class PresetsAndParsingTests
{
    [Fact]
    public void Presets_resolve_inheritance_macros_and_hide_hidden_ones()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("CMakePresets.json", """
            {
              "version": 6,
              // comments and trailing commas are accepted, as CMake accepts them
              "configurePresets": [
                { "name": "base", "hidden": true, "generator": "Ninja", "binaryDir": "${sourceDir}/out/${presetName}-$env{FLAVOR}",
                  "environment": { "FLAVOR": "x" }, "cacheVariables": { "CMAKE_BUILD_TYPE": "Debug", "OPT": true } },
                { "name": "dev", "displayName": "Developer", "inherits": "base" },
                { "name": "rel", "inherits": ["base"], "cacheVariables": { "CMAKE_BUILD_TYPE": { "type": "STRING", "value": "Release" } } },
                { "name": "win", "inherits": "base", "condition": { "type": "equals", "lhs": "${hostSystemName}", "rhs": "Windows" } },
              ],
              "buildPresets": [ { "name": "dev-build", "configurePreset": "dev", "configuration": "Debug", "targets": ["app"] } ]
            }
            """);
        dir.WriteFile("CMakeUserPresets.json", """
            { "version": 6, "configurePresets": [ { "name": "mine", "inherits": "dev", "binaryDir": "${sourceParentDir}/elsewhere/${sourceDirName}" } ] }
            """);

        var presets = CMakePresets.Load(dir.Path, _ => null);

        Assert.Null(presets.Error);
        var names = presets.Configure.Select(p => p.Name).ToList();
        Assert.DoesNotContain("base", names);
        Assert.Equal(!OperatingSystem.IsWindows(), !names.Contains("win"));
        var dev = presets.Configure.Single(p => p.Name == "dev");
        Assert.Equal("Developer", dev.DisplayName);
        Assert.Equal("Ninja", dev.Generator);
        Assert.Equal(Path.Combine(dir.Path, "out", "dev-x"), dev.BinaryDir);
        Assert.Equal("Debug", dev.BuildType);
        Assert.Equal("ON", dev.CacheVariables["OPT"]);
        Assert.Equal("Release", presets.Configure.Single(p => p.Name == "rel").BuildType);
        var mine = presets.Configure.Single(p => p.Name == "mine");
        Assert.Equal(Path.Combine(Path.GetDirectoryName(dir.Path)!, "elsewhere", Path.GetFileName(dir.Path)), mine.BinaryDir);
        var build = Assert.Single(presets.Build);
        Assert.Equal(("dev", "Debug"), (build.ConfigurePreset, build.Configuration));
        Assert.Equal(["app"], build.Targets);
    }

    [Fact]
    public void Broken_presets_report_an_error_and_no_presets()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("CMakePresets.json", "{ \"version\": 6, \"configurePresets\": [ ");
        var presets = CMakePresets.Load(dir.Path);
        Assert.True(presets.Exists);
        Assert.Empty(presets.Configure);
        Assert.NotNull(presets.Error);
        Assert.False(CMakePresets.Load(Path.Combine(dir.Path, "none")).Exists);
    }

    [Fact]
    public void Build_output_problems_go_to_the_error_list_with_paths_resolved()
    {
        using var dir = new TempDirectory();
        var source = dir.WriteFile("src/main.c", "int main(void) { return x; }\n");
        Directory.CreateDirectory(dir.Combine("build"));
        var diagnostics = new DiagnosticService();
        var parser = new BuildOutputParser(diagnostics, "cmake.build:x", dir.Combine("build"), dir.Path);
        parser.Start();
        parser.Line("[1/3] Building C object CMakeFiles/app.dir/src/main.c.o");
        parser.Line("FAILED: CMakeFiles/app.dir/src/main.c.o");
        parser.Line("\u001b[1m../src/main.c:1:25: \u001b[0m\u001b[0;1;31merror: \u001b[0m\u001b[1muse of undeclared identifier 'x'\u001b[0m");
        parser.Line($"{source}:1:5: warning: unused variable 'y' [-Wunused-variable]");
        parser.Line($"{source}:1:5: note: declared here");
        parser.Line("1 error generated.");
        parser.Complete();

        var found = diagnostics.Get(new Uri(source));
        Assert.Equal(3, found.Count);
        var error = found.Single(d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal("use of undeclared identifier 'x'", error.Message);
        Assert.Equal((0, 24), (error.Range.Start.Line, error.Range.Start.Character));
        var warning = found.Single(d => d.Severity == DiagnosticSeverity.Warning);
        Assert.Equal(("unused variable 'y'", "-Wunused-variable"), (warning.Message, warning.Code));
        Assert.Equal((1, 1), (parser.Errors, parser.Warnings));

        parser = new BuildOutputParser(diagnostics, "cmake.build:x", dir.Combine("build"), dir.Path);
        parser.Start(); // a new run clears the last one's problems
        parser.Complete();
        Assert.Empty(diagnostics.Get(new Uri(source)));
    }

    [Fact]
    public void CMake_errors_become_problems_on_the_cmake_file()
    {
        using var dir = new TempDirectory();
        var lists = dir.WriteFile("CMakeLists.txt", "add_executable(app missing.c)\n");
        var diagnostics = new DiagnosticService();
        var parser = new BuildOutputParser(diagnostics, "cmake.configure:x", dir.Combine("build"), dir.Path);
        parser.Start();
        foreach (var line in new[]
        {
            "-- Configuring done (0.1s)",
            "CMake Error at CMakeLists.txt:1 (add_executable):",
            "  Cannot find source file:",
            "",
            "    missing.c",
            "",
            "",
            "-- Generating done (0.0s)",
        }) parser.Line(line);
        parser.Complete();

        var problem = Assert.Single(diagnostics.Get(new Uri(lists)));
        Assert.Equal(DiagnosticSeverity.Error, problem.Severity);
        Assert.Equal("Cannot find source file: missing.c", problem.Message);
        Assert.Equal("add_executable", problem.Code);
        Assert.Equal(0, problem.Range.Start.Line);
    }

    [Fact]
    public void File_api_reply_gives_targets_with_absolute_paths()
    {
        using var dir = new TempDirectory();
        var build = dir.Combine("build");
        var reply = Path.Combine(build, ".cmake", "api", "v1", "reply");
        Directory.CreateDirectory(reply);
        File.WriteAllText(Path.Combine(reply, "index-2026-11-01T00-00-00-0000.json"), $$"""
            { "cmake": { "generator": { "multiConfig": false, "name": "Ninja" }, "paths": { "ctest": "/opt/cmake/bin/ctest" } },
              "reply": { "client-cscode": { "codemodel-v2": { "jsonFile": "codemodel-v2-1.json" } } } }
            """);
        File.WriteAllText(Path.Combine(reply, "codemodel-v2-1.json"), $$"""
            { "paths": { "build": "{{build}}", "source": "{{dir.Path}}" },
              "configurations": [ { "name": "Debug", "targets": [
                { "name": "app", "id": "app::@1", "jsonFile": "target-app.json" },
                { "name": "lib", "id": "lib::@1", "jsonFile": "target-lib.json" },
                { "name": "ZERO_CHECK", "id": "ZERO_CHECK::@1", "jsonFile": "target-zero.json" } ] } ] }
            """);
        File.WriteAllText(Path.Combine(reply, "target-app.json"), """
            { "name": "app", "type": "EXECUTABLE", "artifacts": [ { "path": "bin/app" } ], "paths": { "source": "src", "build": "src" },
              "sources": [ { "path": "src/main.c" }, { "path": "/abs/gen.c" } ], "dependencies": [ { "id": "lib::@1" } ],
              "link": { "commandFragments": [ { "fragment": "-g", "role": "flags" }, { "fragment": "liblib.a", "role": "libraries" } ] } }
            """);
        File.WriteAllText(Path.Combine(reply, "target-lib.json"), """{ "name": "lib", "type": "STATIC_LIBRARY", "artifacts": [ { "path": "liblib.a" } ] }""");
        File.WriteAllText(Path.Combine(reply, "target-zero.json"), """{ "name": "ZERO_CHECK", "type": "UTILITY", "isGeneratorProvided": true }""");

        var result = CMakeFileApi.Read(build, "Debug");

        Assert.NotNull(result);
        Assert.Equal(("Ninja", false, "/opt/cmake/bin/ctest"), (result.Generator, result.MultiConfig, result.CTestPath));
        Assert.Equal(["app", "lib"], result.Targets.Select(t => t.Name));
        var app = result.Targets[0];
        Assert.Equal(CMakeTargetType.Executable, app.Type);
        Assert.Equal(Path.Combine(build, "bin", "app"), app.Executable);
        Assert.Equal(Path.Combine(dir.Path, "src"), app.SourceDirectory);
        Assert.Equal([Path.Combine(dir.Path, "src", "main.c"), "/abs/gen.c"], app.Sources);
        Assert.Equal(["lib"], app.Dependencies);
        Assert.Equal(["liblib.a"], app.LinkLibraries);
        Assert.Null(result.Targets[1].Executable);
    }
}
