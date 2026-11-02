using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CsCode.Core.Tasks;
using CsCode.Dap.Adapters;
using CsCode.Dap.Configuration;
using Microsoft.Extensions.Logging;

namespace CsCode.Dap.Tasks;

/// <summary>
/// Cargo's detected tasks (rust-analyzer's VS Code extension contributes the same ones): <c>build</c>, <c>check</c>, <c>clippy</c>, <c>test</c>,
/// <c>run</c> and <c>doc</c> for the folder's workspace (<c>"cargo: build"</c>), and per package when the workspace has several
/// (<c>"cargo: build - core"</c>, <c>cargo build -p core</c>), plus <c>"cargo: run - &lt;bin&gt;"</c> for each binary when there are several. Each
/// uses the <c>$rustc</c> problem matcher. A folder is a cargo workspace when it holds a <c>Cargo.toml</c>; its packages come from
/// <c>cargo metadata --no-deps</c> (no network, no build), cached until a manifest it read changes. The definitions are rust-analyzer's
/// (<c>{"type": "cargo", "command": "build", "args": ["-p", "core"]}</c>), so a tasks.json written for VS Code customises them, and an entry
/// with other <c>args</c> or an <c>env</c> runs as written.
/// </summary>
internal sealed class CargoTaskProvider(DapPlatform platform) : ITaskProvider
{
    public const string TaskType = "cargo";

    /// <summary>The subcommands and their groups (rust-analyzer's: build, check and clippy build; test tests).</summary>
    internal static readonly (string Command, string? Group)[] Subcommands =
        [("build", "build"), ("check", "build"), ("clippy", "build"), ("test", "test"), ("run", null), ("doc", null)];

    private readonly ILogger _logger = platform.Logger("CsCode.Dap.Tasks.Cargo");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (ManifestStamp Stamp, IReadOnlyList<ProvidedTask> Tasks)> _cache = new(StringComparer.Ordinal);

    /// <summary>Where <c>cargo</c> is (tests point it elsewhere); null: looked up on PATH.</summary>
    internal Func<string?> FindCargo { get; init; } = CargoArtifacts.FindCargo;

    public string Type => TaskType;

    public async ValueTask<IReadOnlyList<ProvidedTask>> ProvideTasksAsync(Uri folder, CancellationToken ct)
    {
        if (!folder.IsFile) return [];
        var root = DapPlatform.NormalizeDir(folder.LocalPath);
        if (!File.Exists(Path.Combine(root, "Cargo.toml"))) return [];
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(root, out var known) && known.Stamp.IsCurrent()) return known.Tasks;
            var (tasks, manifests) = await DetectAsync(root, ct).ConfigureAwait(false);
            if (_cache.Count >= 32) _cache.Clear(); // bounded: a handful of folders in practice
            _cache[root] = (ManifestStamp.Of(manifests.Append(Path.Combine(root, "Cargo.toml"))), tasks);
            return tasks;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// A tasks.json entry: one written for rust-analyzer (<c>command</c> is the cargo subcommand, <c>args</c> come after it, <c>env</c> and
    /// <c>cwd</c> optional) runs <c>cargo &lt;command&gt; &lt;args…&gt;</c>, whether or not it is one of the detected tasks.
    /// </summary>
    public async ValueTask<ProvidedTask?> ResolveTaskAsync(Uri folder, JsonElement definition, CancellationToken ct)
    {
        if (definition.ValueKind != JsonValueKind.Object || !definition.TryGetProperty("command", out var c) || c.ValueKind != JsonValueKind.String
            || c.GetString() is not { Length: > 0 } command)
            return null;
        var args = definition.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];
        var detected = await ProvideTasksAsync(folder, ct).ConfigureAwait(false);
        var match = detected.FirstOrDefault(t => t.Definition is { } d && TaskDefinitionMatch.Matches(d, definition) && SameArgs(d, args));
        var env = definition.TryGetProperty("env", out var e) && e.ValueKind == JsonValueKind.Object
            ? e.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).ToDictionary(p => p.Name, p => p.Value.GetString()!)
            : null;
        var cwd = definition.TryGetProperty("cwd", out var w) && w.ValueKind == JsonValueKind.String && w.GetString() is { Length: > 0 } dir && folder.IsFile
            ? (Path.IsPathRooted(dir) ? dir : Path.Combine(folder.LocalPath, dir))
            : null;
        if (match is not null && env is null && cwd is null) return match;
        return Make(command, match?.Name ?? string.Join(' ', new[] { command }.Concat(args)), [command, .. args], match?.Group ?? GroupOf(command)) with
        {
            Env = env,
            Cwd = cwd,
        };
    }

    private static bool SameArgs(JsonElement definition, List<string> args)
    {
        var mine = definition.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Select(x => x.GetString()).ToList() : [];
        return mine.SequenceEqual(args);
    }

    private static string? GroupOf(string command) => Subcommands.FirstOrDefault(s => s.Command == command).Group;

    private ProvidedTask Make(string command, string name, IReadOnlyList<string> args, string? group)
    {
        var cargo = FindCargo() ?? "cargo";
        var members = new Dictionary<string, object> { ["type"] = TaskType, ["command"] = command };
        if (args.Count > 1) members["args"] = args.Skip(1).ToArray(); // rust-analyzer's tasks.json entries write no args for the plain command
        using var def = JsonDocument.Parse(JsonSerializer.Serialize(members));
        return new ProvidedTask(TaskType, name, cargo, args)
        {
            Group = group,
            Detail = "cargo " + string.Join(' ', args),
            ProblemMatchers = ["$rustc"],
            Definition = def.RootElement.Clone(),
        };
    }

    // ------------------------------------------------------------------ detection

    internal sealed record Package(string Name, string ManifestPath, IReadOnlyList<string> Binaries);

    private async Task<(IReadOnlyList<ProvidedTask> Tasks, IReadOnlyList<string> Manifests)> DetectAsync(string root, CancellationToken ct)
    {
        var packages = await MetadataAsync(root, ct).ConfigureAwait(false);
        var tasks = new List<ProvidedTask>();
        foreach (var (command, group) in Subcommands) tasks.Add(Make(command, command, [command], group));
        if (packages is null) return (tasks, []); // no cargo, or a manifest cargo rejects: the workspace-wide tasks still say what is wrong when run

        if (packages.Count > 1)
            foreach (var package in packages)
                foreach (var (command, group) in Subcommands)
                    tasks.Add(Make(command, $"{command} - {package.Name}", [command, "-p", package.Name], group));
        var binaries = packages.SelectMany(p => p.Binaries.Select(b => (Package: p.Name, Binary: b))).ToList();
        if (binaries.Count > 1)
            foreach (var (package, binary) in binaries)
                tasks.Add(Make("run", $"run - {binary}", packages.Count > 1 ? ["run", "-p", package, "--bin", binary] : ["run", "--bin", binary], null));
        return (tasks, packages.Select(p => p.ManifestPath).ToList());
    }

    /// <summary>The workspace's packages from <c>cargo metadata --no-deps</c>; null when cargo is missing or fails (logged).</summary>
    private async Task<IReadOnlyList<Package>?> MetadataAsync(string root, CancellationToken ct)
    {
        if (FindCargo() is not { } cargo)
        {
            _logger.LogInformation("cargo is not on PATH: only the workspace-wide cargo tasks are listed");
            return null;
        }
        var psi = new ProcessStartInfo(cargo)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = root,
        };
        foreach (var arg in new[] { "metadata", "--no-deps", "--format-version", "1" }) psi.ArgumentList.Add(arg);
        psi.Environment["CARGO_TERM_COLOR"] = "never";
        try
        {
            using var process = Process.Start(psi);
            if (process is null) return null;
            using var tracked = platform.Memory?.TrackChildProcess("cargo metadata (tasks)", process.Id);
            try { process.StandardInput.Close(); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            try { await process.WaitForExitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw;
            }
            if (process.ExitCode != 0)
            {
                _logger.LogInformation("cargo metadata failed in {Root}: {Error}", root, (await stderr.ConfigureAwait(false)).Trim());
                return null;
            }
            return ParseMetadata(await stdout.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or JsonException)
        {
            _logger.LogInformation(ex, "cargo metadata failed in {Root}", root);
            return null;
        }
    }

    /// <summary>The workspace members of <c>cargo metadata</c>'s answer, in its order, with their binary targets.</summary>
    internal static IReadOnlyList<Package> ParseMetadata(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var members = root.TryGetProperty("workspace_members", out var m) && m.ValueKind == JsonValueKind.Array
            ? m.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToHashSet(StringComparer.Ordinal)
            : null;
        var result = new List<Package>();
        foreach (var package in root.GetProperty("packages").EnumerateArray())
        {
            var id = package.TryGetProperty("id", out var i) ? i.GetString() : null;
            if (members is not null && (id is null || !members.Contains(id))) continue;
            var binaries = package.TryGetProperty("targets", out var targets) && targets.ValueKind == JsonValueKind.Array
                ? targets.EnumerateArray()
                    .Where(t => t.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.Array && k.EnumerateArray().Any(x => x.GetString() == "bin"))
                    .Select(t => t.GetProperty("name").GetString()!).ToList()
                : [];
            result.Add(new Package(package.GetProperty("name").GetString()!, package.GetProperty("manifest_path").GetString()!, binaries));
        }
        return result;
    }

    /// <summary>The manifests' last-write times when the tasks were detected: any change (or a manifest appearing or going) detects them again.</summary>
    private sealed record ManifestStamp(IReadOnlyList<(string Path, DateTime Written)> Files)
    {
        public static ManifestStamp Of(IEnumerable<string> paths) =>
            new([.. paths.Distinct(StringComparer.Ordinal).Select(p => (p, WrittenAt(p)))]);

        public bool IsCurrent() => Files.All(f => WrittenAt(f.Path) == f.Written);

        private static DateTime WrittenAt(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
    }
}
