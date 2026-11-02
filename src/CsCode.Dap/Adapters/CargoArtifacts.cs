using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CsCode.Core.Debugging;
using CsCode.Core.Platform;
using CsCode.Dap.Configuration;
using CsCode.Dap.Tasks;

namespace CsCode.Dap.Adapters;

/// <summary>
/// CodeLLDB's <c>"cargo"</c> launch key for the GDB adapter: <c>{"args": ["test", "--no-run"], "filter": {"name": "…", "kind": "lib|bin|test|…"},
/// "cwd": "…", "env": {…}}</c>. cargo runs with <c>--message-format=json-render-diagnostics</c> (its progress and compiler messages go to the
/// Tasks output channel, as a preLaunchTask's do), and the executable it reports — after the filter — becomes the launch's <c>program</c>. That
/// is how a test binary is found: cargo names it <c>target/debug/deps/&lt;crate&gt;-&lt;hash&gt;</c> (wherever the target directory is).
/// <c>test</c>/<c>bench</c> get <c>--no-run</c> when missing. The cargo process is tracked and killed with its tree when the start is cancelled.
/// </summary>
internal static class CargoArtifacts
{
    public sealed record Artifact(string Name, IReadOnlyList<string> Kinds, string Executable, bool IsTest);

    /// <summary>The program to debug, or null (cargo failed, nothing matched, the choice was cancelled — each reported to the user).</summary>
    public static async Task<string?> ResolveProgramAsync(DapPlatform p, Uri? folder, JsonElement cargo, CancellationToken ct)
    {
        var args = cargo.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];
        if (args.Count == 0) { await ReportAsync(p, "The \"cargo\" launch setting needs \"args\", for example [\"test\", \"--no-run\"]."); return null; }
        var subcommand = args.FirstOrDefault(x => !x.StartsWith('-'));
        if (subcommand is "test" or "t" or "bench" && !args.Contains("--no-run")) args.Add("--no-run");
        if (args.Any(x => x.StartsWith("--message-format", StringComparison.Ordinal)))
        {
            await ReportAsync(p, "The \"cargo\" launch setting chooses --message-format itself: remove it from \"args\".");
            return null;
        }
        args.Add("--message-format=json-render-diagnostics");

        var folderPath = folder is { IsFile: true } f ? DapPlatform.NormalizeDir(f.LocalPath) : null;
        var cwd = cargo.TryGetProperty("cwd", out var c) && c.ValueKind == JsonValueKind.String && c.GetString() is { Length: > 0 } dir
            ? (Path.IsPathRooted(dir) || folderPath is null ? dir : Path.Combine(folderPath, dir))
            : folderPath;
        var cargoExe = FindCargo();
        if (cargoExe is null) { await ReportAsync(p, "cargo was not found on PATH: the \"cargo\" launch setting cannot build the program to debug."); return null; }

        var channel = p.Output?.GetOrCreate(TaskService.ChannelName);
        channel?.AppendLine($"> Executing: cargo {string.Join(' ', args)} <");
        var (exitCode, stdout) = await RunAsync(p, cargoExe, args, cwd, cargo, channel, ct);
        if (exitCode is null) return null; // cancelled
        channel?.AppendLine($"> cargo finished with exit code {exitCode}.");
        if (exitCode != 0)
        {
            await ReportAsync(p, $"cargo {string.Join(' ', args.Take(args.Count - 1))} failed with exit code {exitCode}: see the Tasks output.");
            return null;
        }

        var artifacts = Parse(stdout);
        var filter = cargo.TryGetProperty("filter", out var fl) && fl.ValueKind == JsonValueKind.Object ? fl : default;
        string? Str(string name) => filter.ValueKind == JsonValueKind.Object && filter.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var name = Str("name");
        var kind = Str("kind");
        var matches = artifacts.Where(x => (name is null || x.Name == name) && (kind is null || x.Kinds.Contains(kind))).ToList();
        if (matches.Count == 1) return matches[0].Executable;
        var what = $"cargo {string.Join(' ', args.Take(args.Count - 1))}" + (name is not null || kind is not null ? $" (filter: name {name ?? "any"}, kind {kind ?? "any"})" : "");
        if (matches.Count == 0)
        {
            await ReportAsync(p, $"{what} produced no executable to debug" + (artifacts.Count > 0 ? ": it built " + string.Join(", ", artifacts.Select(x => $"{x.Name} [{string.Join('/', x.Kinds)}]")) + "." : "."));
            return null;
        }
        if (p.Window is not { } window)
        {
            await ReportAsync(p, $"{what} produced {matches.Count} executables; add a \"filter\" ({{\"name\": …, \"kind\": …}}) to choose one.");
            return null;
        }
        var picked = await window.ShowQuickPickAsync(
            [.. matches.Select(m => new QuickPickItem(m.Name, string.Join('/', m.Kinds) + (m.IsTest ? " (tests)" : ""), m.Executable, Tag: m.Executable))],
            new QuickPickOptions(Title: "Executable to debug", Placeholder: $"{what} built several executables"), ct);
        return picked?.Tag as string;
    }

    /// <summary>The <c>compiler-artifact</c> messages that carry an executable.</summary>
    internal static List<Artifact> Parse(string stdout)
    {
        var result = new List<Artifact>();
        foreach (var line in stdout.Split('\n'))
        {
            var text = line.Trim();
            if (!text.StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (!root.TryGetProperty("reason", out var reason) || reason.GetString() != "compiler-artifact") continue;
                if (!root.TryGetProperty("executable", out var exe) || exe.ValueKind != JsonValueKind.String) continue;
                var target = root.GetProperty("target");
                var kinds = target.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.Array ? k.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : [];
                var isTest = root.TryGetProperty("profile", out var profile) && profile.TryGetProperty("test", out var t) && t.ValueKind == JsonValueKind.True;
                result.Add(new Artifact(target.GetProperty("name").GetString() ?? "", kinds, exe.GetString()!, isTest));
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { /* not a cargo message */ }
        }
        return result;
    }

    internal static string? FindCargo()
    {
        var name = OperatingSystem.IsWindows() ? "cargo.exe" : "cargo";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>Runs cargo: stderr (progress, compiler messages) to the channel line by line, stdout (JSON) collected. Null exit code: cancelled.</summary>
    private static async Task<(int? ExitCode, string Stdout)> RunAsync(DapPlatform p, string cargo, List<string> args, string? cwd, JsonElement settings, IOutputChannel? channel, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(cargo)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (cwd is not null && Directory.Exists(cwd)) psi.WorkingDirectory = cwd;
        if (settings.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object)
            foreach (var e in env.EnumerateObject())
            {
                if (e.Value.ValueKind == JsonValueKind.Null) psi.Environment.Remove(e.Name);
                else psi.Environment[e.Name] = e.Value.ValueKind == JsonValueKind.String ? e.Value.GetString() : e.Value.GetRawText();
            }
        psi.Environment["CARGO_TERM_COLOR"] = "never";

        Process process;
        try { process = await Task.Run(() => Process.Start(psi) ?? throw new InvalidOperationException("cargo did not start"), ct); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            await ReportAsync(p, $"cargo could not be started: {ex.Message}");
            return (-1, "");
        }
        using var owned = process;
        using var tracked = p.Memory?.TrackChildProcess("cargo (debug launch)", process.Id);
        try { process.StandardInput.Close(); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line) channel?.AppendLine(line);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        }, CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(ct);
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            channel?.AppendLine("> cargo was cancelled.");
            return (null, "");
        }
        catch (TimeoutException) { /* a grandchild keeps the pipes open: cargo itself has finished */ }
        return (process.ExitCode, stdout.IsCompletedSuccessfully ? stdout.Result : "");
    }

    private static Task ReportAsync(DapPlatform p, string message) =>
        p.Window is { } w ? w.ShowMessageAsync(MessageSeverity.Error, message, null, CancellationToken.None).AsTask() : Task.CompletedTask;
}
