using System.Runtime.InteropServices;

namespace CsCode.Remote.Transport;

/// <summary>
/// Finds the local <c>cscode-server</c> build (next to the app: the App project references the server, so its framework-dependent build lands in
/// the same output directory) and the <c>dotnet</c> host that runs it: the host of the running process when it is one, else the runtime
/// directory this process runs on (<c>&lt;root&gt;/shared/Microsoft.NETCore.App/&lt;v&gt;/</c> → <c>&lt;root&gt;/dotnet</c>), else <c>dotnet</c> on PATH.
/// </summary>
public static class LocalServer
{
    public const string AssemblyFile = "cscode-server.dll";

    public static string? FindServerAssembly(string? configured = null, string? baseDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? Path.GetFullPath(configured) : null;
        var candidate = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, AssemblyFile);
        return File.Exists(candidate) ? candidate : null;
    }

    public static string FindDotnetHost()
    {
        var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        if (Environment.ProcessPath is { } self && string.Equals(Path.GetFileName(self), exe, StringComparison.OrdinalIgnoreCase)) return self;
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } hostPath && File.Exists(hostPath)) return hostPath;
        var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory(); // <root>/shared/Microsoft.NETCore.App/<version>/
        var root = Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", ".."));
        var candidate = Path.Combine(root, exe);
        return File.Exists(candidate) ? candidate : exe;
    }

    /// <summary>The launch line of a local server over stdio.</summary>
    /// <param name="dataDir">Its data directory (settings, state, logs).</param>
    /// <param name="belowNormal">Run the whole server tree (and the language servers it starts) below normal priority.</param>
    public static ProcessLaunch Launch(string serverAssembly, string dataDir, string? marker = null, bool belowNormal = true,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        List<string> args = [serverAssembly, "--stdio", "--data-dir", dataDir];
        if (marker is not null) args.AddRange(["--marker", marker]);
        return new ProcessLaunch(FindDotnetHost(), args) { BelowNormalPriority = belowNormal, Environment = environment };
    }
}
