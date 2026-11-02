namespace CsCode.TestDoubles;

/// <summary>
/// A small CMake C project for the real-tool tests of the CMake integration, the CTest provider and the C debugging matrix (stage 3): a static
/// library (<c>mathlib</c>), an executable (<c>app</c>) and a test executable (<c>math_tests</c>) registered with CTest as <c>adds</c>,
/// <c>subtracts</c> (label <c>fast</c>), <c>fails</c> (exits 1) and <c>in_dir</c> (WORKING_DIRECTORY <c>tests/</c>, where it reads a marker file).
/// With <c>presets</c>, a <c>CMakePresets.json</c> with a hidden base, <c>debug</c> and <c>release</c> (Ninja, <c>build/&lt;preset&gt;</c>).
/// Nothing is fetched: the tests skip with <see cref="SkipReason"/> when cmake or a C compiler is missing.
/// </summary>
public static class CMakeCSample
{
    public const string Category = "RealTools";

    public const string CMakeLists = """
        cmake_minimum_required(VERSION 3.20)
        project(csample C)
        enable_testing()
        add_library(mathlib STATIC src/mathlib.c)
        target_include_directories(mathlib PUBLIC include)
        add_executable(app src/main.c)
        target_link_libraries(app PRIVATE mathlib)
        add_executable(math_tests tests/math_tests.c)
        target_link_libraries(math_tests PRIVATE mathlib)
        add_test(NAME adds COMMAND math_tests adds)
        add_test(NAME subtracts COMMAND math_tests subtracts)
        set_tests_properties(subtracts PROPERTIES LABELS "fast;math")
        add_test(NAME fails COMMAND math_tests fails)
        add_test(NAME in_dir COMMAND math_tests in_dir WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}/tests)
        """;

    public const string Header = """
        #ifndef MATHLIB_H
        #define MATHLIB_H
        int add(int a, int b);
        int subtract(int a, int b);
        #endif
        """;

    public const string Library = """
        #include "mathlib.h"

        int add(int a, int b) { return a + b; }

        int subtract(int a, int b) { return a - b; }
        """;

    public const string Main = """
        #include <stdio.h>
        #include "mathlib.h"

        #if 0
        int never_compiled(void) { return 1; }
        #endif

        int main(void) {
            int total = add(2, 3);
            printf("total=%d\n", total);
            return 0;
        }
        """;

    public const string Tests = """
        #include <stdio.h>
        #include <string.h>
        #include "mathlib.h"

        int main(int argc, char **argv) {
            if (argc < 2) return 2;
            if (strcmp(argv[1], "adds") == 0) return add(2, 2) == 4 ? 0 : 1;
            if (strcmp(argv[1], "subtracts") == 0) return subtract(5, 3) == 2 ? 0 : 1;
            if (strcmp(argv[1], "fails") == 0) { printf("expected 5, got %d\n", add(2, 2)); return 1; }
            if (strcmp(argv[1], "in_dir") == 0) { FILE *f = fopen("marker.txt", "r"); if (!f) return 1; fclose(f); return 0; }
            return 2;
        }
        """;

    public const string Presets = """
        {
          "version": 6,
          "configurePresets": [
            { "name": "base", "hidden": true, "generator": "Ninja", "binaryDir": "${sourceDir}/build/${presetName}" },
            { "name": "debug", "displayName": "Debug", "inherits": "base", "cacheVariables": { "CMAKE_BUILD_TYPE": "Debug" } },
            { "name": "release", "displayName": "Release", "inherits": "base", "cacheVariables": { "CMAKE_BUILD_TYPE": { "type": "STRING", "value": "Release" } } }
          ],
          "buildPresets": [
            { "name": "debug", "configurePreset": "debug" }
          ]
        }
        """;

    /// <summary>Writes the project into <paramref name="dir"/>; returns its root.</summary>
    public static string Write(TempDirectory dir, bool presets = false)
    {
        dir.WriteFile("CMakeLists.txt", CMakeLists);
        dir.WriteFile("include/mathlib.h", Header);
        dir.WriteFile("src/mathlib.c", Library);
        dir.WriteFile("src/main.c", Main);
        dir.WriteFile("tests/math_tests.c", Tests);
        dir.WriteFile("tests/marker.txt", "here\n");
        if (presets) dir.WriteFile("CMakePresets.json", Presets);
        return dir.Path;
    }

    private static readonly string[] Extra = OperatingSystem.IsMacOS()
        ? ["/opt/homebrew/bin", "/usr/local/bin", "/opt/local/bin", "/Applications/CMake.app/Contents/bin"]
        : ["/usr/local/bin", "/usr/bin"];

    public static string? Find(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Concat(Extra))
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Null when cmake (3.21+ for ctest's JUnit output), ninja and a C compiler are present; otherwise why the test skips.</summary>
    public static string? SkipReason()
    {
        if (OperatingSystem.IsWindows()) return "the CMake real-tool tests run on macOS and Linux";
        if (Find("cmake") is null) return "cmake is not installed (PATH, Homebrew, MacPorts, CMake.app)";
        if (Find("ninja") is null) return "ninja is not installed";
        if (Find("cc") is null && Find("clang") is null && Find("gcc") is null) return "no C compiler (cc, clang, gcc) is installed";
        return null;
    }
}
