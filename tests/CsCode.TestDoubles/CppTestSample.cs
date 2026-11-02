using System.Diagnostics;

namespace CsCode.TestDoubles;

/// <summary>
/// A CMake C++ project with a GoogleTest, a Catch2 and a doctest test executable, built in a temp folder for the C++ test providers' real-tool
/// tests. The frameworks come from <c>tools/fetch-cpp-test-frameworks.sh</c> (<c>.scratch/tools/cpp-test-frameworks/prefix-path</c>, or the
/// directory named by <c>CSCODE_CPP_TEST_FRAMEWORKS</c>) through <c>find_package</c>; nothing is downloaded by a test. Tests skip with
/// <see cref="SkipReason"/> when CMake, a C++ compiler or the frameworks are missing.
/// </summary>
public static class CppTestSample
{
    public const string Category = "RealTools";
    public const string EnvironmentVariable = "CSCODE_CPP_TEST_FRAMEWORKS";

    public const string CMakeLists = """
        cmake_minimum_required(VERSION 3.20)
        project(cppsample CXX)
        set(CMAKE_CXX_STANDARD 20)
        find_package(GTest REQUIRED)
        find_package(Catch2 3 REQUIRED)
        find_package(doctest REQUIRED)
        add_executable(gtest_tests gtest_tests.cpp)
        target_link_libraries(gtest_tests GTest::gtest_main)
        add_executable(catch_tests catch_tests.cpp)
        target_link_libraries(catch_tests Catch2::Catch2WithMain)
        add_executable(doctest_tests doctest_tests.cpp)
        target_link_libraries(doctest_tests doctest::doctest)
        """;

    /// <summary>Line 8 (1-based) is <c>int sum = Add(2, 2);</c> in <c>MathTest.Adds</c>; the failure of <c>MathTest.Fails</c> is on line 14.</summary>
    public const string GoogleTestSource = """
        #include <gtest/gtest.h>
        #include <iostream>

        int Add(int a, int b) { return a + b; }

        TEST(MathTest, Adds)
        {
          int sum = Add(2, 2);
          EXPECT_EQ(sum, 4);
        }

        TEST(MathTest, Fails) {
          std::cout << "from the test" << std::endl;
          EXPECT_EQ(Add(2, 2), 5) << "two and two";
        }
        TEST(MathTest, DISABLED_Later) {}

        class Param : public ::testing::TestWithParam<int> {};
        TEST_P(Param, IsPositive) { EXPECT_GT(GetParam(), 0); }
        INSTANTIATE_TEST_SUITE_P(Numbers, Param, ::testing::Values(1, -3));
        """;

    /// <summary>The failure of <c>fails</c> is on line 8 (1-based).</summary>
    public const string Catch2Source = """
        #include <catch2/catch_test_macros.hpp>
        #include <iostream>

        static int Add(int a, int b) { return a + b; }

        TEST_CASE("adds numbers", "[math]") { REQUIRE(Add(2, 2) == 4); }
        TEST_CASE("fails", "[math][slow]") {
          CHECK(Add(2, 2) == 5);
        }
        TEST_CASE("hidden test", "[.]") {}
        TEST_CASE("Name, with comma [and brackets]") { REQUIRE(true); }
        TEST_CASE("has sections") {
          SECTION("first") { REQUIRE(1 == 1); }
          SECTION("second") { REQUIRE(1 == 2); }
        }
        """;

    public const string DoctestSource = """
        #define DOCTEST_CONFIG_IMPLEMENT_WITH_MAIN
        #include <doctest/doctest.h>
        static int Add(int a, int b) { return a + b; }
        TEST_CASE("adds") { CHECK(Add(2, 2) == 4); }
        TEST_CASE("fails") { CHECK(Add(2, 2) == 5); }
        TEST_SUITE("suite") {
          TEST_CASE("in suite") { CHECK(true); }
        }
        TEST_CASE("skipped" * doctest::skip()) {}
        """;

    /// <summary>The frameworks' install prefixes (<c>;</c>-separated, for <c>CMAKE_PREFIX_PATH</c>), or null.</summary>
    public static string? FindPrefixPath()
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } env)
            return File.Exists(Path.Combine(env, "prefix-path")) ? File.ReadAllText(Path.Combine(env, "prefix-path")).Trim() : null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var file = Path.Combine(dir.FullName, ".scratch", "tools", "cpp-test-frameworks", "prefix-path");
            if (File.Exists(file)) return File.ReadAllText(file).Trim();
        }
        return null;
    }

    public static string? FindCMake() =>
        OnPath("cmake") ?? new[] { "/opt/homebrew/bin/cmake", "/usr/local/bin/cmake", "/opt/local/bin/cmake", "/usr/bin/cmake" }.FirstOrDefault(File.Exists);

    public static string? FindNinja() =>
        OnPath("ninja") ?? new[] { "/opt/homebrew/bin/ninja", "/usr/local/bin/ninja", "/opt/local/bin/ninja", "/usr/bin/ninja" }.FirstOrDefault(File.Exists);

    private static string? OnPath(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, OperatingSystem.IsWindows() ? name + ".exe" : name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Null when everything is there; otherwise why the real C++ test-framework tests skip.</summary>
    public static string? SkipReason()
    {
        if (OperatingSystem.IsWindows()) return "the C++ test sample is built with CMake and a Unix compiler (macOS, Linux)";
        if (FindCMake() is null) return "cmake is not installed";
        if (OnPath("c++") is null && OnPath("clang++") is null && OnPath("g++") is null) return "no C++ compiler (c++, clang++, g++) on PATH";
        if (FindPrefixPath() is null) return $"GoogleTest, Catch2 and doctest are not installed: run tools/fetch-cpp-test-frameworks.sh (or set {EnvironmentVariable})";
        return null;
    }

    /// <summary>Writes the sample into <paramref name="folder"/> and builds it into <c>build/</c> (Debug); returns the build's output when it fails.</summary>
    public static async Task<string?> WriteAndBuildAsync(string folder, CancellationToken ct)
    {
        File.WriteAllText(Path.Combine(folder, "CMakeLists.txt"), CMakeLists);
        File.WriteAllText(Path.Combine(folder, "gtest_tests.cpp"), GoogleTestSource);
        File.WriteAllText(Path.Combine(folder, "catch_tests.cpp"), Catch2Source);
        File.WriteAllText(Path.Combine(folder, "doctest_tests.cpp"), DoctestSource);
        var cmake = FindCMake()!;
        List<string> configure = ["-S", folder, "-B", Path.Combine(folder, "build"), "-DCMAKE_BUILD_TYPE=Debug", "-DCMAKE_PREFIX_PATH=" + FindPrefixPath()];
        if (FindNinja() is { } ninja) configure.AddRange(["-G", "Ninja", "-DCMAKE_MAKE_PROGRAM=" + ninja]);
        var (code, output) = await RunAsync(cmake, configure, folder, ct);
        if (code != 0) return "cmake configure failed:\n" + output;
        (code, output) = await RunAsync(cmake, ["--build", Path.Combine(folder, "build"), "--parallel", "4"], folder, ct);
        return code == 0 ? null : "cmake --build failed:\n" + output;
    }

    private static async Task<(int Code, string Output)> RunAsync(string program, IEnumerable<string> args, string cwd, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(program) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { p.Kill(entireProcessTree: true); throw; }
        return (p.ExitCode, await stdout + await stderr);
    }
}
