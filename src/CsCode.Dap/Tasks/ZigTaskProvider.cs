using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CsCode.Core.Tasks;
using CsCode.Dap.Adapters;
using CsCode.Dap.Configuration;
using Microsoft.Extensions.Logging;

namespace CsCode.Dap.Tasks;

/// <summary>
/// Zig's detected tasks for a folder with a <c>build.zig</c> (docs/ZIG.md, "Tasks"): <c>"zig: build"</c> (<c>zig build</c>, the default step,
/// the default build task's group) and one <c>"zig: build &lt;step&gt;"</c> per other step of <c>zig build -l</c> (<c>run</c>, <c>test</c> in
/// the test group, <c>check</c> in the build group, the project's own), each with the <c>$zig</c> problem matcher.
/// <para><c>zig build -l</c> compiles the build script first: well under a second on a warm cache, close to a minute on a cold one. So the
/// listing runs in the background, bounded (<see cref="ListingTimeout"/>, <see cref="MaxSteps"/>, <see cref="MaxOutput"/>), never on the
/// caller's thread; the tasks are asked for wait for it at most <see cref="ListingWait"/>, and until it has answered (or when it fails: a
/// build.zig that does not compile) the folder gets <c>build</c>, <c>build run</c> and <c>build test</c>. The answer is cached per folder until
/// build.zig or build.zig.zon changes (or another zig is configured).</para>
/// <para>Definitions are <c>{"type": "zig"}</c> and <c>{"type": "zig", "step": "run"}</c>; a tasks.json entry may add <c>args</c> (after the
/// step: <c>"-Doptimize=ReleaseFast"</c>), <c>env</c> and <c>cwd</c>, and then runs as written.</para>
/// </summary>
internal sealed partial class ZigTaskProvider : ITaskProvider
{
    public const string TaskType = "zig";
    public const int MaxSteps = 200;
    public const int MaxOutput = 256 * 1024;

    private static readonly string[] FallbackSteps = ["run", "test"];

    private readonly DapPlatform _p;
    private readonly ILogger _logger;
    private readonly ZigToolchainLocator _zig;
    private readonly object _gate = new();
    private readonly Dictionary<string, Listing> _listings = new(StringComparer.Ordinal);

    public ZigTaskProvider(DapPlatform platform)
    {
        _p = platform;
        _logger = platform.Logger("CsCode.Dap.Tasks.Zig");
        _zig = new ZigToolchainLocator(name => Environment(name), key => _p.Configuration?.Get<string>(key), _logger);
    }

    /// <summary>Environment seam (tests must not mutate process-wide variables).</summary>
    internal Func<string, string?> Environment { get; init; } = System.Environment.GetEnvironmentVariable;

    /// <summary>Runs <c>zig build -l</c> in a folder and returns what it printed (both streams), or null when it failed. A seam for tests.</summary>
    internal Func<string, string, CancellationToken, Task<string?>> ListSteps { get; init; } = ListStepsAsync;

    /// <summary>How long the tasks wait for a listing that is still running before the fallback tasks are given.</summary>
    internal TimeSpan ListingWait { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>How long <c>zig build -l</c> may take (a cold cache compiles the standard library's build runner).</summary>
    internal static TimeSpan ListingTimeout { get; set; } = TimeSpan.FromMinutes(3);

    public string Type => TaskType;

    /// <summary>One folder's <c>zig build -l</c>: the files it depends on as they were, and its steps (null while it runs, or when it failed).</summary>
    private sealed record Listing(Stamp Stamp, string Zig, Task<IReadOnlyList<Step>?> Steps);

    internal sealed record Step(string Name, bool IsDefault, string? Description);

    public async ValueTask<IReadOnlyList<ProvidedTask>> ProvideTasksAsync(Uri folder, CancellationToken ct)
    {
        if (!folder.IsFile) return [];
        var root = DapPlatform.NormalizeDir(folder.LocalPath);
        if (!File.Exists(Path.Combine(root, "build.zig"))) return [];
        var zig = _zig.LocateExecutable();
        if (zig is null) return Tasks("zig", null);
        var steps = await Current(root, zig).WaitAsync(ct).ConfigureAwait(false);
        return Tasks(zig, steps);
    }

    /// <summary>
    /// The folder's steps: the cached listing's, or a new listing's when build.zig or build.zig.zon changed or another zig is used. The task
    /// returned ends within <see cref="ListingWait"/> (null: not listed yet); the listing goes on, bounded by <see cref="ListingTimeout"/>, and
    /// fills the cache. At most 32 folders are kept (a handful in practice).
    /// </summary>
    private Task<IReadOnlyList<Step>?> Current(string root, string zig)
    {
        Listing listing;
        lock (_gate)
        {
            if (!_listings.TryGetValue(root, out listing!) || listing.Zig != zig || !listing.Stamp.IsCurrent())
            {
                if (_listings.Count >= 32) _listings.Clear();
                listing = new Listing(Stamp.Of(root), zig, Task.Run(() => ListAsync(zig, root)));
                _listings[root] = listing;
            }
        }
        return listing.Steps.IsCompleted
            ? listing.Steps
            : listing.Steps.WaitAsync(ListingWait).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null, TaskScheduler.Default);
    }

    private async Task<IReadOnlyList<Step>?> ListAsync(string zig, string root)
    {
        using var bounded = new CancellationTokenSource(ListingTimeout);
        try
        {
            var text = await ListSteps(zig, root, bounded.Token).ConfigureAwait(false);
            if (text is null) return null;
            var steps = ParseSteps(text);
            return steps.Count == 0 ? null : steps;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("zig build -l took more than {Seconds} s in {Root}: its steps are not listed", ListingTimeout.TotalSeconds, root);
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            _logger.LogInformation(ex, "zig build -l failed in {Root}", root);
            return null;
        }
    }

    /// <summary><c>"zig: build"</c> then a task per other step (or <c>run</c> and <c>test</c> without a listing).</summary>
    private static IReadOnlyList<ProvidedTask> Tasks(string zig, IReadOnlyList<Step>? steps)
    {
        var tasks = new List<ProvidedTask> { Make(zig, null, [], steps?.FirstOrDefault(s => s.IsDefault)?.Description) };
        foreach (var step in steps ?? FallbackSteps.Select(s => new Step(s, false, null)).ToList())
            if (!step.IsDefault) tasks.Add(Make(zig, step.Name, [], step.Description));
        return tasks;
    }

    private static string? GroupOf(string? step) => step switch
    {
        null or "install" or "check" => "build",
        "test" => "test",
        _ => null,
    };

    private static ProvidedTask Make(string zig, string? step, IReadOnlyList<string> extra, string? description)
    {
        string[] args = ["build", .. step is null ? Array.Empty<string>() : [step], .. extra];
        var members = new Dictionary<string, object> { ["type"] = TaskType };
        if (step is not null) members["step"] = step;
        if (extra.Count > 0) members["args"] = extra;
        using var def = JsonDocument.Parse(JsonSerializer.Serialize(members));
        var commandLine = "zig " + string.Join(' ', args);
        return new ProvidedTask(TaskType, step is null ? "build" : "build " + step, zig, args)
        {
            Group = GroupOf(step),
            Detail = description is { Length: > 0 } ? $"{commandLine}: {description}" : commandLine,
            ProblemMatchers = ["$zig"],
            Definition = def.RootElement.Clone(),
        };
    }

    /// <summary>
    /// A tasks.json entry of type <c>zig</c>: <c>step</c> (none: the default step), <c>args</c> after it, <c>env</c> and <c>cwd</c> optional. One
    /// that names a detected task as it is gets that task; any other runs <c>zig build [step] [args…]</c> as written.
    /// </summary>
    public async ValueTask<ProvidedTask?> ResolveTaskAsync(Uri folder, JsonElement definition, CancellationToken ct)
    {
        if (definition.ValueKind != JsonValueKind.Object) return null;
        var step = definition.TryGetProperty("step", out var s) && s.ValueKind == JsonValueKind.String && s.GetString() is { Length: > 0 } named ? named : null;
        var args = definition.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];
        var env = definition.TryGetProperty("env", out var e) && e.ValueKind == JsonValueKind.Object
            ? e.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).ToDictionary(p => p.Name, p => p.Value.GetString()!)
            : null;
        var cwd = definition.TryGetProperty("cwd", out var w) && w.ValueKind == JsonValueKind.String && w.GetString() is { Length: > 0 } dir && folder.IsFile
            ? (Path.IsPathRooted(dir) ? dir : Path.Combine(folder.LocalPath, dir))
            : null;
        var detected = await ProvideTasksAsync(folder, ct).ConfigureAwait(false);
        var match = detected.FirstOrDefault(t => t.Args.SequenceEqual(step is null ? ["build"] : ["build", step]));
        if (match is not null && args.Count == 0 && env is null && cwd is null) return match;
        return Make(_zig.LocateExecutable() ?? "zig", step, args, null) with { Env = env, Cwd = cwd };
    }

    // ------------------------------------------------------------------ zig build -l

    /// <summary>
    /// The steps of <c>zig build -l</c>'s answer (two-space indented <c>name [(default)]  description</c> lines; zig 0.11 to 0.17 print them so),
    /// at most <see cref="MaxSteps"/>.
    /// </summary>
    internal static IReadOnlyList<Step> ParseSteps(string text)
    {
        var steps = new List<Step>();
        foreach (var line in text.Split('\n'))
        {
            var m = StepLine().Match(line.TrimEnd('\r'));
            if (!m.Success) continue;
            steps.Add(new Step(m.Groups["name"].Value, m.Groups["default"].Success, m.Groups["description"].Value is { Length: > 0 } d ? d : null));
            if (steps.Count >= MaxSteps) break;
        }
        return steps;
    }

    private static async Task<string?> ListStepsAsync(string zig, string root, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(zig)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = root,
        };
        foreach (var arg in new[] { "build", "-l", "--color", "off" }) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi);
        if (process is null) return null;
        try { process.StandardInput.Close(); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
        // zig prints the steps on stderr (0.17), older versions on stdout: both, each bounded
        var stdout = ReadBoundedAsync(process.StandardOutput);
        var stderr = ReadBoundedAsync(process.StandardError);
        try { await process.WaitForExitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        var text = await stdout.ConfigureAwait(false) + "\n" + await stderr.ConfigureAwait(false);
        return process.ExitCode == 0 ? text : null;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var sb = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            if (sb.Length < MaxOutput) sb.Append(buffer, 0, Math.Min(read, MaxOutput - sb.Length)); // read on to the end: the process must not block
        return sb.ToString();
    }

    /// <summary>build.zig's and build.zig.zon's last-write times when the listing started: any change lists the steps again.</summary>
    private sealed record Stamp(IReadOnlyList<(string Path, DateTime Written)> Files)
    {
        public static Stamp Of(string root) => new([.. new[] { "build.zig", "build.zig.zon" }.Select(f => Path.Combine(root, f)).Select(p => (p, WrittenAt(p)))]);

        public bool IsCurrent() => Files.All(f => WrittenAt(f.Path) == f.Written);

        private static DateTime WrittenAt(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
    }

    [GeneratedRegex(@"^  (?<name>\S+)(?<default> \(default\))?(?:\s{2,}(?<description>.*?))?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex StepLine();
}
