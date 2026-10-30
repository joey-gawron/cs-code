using System.Runtime.InteropServices;
using System.Text.Json;

namespace CsCode.Remote.Transport;

/// <summary>
/// Finds the local <c>cscode-server</c> build (next to the app: the App project references the server, so its build lands in the same output
/// directory) and what runs it. In a self-contained app (an installed publish: no dotnet need exist on the machine) the server is self-contained
/// too and runs through its own apphost next to the app. Otherwise a <c>dotnet</c> host runs the dll: the host of the running process when it is
/// one, else the runtime directory this process runs on (<c>&lt;root&gt;/shared/Microsoft.NETCore.App/&lt;v&gt;/</c> → <c>&lt;root&gt;/dotnet</c>),
/// else <c>dotnet</c> on PATH.
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

    /// <summary>
    /// The server's own executable (apphost) when the build next to <paramref name="serverAssembly"/> is self-contained (its runtimeconfig names
    /// the frameworks it includes instead of the ones it needs), else null: a framework-dependent build runs through a dotnet host.
    /// </summary>
    public static string? SelfContainedHost(string serverAssembly)
    {
        var apphost = Path.ChangeExtension(serverAssembly, OperatingSystem.IsWindows() ? ".exe" : null);
        if (!File.Exists(apphost)) return null;
        try
        {
            using var config = JsonDocument.Parse(File.ReadAllBytes(Path.ChangeExtension(serverAssembly, ".runtimeconfig.json")));
            return config.RootElement.TryGetProperty("runtimeOptions", out var options) && options.TryGetProperty("includedFrameworks", out _) ? apphost : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>The launch line of a local server over stdio.</summary>
    /// <param name="dataDir">Its data directory (settings, state, logs).</param>
    /// <param name="belowNormal">Run the whole server tree (and the language servers it starts) below normal priority.</param>
    /// <param name="persist">R4: relay to the persistent server of this session id (started when it does not run), with its grace period in seconds.</param>
    public static ProcessLaunch Launch(string serverAssembly, string dataDir, string? marker = null, bool belowNormal = true,
        IReadOnlyDictionary<string, string?>? environment = null, (string Session, int GraceSeconds)? persist = null)
    {
        var apphost = SelfContainedHost(serverAssembly);
        List<string> args = apphost is null ? [serverAssembly, "--stdio", "--data-dir", dataDir] : ["--stdio", "--data-dir", dataDir];
        if (marker is not null) args.AddRange(["--marker", marker]);
        if (persist is { } p) args.AddRange(["--persist", p.Session, "--grace", p.GraceSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        return new ProcessLaunch(apphost ?? FindDotnetHost(), args) { BelowNormalPriority = belowNormal, Environment = environment };
    }
}
