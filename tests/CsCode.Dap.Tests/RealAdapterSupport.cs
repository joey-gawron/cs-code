using System.Diagnostics;
using CsCode.Dap.Adapters;
using CsCode.TestDoubles;

namespace CsCode.Dap.Tests;

/// <summary>
/// Locates the real netcoredbg (fetched by <c>tools/fetch-netcoredbg.sh</c> into <c>.scratch/tools/netcoredbg/</c>) and builds the tiny C#
/// sample the real-adapter tests debug. Tests that need either SKIP with an explicit reason when it is missing.
/// </summary>
public static class RealAdapterSupport
{
    public const string Category = "RealAdapter";

    /// <summary>The sample program. Marker comments (<c>// @name</c>) give tests stable line numbers without hard-coding them.</summary>
    public const string ProgramSource = """
        using System;

        namespace Sample;

        public static class Program
        {
            public static int Add(int a, int b)
            {
                var sum = a + b; // @add
                return sum;
            }

            public static int Outer(int n)
            {
                var inner = Add(n, 1); // @outer
                return inner * 2;
            }

            public static int Main()
            {
                int x = 41; // @start
                string s = "hello";
                for (int i = 0; i < 5; i++)
                {
                    x += i; // @loop
                    Console.WriteLine($"loop {i}"); // @print
                }
                var r = Outer(x); // @call
                try { Throw(); } catch (InvalidOperationException) { } // @throw
                Console.WriteLine($"done {s} {r}"); // @done
                return 7;
            }

            static void Throw() => throw new InvalidOperationException("boom");
        }
        """;

    public const string ProjectFile = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
            <UseAppHost>false</UseAppHost>
            <Optimize>false</Optimize>
            <DebugType>portable</DebugType>
            <ImplicitUsings>disable</ImplicitUsings>
            <Nullable>enable</Nullable>
            <InvariantGlobalization>true</InvariantGlobalization>
            <SatelliteResourceLanguages>en</SatelliteResourceLanguages>
            <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
          </PropertyGroup>
        </Project>
        """;

    /// <summary>1-based source line of a <c>// @marker</c>.</summary>
    public static int LineOf(string marker)
    {
        var lines = ProgramSource.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].Contains("// @" + marker, StringComparison.Ordinal)) return i + 1;
        throw new ArgumentException($"No marker @{marker} in the sample");
    }

    /// <summary>Path of netcoredbg if present (repo-local tools dir, then CSCODE_NETCOREDBG), else null.</summary>
    public static string? FindNetCoreDbg()
    {
        var exe = NetCoreDbgAdapterFactory.ExecutableName;
        if (Environment.GetEnvironmentVariable("CSCODE_NETCOREDBG") is { Length: > 0 } fromEnv && File.Exists(fromEnv)) return fromEnv;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, ".scratch", "tools", "netcoredbg", exe);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static string? FindDotnet()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary><paramref name="path"/> with every symbolic link along it resolved (macOS's /var and /tmp are links into /private).</summary>
    public static string PhysicalPath(string path)
    {
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true) is { } target) current = target.FullName;
        }
        return current;
    }

    public sealed record SampleBuild(string Directory, string Dll, string SourcePath);

    private static readonly SemaphoreSlim BuildGate = new(1, 1);
    private static SampleBuild? _built;
    private static TempDirectory? _dir;

    /// <summary>Writes and builds the sample once per test run (Debug, portable PDBs). Throws with the build output if it fails.</summary>
    public static async Task<SampleBuild> BuildSampleAsync(CancellationToken ct)
    {
        await BuildGate.WaitAsync(ct);
        try
        {
            if (_built is not null) return _built;
            var dotnet = FindDotnet() ?? throw new InvalidOperationException("dotnet not found on PATH");
            _dir = new TempDirectory("dapreal-");
            _dir.WriteFile("Sample.csproj", ProjectFile);
            _dir.WriteFile("Program.cs", ProgramSource);
            // The compiler records the build's physical directory in the PDB, and netcoredbg binds a breakpoint only to that exact path: on a
            // Mac the temp dir (/var/folders/...) is behind the /var -> /private/var link, so the tests use the physical path throughout.
            var dir = PhysicalPath(_dir.Path);
            var source = Path.Combine(dir, "Program.cs");
            var psi = new ProcessStartInfo(dotnet)
            {
                WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            foreach (var a in new[] { "build", "Sample.csproj", "-c", "Debug", "-o", "out", "--nologo", "-v", "q" }) psi.ArgumentList.Add(a);
            psi.Environment["DOTNET_NOLOGO"] = "1";
            psi.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
            psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync(ct);
            var stderr = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0) throw new InvalidOperationException($"dotnet build of the sample failed ({p.ExitCode}):\n{await stdout}\n{await stderr}");
            _built = new SampleBuild(dir, Path.Combine(dir, "out", "Sample.dll"), source);
            return _built;
        }
        finally { BuildGate.Release(); }
    }
}
