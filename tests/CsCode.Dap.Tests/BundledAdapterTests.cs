using CsCode.Dap.Adapters;
using CsCode.Dap.Configuration;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>
/// The debug adapters tools/install.sh copies next to the installed app (<c>{InstallDir}/debug-adapters</c>) are found by default: no setting, no
/// <c>CSCODE_*</c> variable, no download offer. The user's own choices still come first and PATH last.
/// </summary>
public class BundledAdapterTests
{
    private static string Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "#!/bin/sh\nexit 0\n");
        return path;
    }

    /// <summary>The layout install.sh writes: the tools directory's, under debug-adapters/.</summary>
    private static (string NetCoreDbg, string JsDebug, string CodeLldb) Bundle(string installDir)
    {
        var root = Path.Combine(installDir, BundledAdapters.FolderName);
        return (Touch(Path.Combine(root, "netcoredbg", NetCoreDbgAdapterFactory.ExecutableName)),
            Touch(Path.Combine(root, "js-debug", JsDebugInstaller.Version, "js-debug", "src", "dapDebugServer.js")),
            Touch(Path.Combine(root, "codelldb", CodeLldbInstaller.Version, "extension", "adapter", CodeLldbInstaller.AdapterExecutableName)));
    }

    private static Dictionary<string, string?> NoVariables() => [];

    [Fact]
    public void An_installed_app_finds_every_bundled_adapter_without_settings_or_variables()
    {
        using var install = new TempDirectory("cscode-install-");
        using var paths = new TestAppPaths { InstallDir = install.Path };
        var bundled = Bundle(install.Path);
        var platform = new DapPlatform { Paths = paths, Configuration = new TestConfigurationService() };
        var env = NoVariables();

        Assert.Equal(bundled.NetCoreDbg, new NetCoreDbgAdapterFactory(platform) { PathVariable = () => null }.Locate());
        Assert.Equal(bundled.JsDebug, new JsDebugAdapterFactory(platform) { Environment = n => env.GetValueOrDefault(n) }.Locate());
        Assert.Equal(bundled.CodeLldb, new CodeLldbAdapterFactory(platform) { Environment = n => env.GetValueOrDefault(n) }.Locate());
        Assert.Equal(Path.Combine(install.Path, "debug-adapters"), BundledAdapters.Root(paths));
    }

    /// <summary>
    /// CsCode.app: the program is in <c>Contents/MacOS</c> and tools/package-macos.sh puts the adapters in <c>Contents/Resources/debug-adapters</c>
    /// (below Contents/MacOS codesign takes <c>codelldb/v1.12.3</c> for a nested bundle and refuses the app). Before, only the program's own
    /// folder was looked in, so the bundled adapters were never found and each session offered a download.
    /// </summary>
    [Fact]
    public void A_macos_app_bundle_finds_the_adapters_in_its_resources_and_a_folder_beside_the_program_still_wins()
    {
        using var root = new TempDirectory("cscode-app-");
        var program = root.Combine("CsCode.app", "Contents", "MacOS");
        Directory.CreateDirectory(program);
        using var paths = new TestAppPaths { InstallDir = program + Path.DirectorySeparatorChar }; // AppContext.BaseDirectory ends with a separator
        var bundled = Bundle(root.Combine("CsCode.app", "Contents", "Resources"));
        var platform = new DapPlatform { Paths = paths, Configuration = new TestConfigurationService() };
        var env = NoVariables();

        Assert.Equal(root.Combine("CsCode.app", "Contents", "Resources", "debug-adapters"), BundledAdapters.Root(paths));
        Assert.Equal(bundled.NetCoreDbg, new NetCoreDbgAdapterFactory(platform) { PathVariable = () => null }.Locate());
        Assert.Equal(bundled.JsDebug, new JsDebugAdapterFactory(platform) { Environment = n => env.GetValueOrDefault(n) }.Locate());
        Assert.Equal(bundled.CodeLldb, new CodeLldbAdapterFactory(platform) { Environment = n => env.GetValueOrDefault(n) }.Locate());

        var beside = Bundle(program).CodeLldb;
        Assert.Equal(beside, BundledAdapters.CodeLldb(paths));

        // only a bundle's Contents/MacOS looks in its Resources
        using var plain = new TempDirectory("cscode-install-");
        Bundle(Path.Combine(plain.Path, "Resources"));
        using var plainPaths = new TestAppPaths { InstallDir = Path.Combine(plain.Path, "MacOS") };
        Assert.Null(BundledAdapters.CodeLldb(plainPaths));
    }

    [Fact]
    public void A_development_build_without_the_folder_finds_nothing_there()
    {
        using var install = new TempDirectory("cscode-install-");
        using var paths = new TestAppPaths { InstallDir = install.Path };
        var platform = new DapPlatform { Paths = paths };
        var env = NoVariables();
        Assert.Null(new NetCoreDbgAdapterFactory(platform) { PathVariable = () => null }.Locate());
        Assert.Null(new JsDebugAdapterFactory(platform) { Environment = n => env.GetValueOrDefault(n) }.Locate());
        Assert.Null(new CodeLldbAdapterFactory(platform) { Environment = n => env.GetValueOrDefault(n) }.Locate());
        Assert.Null(BundledAdapters.NetCoreDbg(null)); // no IAppPaths: the program's own directory (this test's bin) has none either
    }

    [Fact]
    public void Netcoredbg_the_setting_and_the_tools_copy_win_over_the_bundled_one_which_wins_over_PATH()
    {
        using var install = new TempDirectory("cscode-install-");
        using var scratch = new TempDirectory("cscode-adapters-");
        using var paths = new TestAppPaths { InstallDir = install.Path };
        var bundled = Bundle(install.Path).NetCoreDbg;
        var tools = Touch(Path.Combine(paths.ToolsDir, "netcoredbg", NetCoreDbgAdapterFactory.ExecutableName));
        var onPath = Touch(scratch.Combine("bin", NetCoreDbgAdapterFactory.ExecutableName));
        var setting = Touch(scratch.Combine("mine", NetCoreDbgAdapterFactory.ExecutableName));
        var settings = new TestConfigurationService();
        var f = new NetCoreDbgAdapterFactory(new DapPlatform { Paths = paths, Configuration = settings }) { PathVariable = () => scratch.Combine("bin") };

        settings.Set(NetCoreDbgAdapterFactory.SettingPath, setting);
        Assert.Equal(setting, f.Locate());
        settings.Set(NetCoreDbgAdapterFactory.SettingPath, "");
        Assert.Equal(tools, f.Locate()); // a copy the user downloaded through the offer
        File.Delete(tools);
        Assert.Equal(bundled, f.Locate());
        File.Delete(bundled);
        Assert.Equal(onPath, f.Locate());
    }

    [Fact]
    public void Js_debug_the_variable_and_the_tools_copy_win_over_the_bundled_one()
    {
        using var install = new TempDirectory("cscode-install-");
        using var scratch = new TempDirectory("cscode-adapters-");
        using var paths = new TestAppPaths { InstallDir = install.Path };
        var bundled = Bundle(install.Path).JsDebug;
        var tools = Touch(JsDebugInstaller.ServerScript(paths.ToolsDir));
        var viaEnv = Touch(scratch.Combine("js-debug", "src", "dapDebugServer.js"));
        var env = new Dictionary<string, string?> { [JsDebugAdapterFactory.EnvironmentVariable] = scratch.Combine("js-debug") };
        var f = new JsDebugAdapterFactory(new DapPlatform { Paths = paths }) { Environment = n => env.GetValueOrDefault(n) };

        Assert.Equal(viaEnv, f.Locate());
        env.Clear();
        Assert.Equal(tools, f.Locate());
        File.Delete(tools);
        Assert.Equal(bundled, f.Locate());
    }

    [Fact]
    public void CodeLLDB_the_variable_and_the_tools_copy_win_over_the_bundled_one_which_wins_over_PATH()
    {
        using var install = new TempDirectory("cscode-install-");
        using var scratch = new TempDirectory("cscode-adapters-");
        using var paths = new TestAppPaths { InstallDir = install.Path };
        var bundled = Bundle(install.Path).CodeLldb;
        var tools = Touch(CodeLldbInstaller.AdapterPath(paths.ToolsDir));
        var viaEnv = Touch(scratch.Combine("env", "extension", "adapter", CodeLldbInstaller.AdapterExecutableName));
        var onPath = Touch(scratch.Combine("bin", CodeLldbInstaller.AdapterExecutableName));
        var env = new Dictionary<string, string?> { [CodeLldbAdapterFactory.EnvironmentVariable] = scratch.Combine("env"), ["PATH"] = scratch.Combine("bin") };
        var f = new CodeLldbAdapterFactory(new DapPlatform { Paths = paths }) { Environment = n => env.GetValueOrDefault(n) };

        Assert.Equal(viaEnv, f.Locate());
        env.Remove(CodeLldbAdapterFactory.EnvironmentVariable);
        Assert.Equal(tools, f.Locate());
        File.Delete(tools);
        Assert.Equal(bundled, f.Locate());
        File.Delete(bundled);
        Assert.Equal(onPath, f.Locate());
    }
}
