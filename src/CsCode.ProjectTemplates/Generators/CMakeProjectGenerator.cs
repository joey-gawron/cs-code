using System.Text.Json.Nodes;
using CsCode.Core.Projects;
using CsCode.ProjectTemplates.Engine;
using CsCode.ProjectTemplates.Templates;

namespace CsCode.ProjectTemplates.Generators;

/// <summary>
/// A CMake project written by CsCode, for C or C++: <c>CMakeLists.txt</c>, <c>CMakePresets.json</c> (Debug and Release, Ninja when it is
/// installed, compile_commands.json exported), the sources, a <c>.clangd</c> that points clangd at the Debug build's compilation database, and a
/// <c>.clang-format</c>. Tests: CTest with a plain test program for C; GoogleTest or Catch2 through <c>FetchContent</c> for C++ (downloaded at
/// the first configure). F5: CodeLLDB on the Debug build after the <c>cmake: build (debug)</c> task.
/// </summary>
public sealed class CMakeProjectGenerator(IToolRunner runner, bool cpp) : GeneratorBase(runner)
{
    public const string CId = "cmake-c";
    public const string CppId = "cmake-cpp";
    /// <summary>The configure preset the tasks, launch.json and New Project's first configure use.</summary>
    public const string DebugPreset = "debug";
    public const string StandardOption = "standard";
    public const string KindOption = "kind";
    public const string TestsOption = "tests";

    /// <summary>The pinned test frameworks (FetchContent by git tag, shallow).</summary>
    public const string GoogleTestTag = "v1.17.0";
    public const string Catch2Tag = "v3.8.1";

    public const string BuildTask = "cmake: build (debug)";
    public const string ConfigureTask = "cmake: configure (debug)";

    public override string Id => cpp ? CppId : CId;
    public override string DisplayName => cpp ? "C++" : "C";
    public override string Description => cpp ? "CMake project with GoogleTest or Catch2" : "CMake project with CTest";
    public override string? IconKey => "symbol-structure";
    public override string? LanguageId => cpp ? "cpp" : "c";
    public override int Order => cpp ? 40 : 30;

    private static string? CMake => ToolLocator.Find("cmake");
    private string? Compiler => cpp ? ToolLocator.Find("c++", "clang++", "g++", "cl") : ToolLocator.Find("cc", "clang", "gcc", "cl");

    public override ValueTask<IReadOnlyList<ProjectOption>> GetOptionsAsync(CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<ProjectOption>>(
    [
        cpp
            ? ProjectOption.Choice(StandardOption, "C++ standard", "20", [new("17", "C++17"), new("20", "C++20"), new("23", "C++23")])
            : ProjectOption.Choice(StandardOption, "C standard", "17", [new("11", "C11"), new("17", "C17"), new("23", "C23")]),
        ProjectOption.Choice(KindOption, "Type", "executable",
        [
            new("executable", "Executable", $"src/main.{Ext} and a small library it uses"),
            new("library", "Library", "A static library with a public header in include/"),
        ]),
        cpp
            ? ProjectOption.Choice(TestsOption, "Tests", "gtest",
            [
                new("gtest", "GoogleTest", $"FetchContent, googletest {GoogleTestTag} (downloaded at the first configure)"),
                new("catch2", "Catch2", $"FetchContent, Catch2 {Catch2Tag} (downloaded at the first configure)"),
                new("ctest", "CTest only", "A plain test program, nothing downloaded"),
                new("none", "None"),
            ])
            : ProjectOption.Toggle(TestsOption, "Tests (CTest)", true, "A test program run by ctest"),
    ]);

    private string Ext => cpp ? "cpp" : "c";
    private string HeaderExt => cpp ? "hpp" : "h";

    public override async ValueTask<ProjectToolCheck> CheckToolsAsync(CancellationToken ct)
    {
        var cmake = await CheckToolAsync(CMake, "cmake", Tools.CMake, ct).ConfigureAwait(false);
        if (!cmake.IsAvailable) return cmake;
        var compilerName = cpp ? "a C++ compiler (c++, clang++, g++)" : "a C compiler (cc, clang, gcc)";
        if (Compiler is not { } compiler) return ProjectToolCheck.Missing($"{compilerName} was not found", Tools.CCompiler.ForCurrentPlatform);
        var version = Path.GetFileName(compiler) == "cl" ? "cl" : await ToolLocator.VersionAsync(Runner, compiler, ct).ConfigureAwait(false);
        if (version is null) return ProjectToolCheck.Missing($"{compiler} did not run", Tools.CCompiler.ForCurrentPlatform);
        var ninja = ToolLocator.Find("ninja") is not null ? ", Ninja" : "";
        return ProjectToolCheck.Available($"{cmake.Summary}, {version}{ninja}");
    }

    public override string? ValidateName(string name) => ProjectNames.CMake(name);

    public override async Task<ProjectResult> GenerateAsync(ProjectRequest request, IProjectGenerationProgress progress, CancellationToken ct)
    {
        Require(CMake, "cmake", Tools.CMake);
        Require(Compiler, cpp ? "A C++ compiler" : "A C compiler", Tools.CCompiler);
        var layout = new Layout(request.Name, ProjectNames.CIdentifier(request.Name), cpp,
            request.Option(StandardOption, cpp ? "20" : "17"),
            request.Option(KindOption, "executable") == "library",
            cpp ? request.Option(TestsOption, "gtest") : (request.Flag(TestsOption, true) ? "ctest" : "none"),
            ToolLocator.Find("ninja") is not null);

        var files = new ProjectWriter(request.Directory);
        progress.Step("Writing CMakeLists.txt, CMakePresets.json and the sources");
        await files.WriteAsync("CMakeLists.txt", CMakeLists(layout), ct).ConfigureAwait(false);
        await files.WriteJsonAsync("CMakePresets.json", Presets(layout.Ninja), ct).ConfigureAwait(false);
        foreach (var (path, content) in Sources(layout)) await files.WriteAsync(path, content, ct).ConfigureAwait(false);
        await files.WriteAsync(".clang-format", ClangFormat, ct).ConfigureAwait(false);
        // clangd looks for compile_commands.json in build/ of each parent, not in build/debug: tell it where the Debug preset puts it.
        await files.WriteAsync(".clangd", "CompileFlags:\n  CompilationDatabase: build/debug\n", ct).ConfigureAwait(false);
        await files.WriteAsync(".gitignore", CommonFiles.GitIgnore(CommonFiles.CMakeIgnore), ct).ConfigureAwait(false);
        await files.WriteAsync(".editorconfig", CommonFiles.EditorConfig("[{CMakeLists.txt,*.cmake}]\nindent_size = 2"), ct).ConfigureAwait(false);
        var commands = new List<(string, string)> { ("Configure", "cmake --preset debug"), ("Build", "cmake --build --preset debug") };
        if (!layout.Library) commands.Add(("Run", "./build/debug/" + VsCodeFiles.Executable(layout.Name)));
        if (layout.Tests != "none") commands.Add(("Test", "ctest --preset debug"));
        await files.WriteAsync("README.md", CommonFiles.Readme(request.Name,
            $"A {(cpp ? "C++" : "C")}{layout.Standard} {(layout.Library ? "library" : "program")} built with CMake.", commands), ct).ConfigureAwait(false);
        await files.WriteJsonAsync(VsCodeFiles.TasksPath, TasksJson(layout), ct).ConfigureAwait(false);
        if (LaunchJson(layout) is { } launch) await files.WriteJsonAsync(VsCodeFiles.LaunchPath, launch, ct).ConfigureAwait(false);
        return new ProjectResult(request.Directory, layout.Library ? $"src/{layout.Ident}.{Ext}" : $"src/main.{Ext}");
    }

    /// <summary>What the files are made from.</summary>
    internal sealed record Layout(string Name, string Ident, bool Cpp, string Standard, bool Library, string Tests, bool Ninja)
    {
        public string Ext => Cpp ? "cpp" : "c";
        public string HeaderExt => Cpp ? "hpp" : "h";
        /// <summary>The library target: the project itself for a library, <c>name_core</c> beside the executable for a program.</summary>
        public string LibraryTarget => Library ? Name : Name + "_core";
        public string TestTarget => Name + "_tests";
    }

    internal static string CMakeLists(Layout l)
    {
        var lang = l.Cpp ? "CXX" : "C";
        var sb = new System.Text.StringBuilder();
        sb.Append($$"""
            cmake_minimum_required(VERSION 3.21)
            project({{l.Name}} VERSION 0.1.0 LANGUAGES {{lang}})

            set(CMAKE_{{lang}}_STANDARD {{l.Standard}})
            set(CMAKE_{{lang}}_STANDARD_REQUIRED ON)
            set(CMAKE_{{lang}}_EXTENSIONS OFF)
            # compile_commands.json for clangd (the presets set it too, for builds configured without them)
            set(CMAKE_EXPORT_COMPILE_COMMANDS ON)


            """);
        if (l.Library)
            sb.Append($$"""
                add_library({{l.Name}} src/{{l.Ident}}.{{l.Ext}})
                target_include_directories({{l.Name}} PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/include)

                """);
        else
            sb.Append($$"""
                add_library({{l.LibraryTarget}} STATIC src/{{l.Ident}}.{{l.Ext}})
                target_include_directories({{l.LibraryTarget}} PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/include)

                add_executable({{l.Name}} src/main.{{l.Ext}})
                target_link_libraries({{l.Name}} PRIVATE {{l.LibraryTarget}})

                """);
        if (l.Tests == "none") return sb.ToString();

        sb.Append("""

            option(BUILD_TESTING "Build the tests" ON)
            if(BUILD_TESTING)
              enable_testing()

            """);
        switch (l.Tests)
        {
            case "gtest":
                sb.Append($$"""
                      include(FetchContent)
                      FetchContent_Declare(
                        googletest
                        GIT_REPOSITORY https://github.com/google/googletest.git
                        GIT_TAG {{GoogleTestTag}}
                        GIT_SHALLOW TRUE
                      )
                      # Windows: link the C runtime as the project does
                      set(gtest_force_shared_crt ON CACHE BOOL "" FORCE)
                      set(INSTALL_GTEST OFF CACHE BOOL "" FORCE)
                      FetchContent_MakeAvailable(googletest)

                      add_executable({{l.TestTarget}} tests/test_{{l.Ident}}.cpp)
                      target_link_libraries({{l.TestTarget}} PRIVATE {{l.LibraryTarget}} GTest::gtest_main)
                      include(GoogleTest)
                      gtest_discover_tests({{l.TestTarget}})

                    """);
                break;
            case "catch2":
                sb.Append($$"""
                      include(FetchContent)
                      FetchContent_Declare(
                        Catch2
                        GIT_REPOSITORY https://github.com/catchorg/Catch2.git
                        GIT_TAG {{Catch2Tag}}
                        GIT_SHALLOW TRUE
                      )
                      FetchContent_MakeAvailable(Catch2)

                      add_executable({{l.TestTarget}} tests/test_{{l.Ident}}.cpp)
                      target_link_libraries({{l.TestTarget}} PRIVATE {{l.LibraryTarget}} Catch2::Catch2WithMain)
                      list(APPEND CMAKE_MODULE_PATH ${catch2_SOURCE_DIR}/extras)
                      include(Catch)
                      catch_discover_tests({{l.TestTarget}})

                    """);
                break;
            default:
                sb.Append($$"""
                      add_executable({{l.TestTarget}} tests/test_{{l.Ident}}.{{l.Ext}})
                      target_link_libraries({{l.TestTarget}} PRIVATE {{l.LibraryTarget}})
                      add_test(NAME {{l.TestTarget}} COMMAND {{l.TestTarget}})

                    """);
                break;
        }
        sb.Append("endif()\n");
        return sb.ToString();
    }

    internal static JsonObject Presets(bool ninja)
    {
        JsonObject Configure(string name, string display, string type)
        {
            var preset = new JsonObject { ["name"] = name, ["displayName"] = display };
            if (ninja) preset["generator"] = "Ninja";
            preset["binaryDir"] = "${sourceDir}/build/" + name;
            preset["cacheVariables"] = new JsonObject { ["CMAKE_BUILD_TYPE"] = type, ["CMAKE_EXPORT_COMPILE_COMMANDS"] = "ON" };
            return preset;
        }
        return new JsonObject
        {
            ["version"] = 3,
            ["configurePresets"] = new JsonArray(Configure("debug", "Debug", "Debug"), Configure("release", "Release", "Release")),
            ["buildPresets"] = new JsonArray(
                new JsonObject { ["name"] = "debug", ["configurePreset"] = "debug" },
                new JsonObject { ["name"] = "release", ["configurePreset"] = "release" }),
            ["testPresets"] = new JsonArray(
                new JsonObject { ["name"] = "debug", ["configurePreset"] = "debug", ["output"] = new JsonObject { ["outputOnFailure"] = true } },
                new JsonObject { ["name"] = "release", ["configurePreset"] = "release", ["output"] = new JsonObject { ["outputOnFailure"] = true } }),
        };
    }

    private static JsonObject TasksJson(Layout l)
    {
        var tasks = new List<JsonObject>
        {
            VsCodeFiles.ProcessTask(ConfigureTask, "cmake", ["--preset", "debug"], null),
            VsCodeFiles.ProcessTask(BuildTask, "cmake", ["--build", "--preset", "debug"], "$gcc", group: "build", dependsOn: ConfigureTask),
            VsCodeFiles.ProcessTask("cmake: build (release)", "cmake", ["--build", "--preset", "release"], "$gcc", dependsOn: "cmake: configure (release)"),
            VsCodeFiles.ProcessTask("cmake: configure (release)", "cmake", ["--preset", "release"], null),
        };
        if (l.Tests != "none") tasks.Add(VsCodeFiles.ProcessTask("ctest (debug)", "ctest", ["--preset", "debug"], null, group: "test", dependsOn: BuildTask));
        return VsCodeFiles.Tasks([.. tasks]);
    }

    /// <summary>The program for an executable; the test program for a library with tests; none for a library without (nothing to run).</summary>
    private static JsonObject? LaunchJson(Layout l)
    {
        var language = l.Cpp ? "cpp" : "c";
        var configs = new List<JsonObject>();
        if (!l.Library)
            configs.Add(VsCodeFiles.LldbLaunch($"Debug {l.Name}", "${workspaceFolder}/build/debug/" + VsCodeFiles.Executable(l.Name), BuildTask, language));
        if (l.Tests != "none")
            configs.Add(VsCodeFiles.LldbLaunch($"Debug {l.TestTarget}", "${workspaceFolder}/build/debug/" + VsCodeFiles.Executable(l.TestTarget), BuildTask, language));
        return configs.Count == 0 ? null : VsCodeFiles.Launch([.. configs]);
    }

    internal static IEnumerable<(string Path, string Content)> Sources(Layout l)
    {
        var id = l.Ident;
        var guard = id.ToUpperInvariant() + (l.Cpp ? "_HPP" : "_H");
        if (l.Cpp)
        {
            yield return ($"include/{id}.hpp", $$"""
                #ifndef {{guard}}
                #define {{guard}}

                #include <string>

                namespace {{id}} {

                /// Returns a greeting for `name`.
                std::string greet(const std::string& name);

                /// Returns the sum of `a` and `b`.
                int add(int a, int b);

                }  // namespace {{id}}

                #endif  // {{guard}}
                """);
            yield return ($"src/{id}.cpp", $$"""
                #include "{{id}}.hpp"

                namespace {{id}} {

                std::string greet(const std::string& name) { return "Hello, " + name + "!"; }

                int add(int a, int b) { return a + b; }

                }  // namespace {{id}}
                """);
            if (!l.Library)
                yield return ("src/main.cpp", $$"""
                    #include <iostream>

                    #include "{{id}}.hpp"

                    int main() {
                        std::cout << {{id}}::greet("world") << '\n';
                        return 0;
                    }
                    """);
            switch (l.Tests)
            {
                case "gtest":
                    yield return ($"tests/test_{id}.cpp", $$"""
                        #include <gtest/gtest.h>

                        #include "{{id}}.hpp"

                        TEST({{Pascal(id)}}Test, Greets) { EXPECT_EQ({{id}}::greet("world"), "Hello, world!"); }

                        TEST({{Pascal(id)}}Test, Adds) { EXPECT_EQ({{id}}::add(2, 3), 5); }
                        """);
                    break;
                case "catch2":
                    yield return ($"tests/test_{id}.cpp", $$"""
                        #include <catch2/catch_test_macros.hpp>

                        #include "{{id}}.hpp"

                        TEST_CASE("greet greets", "[{{id}}]") { REQUIRE({{id}}::greet("world") == "Hello, world!"); }

                        TEST_CASE("add adds", "[{{id}}]") { REQUIRE({{id}}::add(2, 3) == 5); }
                        """);
                    break;
                case "ctest":
                    yield return ($"tests/test_{id}.cpp", $$"""
                        #include <iostream>

                        #include "{{id}}.hpp"

                        int main() {
                            int failures = 0;
                            if ({{id}}::greet("world") != "Hello, world!") {
                                std::cerr << "greet failed\n";
                                ++failures;
                            }
                            if ({{id}}::add(2, 3) != 5) {
                                std::cerr << "add failed\n";
                                ++failures;
                            }
                            return failures == 0 ? 0 : 1;
                        }
                        """);
                    break;
            }
            yield break;
        }

        yield return ($"include/{id}.h", $$"""
            #ifndef {{guard}}
            #define {{guard}}

            /* Writes a greeting for `name` into `buffer` (at most `size` bytes, terminated) and returns `buffer`. */
            char *{{id}}_greet(const char *name, char *buffer, unsigned long size);

            /* Returns the sum of `a` and `b`. */
            int {{id}}_add(int a, int b);

            #endif /* {{guard}} */
            """);
        yield return ($"src/{id}.c", $$"""
            #include "{{id}}.h"

            #include <stdio.h>

            char *{{id}}_greet(const char *name, char *buffer, unsigned long size) {
                snprintf(buffer, size, "Hello, %s!", name);
                return buffer;
            }

            int {{id}}_add(int a, int b) { return a + b; }
            """);
        if (!l.Library)
            yield return ("src/main.c", $$"""
                #include <stdio.h>

                #include "{{id}}.h"

                int main(void) {
                    char greeting[64];
                    puts({{id}}_greet("world", greeting, sizeof greeting));
                    return 0;
                }
                """);
        if (l.Tests != "none")
            yield return ($"tests/test_{id}.c", $$"""
                #include <stdio.h>
                #include <string.h>

                #include "{{id}}.h"

                static int failures = 0;

                static void check(int ok, const char *what) {
                    if (!ok) {
                        fprintf(stderr, "FAILED: %s\n", what);
                        ++failures;
                    }
                }

                int main(void) {
                    char greeting[64];
                    check(strcmp({{id}}_greet("world", greeting, sizeof greeting), "Hello, world!") == 0, "greet");
                    check({{id}}_add(2, 3) == 5, "add");
                    return failures == 0 ? 0 : 1;
                }
                """);
    }

    private static string Pascal(string ident) =>
        string.Concat(ident.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));

    private const string ClangFormat = """
        # https://clang.llvm.org/docs/ClangFormatStyleOptions.html
        BasedOnStyle: LLVM
        IndentWidth: 4
        ColumnLimit: 100
        AllowShortFunctionsOnASingleLine: All
        """;
}
