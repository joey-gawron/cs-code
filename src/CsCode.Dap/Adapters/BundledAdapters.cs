using CsCode.Core.Platform;

namespace CsCode.Dap.Adapters;

/// <summary>
/// Debug adapters installed WITH the app: <c>tools/install.sh</c> copies the ones already on the build machine (netcoredbg, js-debug, CodeLLDB;
/// nothing is downloaded) into <c>{InstallDir}/debug-adapters</c>, laid out like the tools directory the download offers install into
/// (<c>netcoredbg/netcoredbg</c>, <c>js-debug/&lt;version&gt;/js-debug</c>, <c>codelldb/&lt;version&gt;/extension</c>), so an installed CsCode debugs
/// .NET, Node.js/TypeScript and Rust without a download offer and without the <c>CSCODE_*</c> variables. On macOS <c>tools/package-macos.sh</c>
/// puts the same folder in CsCode.app's <c>Contents/Resources</c> (the program is in <c>Contents/MacOS</c>, where codesign takes a dotted
/// folder such as <c>codelldb/v1.12.3</c> for a nested bundle and refuses the app). Each factory looks there after the user's own choices (its
/// setting, its environment variable, a copy the user downloaded into the tools directory) and before PATH. A development build has no such
/// folder, so nothing changes there.
/// </summary>
public static class BundledAdapters
{
    public const string FolderName = "debug-adapters";

    /// <summary>The folder next to the app (<see cref="IAppPaths.InstallDir"/>, else the program's directory); in a macOS app bundle whose
    /// program directory has none, the bundle's <c>Contents/Resources/debug-adapters</c> when that exists.</summary>
    public static string Root(IAppPaths? paths)
    {
        var program = paths?.InstallDir ?? AppContext.BaseDirectory;
        var beside = Path.Combine(program, FolderName);
        if (Directory.Exists(beside)) return beside;
        var dir = new DirectoryInfo(Path.TrimEndingDirectorySeparator(program));
        return dir is { Name: "MacOS", Parent: { Name: "Contents" } contents } && Path.Combine(contents.FullName, "Resources", FolderName) is var resources
            && Directory.Exists(resources) ? resources : beside;
    }

    /// <summary>The bundled netcoredbg executable, or null when the app has none.</summary>
    public static string? NetCoreDbg(IAppPaths? paths) => Existing(Path.Combine(Root(paths), "netcoredbg", NetCoreDbgAdapterFactory.ExecutableName));

    /// <summary>The bundled js-debug server script (<c>dapDebugServer.js</c>), or null.</summary>
    public static string? JsDebug(IAppPaths? paths) => Existing(JsDebugInstaller.ServerScript(Root(paths)));

    /// <summary>The bundled CodeLLDB adapter, or null.</summary>
    public static string? CodeLldb(IAppPaths? paths) => Existing(CodeLldbInstaller.AdapterPath(Root(paths)));

    private static string? Existing(string path) => File.Exists(path) ? path : null;
}
