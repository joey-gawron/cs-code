using CsCode.Core.Platform;

namespace CsCode.ProjectTemplates.Engine;

/// <summary>How to install a tool that is not there, per platform, in the style of the language servers' hints. CsCode never installs it.</summary>
public sealed record ToolInstallHint(string Linux, string MacOS, string Windows)
{
    public static ToolInstallHint Everywhere(string text) => new(text, text, text);

    public string ForCurrentPlatform => OperatingSystem.IsWindows() ? Windows : OperatingSystem.IsMacOS() ? MacOS : Linux;
}

/// <summary>The tools the generators run and their install hints.</summary>
public static class Tools
{
    public static readonly ToolInstallHint Python = new(
        "Install it with: sudo apt install python3 python3-venv",
        "Install it with: brew install python",
        "Install it with: winget install Python.Python.3.13");

    public static readonly ToolInstallHint Cargo = new(
        "Install Rust with rustup: curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh (https://rustup.rs)",
        "Install Rust with rustup: curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh (https://rustup.rs), or: brew install rustup",
        "Install it with: winget install Rustlang.Rustup");

    public static readonly ToolInstallHint Dotnet = new(
        "Install the .NET SDK: sudo apt install dotnet-sdk-10.0, or the script from https://dot.net/v1/dotnet-install.sh",
        "Install it with: brew install --cask dotnet-sdk (or the installer from https://dot.net)",
        "Install it with: winget install Microsoft.DotNet.SDK.10");

    public static readonly ToolInstallHint Zig = new(
        "Download Zig from https://ziglang.org/download/ and put it on PATH (or: sudo snap install zig --classic --beta)",
        "Install it with: brew install zig",
        "Install it with: winget install -e --id zig.zig");

    public static readonly ToolInstallHint CMake = new(
        "Install it with: sudo apt install cmake",
        "Install it with: brew install cmake",
        "Install it with: winget install Kitware.CMake");

    public static readonly ToolInstallHint CCompiler = new(
        "Install it with: sudo apt install build-essential (or: sudo apt install clang)",
        "Install Apple's command line tools: xcode-select --install",
        "Install it with: winget install LLVM.LLVM (or the Visual Studio Build Tools' C++ workload)");

    public static readonly ToolInstallHint Node = new(
        "Install Node.js (it includes npm): sudo apt install nodejs npm, or nvm (https://github.com/nvm-sh/nvm)",
        "Install it with: brew install node",
        "Install it with: winget install OpenJS.NodeJS.LTS");

    public static readonly ToolInstallHint Git = new(
        "Install it with: sudo apt install git",
        "Install Apple's command line tools (xcode-select --install), or: brew install git",
        "Install it with: winget install Git.Git");
}

/// <summary>
/// Finds a tool's executable: on PATH (which, on a Mac app started from the Dock, is the login shell's once <see cref="LoginShellEnvironment"/>
/// has merged it), then in the usual per-user install directories (<see cref="LoginShellEnvironment.FallbackDirectories"/>, plus <c>~/.dotnet</c>
/// for the SDK installer's dotnet), with Windows' executable extensions.
/// </summary>
public static class ToolLocator
{
    /// <summary>The first of <paramref name="names"/> found, as a full path; null when none is installed.</summary>
    public static string? Find(params string[] names)
    {
        foreach (var name in names)
            if (Find(name, Environment.GetEnvironmentVariable("PATH"), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), File.Exists) is { } found)
                return found;
        return null;
    }

    /// <summary><see cref="Find(string[])"/> with its inputs given (tests).</summary>
    public static string? Find(string name, string? path, string home, Func<string, bool> fileExists)
    {
        var exts = OperatingSystem.IsWindows() ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';') : [""];
        var dirs = (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).AsEnumerable();
        if (home.Length > 0)
            dirs = dirs.Concat(LoginShellEnvironment.FallbackDirectories.Select(d => d.StartsWith("~/", StringComparison.Ordinal) ? home + d[1..] : d))
                .Append(Path.Combine(home, ".dotnet"));
        foreach (var dir in dirs)
            foreach (var ext in exts)
            {
                var candidate = Path.Combine(dir, name + ext);
                if (fileExists(candidate)) return candidate;
            }
        return null;
    }

    /// <summary>
    /// The dotnet host: <c>DOTNET_ROOT</c> (the SDK CsCode was pointed at) first, then <see cref="Find(string[])"/>, then the system install
    /// directories.
    /// </summary>
    public static string? FindDotnet()
    {
        var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        foreach (var variable in new[] { InheritedDotnetRoot.ArchRootVariable, InheritedDotnetRoot.RootVariable })
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } root && File.Exists(Path.Combine(root, exe))) return Path.Combine(root, exe);
        if (Find("dotnet") is { } found) return found;
        foreach (var dir in new[] { "/usr/local/share/dotnet", "/usr/share/dotnet", "/usr/lib/dotnet" })
            if (File.Exists(Path.Combine(dir, exe))) return Path.Combine(dir, exe);
        return null;
    }

    /// <summary>
    /// The first line of <c>tool --version</c> (or the given arguments) that has a digit in it ("cargo 1.96.1 (…)", "cmake version 4.4.2"); null when
    /// the tool did not run.
    /// </summary>
    public static async Task<string?> VersionAsync(IToolRunner runner, string tool, CancellationToken ct, params string[] args)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        ToolResult result;
        try { result = await runner.RunAsync(new ToolRun(tool, args.Length > 0 ? args : ["--version"], Path.GetTempPath()), null, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; } // hung: treated as not working
        if (!result.Succeeded) return null;
        return result.Output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Any(char.IsDigit)) ?? result.Output.Trim();
    }
}
