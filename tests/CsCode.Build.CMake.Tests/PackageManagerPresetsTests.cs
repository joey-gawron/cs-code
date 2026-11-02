using Xunit;
using CsCode.Core.Languages;
using CsCode.TestDoubles;

namespace CsCode.Build.CMake.Tests;

/// <summary>
/// vcpkg's and Conan 2's CMake presets through the real cmake (stage 4's check, docs/C-CPP.md): the integration lists the presets they add and
/// configures and builds through them, so the package manager's toolchain file runs and its packages are found. Neither tool is installed here:
/// the toolchain files are stand-ins doing what theirs do for <c>find_package</c> (vcpkg's adds its installed tree to the search path,
/// Conan's its generators folder), at the paths and in the preset shapes the tools write.
/// </summary>
[Trait("Category", CMakeCSample.Category)]
public sealed class PackageManagerPresetsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CMakeService Real(string folder, InMemoryOutputService output) =>
        new(new TestWorkspace(folder), new TestConfigurationService(), new MemoryState(), new DiagnosticService(), output, null, new CMakeProcesses(), null, null);

    private const string Project = """
        cmake_minimum_required(VERSION 3.21)
        project(pm C)
        find_package(greet CONFIG REQUIRED)
        add_executable(app main.c)
        target_link_libraries(app PRIVATE greet::greet)
        """;

    /// <summary>A package's config file, as a package manager installs one: an imported target with a usage requirement main.c needs.</summary>
    private const string GreetConfig = """
        add_library(greet::greet INTERFACE IMPORTED)
        set_target_properties(greet::greet PROPERTIES INTERFACE_COMPILE_DEFINITIONS GREETING=42)
        """;

    private static void WriteProject(TempDirectory dir)
    {
        dir.WriteFile("CMakeLists.txt", Project);
        dir.WriteFile("main.c", "int main(void) { return GREETING - 42; }\n");
    }

    private static async Task<(CMakeService Service, InMemoryOutputService Output)> DetectedAsync(TempDirectory dir)
    {
        var output = new InMemoryOutputService();
        var service = Real(dir.Path, output);
        await service.Detected.WaitAsync(Ct);
        return (service, output);
    }

    [Fact]
    public async Task A_vcpkg_toolchain_preset_configures_and_builds()
    {
        if (CMakeCSample.SkipReason() is { } why) Assert.Skip(why);
        using var dir = new TempDirectory();
        WriteProject(dir);
        // vcpkg's documented presets: the toolchain under $env{VCPKG_ROOT} (CMakePresets.json), the root in the user's CMakeUserPresets.json
        dir.WriteFile("CMakePresets.json", """
            {
              "version": 3,
              "configurePresets": [
                { "name": "vcpkg", "generator": "Ninja", "binaryDir": "${sourceDir}/build/vcpkg",
                  "toolchainFile": "$env{VCPKG_ROOT}/scripts/buildsystems/vcpkg.cmake", "cacheVariables": { "CMAKE_BUILD_TYPE": "Debug" } }
              ]
            }
            """);
        var root = dir.Combine("tools", "vcpkg");
        dir.WriteFile("CMakeUserPresets.json", $$"""
            { "version": 3, "configurePresets": [ { "name": "default", "inherits": "vcpkg", "environment": { "VCPKG_ROOT": "{{root}}" } } ] }
            """);
        dir.WriteFile("tools/vcpkg/scripts/buildsystems/vcpkg.cmake",
            "list(APPEND CMAKE_PREFIX_PATH \"${CMAKE_CURRENT_LIST_DIR}/../../installed/any\")\nset(CSCODE_VCPKG_TOOLCHAIN ON CACHE BOOL \"\" FORCE)\n");
        dir.WriteFile("tools/vcpkg/installed/any/share/greet/greet-config.cmake", GreetConfig);
        var (service, output) = await DetectedAsync(dir);
        using var _ = service;

        Assert.Equal(["vcpkg", "default"], service.Views[0].ConfigurePresets.Select(p => p.Name));
        await service.SelectConfigurePresetAsync(dir.Path, "default");
        var configured = await service.ConfigureAsync(service.Projects[0], Ct).AsTask().WaitAsync(TimeSpan.FromMinutes(2), Ct);
        Assert.True(configured is not null, output.Text(CMakeService.OutputChannelName));
        Assert.Equal(dir.Combine("build", "vcpkg"), configured.BuildDirectory);
        Assert.Contains("CSCODE_VCPKG_TOOLCHAIN:BOOL=ON", File.ReadAllText(dir.Combine("build", "vcpkg", "CMakeCache.txt")), StringComparison.Ordinal);
        var built = await service.BuildAsync(configured, ["app"], Ct).AsTask().WaitAsync(TimeSpan.FromMinutes(2), Ct);
        Assert.True(built.Succeeded, output.Text(CMakeService.OutputChannelName));
    }

    [Fact]
    public async Task Conans_generated_presets_configure_and_build()
    {
        if (CMakeCSample.SkipReason() is { } why) Assert.Skip(why);
        using var dir = new TempDirectory();
        WriteProject(dir);
        // what `conan install . --output-folder=build/Release` writes: CMakeUserPresets.json including the generated presets, whose toolchain
        // (relative: CMake looks in the build directory first) puts the generators folder, where CMakeDeps wrote the package configs, on the path
        var release = dir.Combine("build", "Release");
        dir.WriteFile("CMakeUserPresets.json", """
            { "version": 4, "vendor": { "conan": {} }, "include": ["build/Release/generators/CMakePresets.json"] }
            """);
        dir.WriteFile("build/Release/generators/CMakePresets.json", $$"""
            {
              "version": 3,
              "vendor": { "conan": {} },
              "cmakeMinimumRequired": { "major": 3, "minor": 15, "patch": 0 },
              "configurePresets": [
                { "name": "conan-release", "displayName": "'conan-release' config", "description": "'conan-release' configure using 'Ninja' generator",
                  "generator": "Ninja", "cacheVariables": { "CMAKE_POLICY_DEFAULT_CMP0091": "NEW", "CMAKE_BUILD_TYPE": "Release" },
                  "toolchainFile": "generators/conan_toolchain.cmake", "binaryDir": "{{release}}" }
              ],
              "buildPresets": [ { "name": "conan-release", "configurePreset": "conan-release" } ],
              "testPresets": [ { "name": "conan-release", "configurePreset": "conan-release" } ]
            }
            """);
        dir.WriteFile("build/Release/generators/conan_toolchain.cmake", "list(PREPEND CMAKE_PREFIX_PATH \"${CMAKE_CURRENT_LIST_DIR}\")\n");
        dir.WriteFile("build/Release/generators/greet-config.cmake", GreetConfig);
        var (service, output) = await DetectedAsync(dir);
        using var _ = service;

        Assert.Equal(["conan-release"], service.Views[0].ConfigurePresets.Select(p => p.Name));
        await service.SelectConfigurePresetAsync(dir.Path, "conan-release");
        await service.SelectBuildPresetAsync(dir.Path, "conan-release");
        var configured = await service.ConfigureAsync(service.Projects[0], Ct).AsTask().WaitAsync(TimeSpan.FromMinutes(2), Ct);
        Assert.True(configured is not null, output.Text(CMakeService.OutputChannelName));
        Assert.Equal((release, "Release"), (configured.BuildDirectory, configured.Configuration));
        var built = await service.BuildAsync(configured, ["app"], Ct).AsTask().WaitAsync(TimeSpan.FromMinutes(2), Ct);
        Assert.True(built.Succeeded, output.Text(CMakeService.OutputChannelName));
        Assert.True(File.Exists(Path.Combine(release, "app")));
    }
}
