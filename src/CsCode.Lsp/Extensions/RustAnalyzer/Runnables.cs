using System.Text.Json;
using System.Text.Json.Nodes;
using CsCode.Core.Debugging;
using CsCode.Core.Platform;
using CsCode.Core.Tasks;
using CsCode.Core.Text;

namespace CsCode.Lsp.Extensions.RustAnalyzer;

/// <summary>
/// A rust-analyzer runnable (<c>experimental/runnables</c>, and the argument of its Run / Debug lenses and hover actions) of kind <c>cargo</c>:
/// <c>cargo &lt;cargoArgs&gt; -- &lt;executableArgs&gt;</c> in <c>cwd</c> with <c>environment</c>.
/// </summary>
internal sealed record Runnable(string Label, IReadOnlyList<string> CargoArgs, IReadOnlyList<string> ExecutableArgs, string? Cwd, string? WorkspaceRoot,
    IReadOnlyDictionary<string, string> Environment)
{
    public string? Subcommand => CargoArgs.Count > 0 ? CargoArgs[0] : null;
    /// <summary>A binary or example (<c>cargo run</c>): the lenses CsCode keeps.</summary>
    public bool IsRun => Subcommand == "run";
    public bool IsTest => Subcommand is "test" or "bench";

    public string CommandLine => "cargo " + string.Join(' ', CargoArgs) + (ExecutableArgs.Count > 0 ? " -- " + string.Join(' ', ExecutableArgs) : "");

    public static Runnable? From(IReadOnlyList<JsonElement>? arguments) => arguments is { Count: > 0 } ? Parse(arguments[0]) : null;

    public static Runnable? Parse(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || e.Str("kind") is { } kind && kind != "cargo" || !e.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Object)
            return null;
        static List<string> Strings(JsonElement o, string name) =>
            o.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? [.. a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)] : [];
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (args.TryGetProperty("environment", out var en) && en.ValueKind == JsonValueKind.Object)
            foreach (var p in en.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) env[p.Name] = p.Value.GetString()!;
        var cargoArgs = Strings(args, "cargoArgs");
        // rust-analyzer before 2024 split the extra arguments of the cargo command into cargoExtraArgs
        cargoArgs.AddRange(Strings(args, "cargoExtraArgs"));
        return new Runnable(e.Str("label") ?? "cargo " + string.Join(' ', cargoArgs), cargoArgs, Strings(args, "executableArgs"), args.Str("cwd"), args.Str("workspaceRoot"), env);
    }

    /// <summary>The value after <paramref name="flag"/> in the cargo arguments (<c>--bin app</c>), if any.</summary>
    public string? ValueOf(string flag)
    {
        for (var i = 0; i + 1 < CargoArgs.Count; i++)
            if (CargoArgs[i] == flag) return CargoArgs[i + 1];
        return null;
    }
}

public sealed partial class RustAnalyzerExtension
{
    /// <summary><c>rust-analyzer.runSingle</c> / <c>debugSingle</c>: the runnable in the command's first argument.</summary>
    private async ValueTask RunSingleAsync(CommandInvocation inv, bool debug)
    {
        var arg = inv.Args.Count > 0 ? inv.Args[0] : null;
        var element = arg as JsonElement? ?? (arg is null ? null : JsonSerializer.SerializeToElement(arg));
        if (element is not { } e || Runnable.Parse(e) is not { } runnable)
        {
            Status("This runnable is not a cargo command CsCode can run");
            return;
        }
        if (debug) await DebugAsync(runnable).ConfigureAwait(false);
        else await RunAsync(runnable).ConfigureAwait(false);
    }

    /// <summary>"Rust: Run…" / "Rust: Debug…": the runnables at the caret's file (those around the caret first), picked from a list.</summary>
    private async ValueTask PickRunnableAsync(bool debug)
    {
        var what = debug ? "Debug" : "Run";
        if (Active(what) is not { } a || Services.Window is not { } window) return;
        var answer = await a.Session.RequestAsync("experimental/runnables", a.Document,
            (id, m) => new { textDocument = id, position = m.ToLsp(a.Caret, a.Document.Uri) }, CancellationToken.None, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        var runnables = answer is { ValueKind: JsonValueKind.Array } arr ? arr.EnumerateArray().Select(Runnable.Parse).OfType<Runnable>().ToList() : [];
        if (runnables.Count == 0) { Status("rust-analyzer found nothing to run here"); return; }
        var picked = await window.ShowQuickPickAsync([.. runnables.Select(r => new QuickPickItem(r.Label, r.CommandLine, Tag: r))],
            new QuickPickOptions(Title: debug ? "Debug" : "Run", Placeholder: "Select the runnable", MatchOnDescription: true)).ConfigureAwait(false);
        if (picked?.Tag is not Runnable chosen) return;
        if (debug) await DebugAsync(chosen).ConfigureAwait(false);
        else await RunAsync(chosen).ConfigureAwait(false);
    }

    /// <summary>Runs the cargo command in a task terminal named after the runnable (rust-analyzer's VS Code extension runs it as a cargo task).</summary>
    private async ValueTask RunAsync(Runnable runnable)
    {
        if (Services.TaskTerminals?.Invoke() is not { } terminals)
        {
            if (Services.Window is { } w) await w.ShowMessageAsync(MessageSeverity.Error, $"'{runnable.Label}' runs in a terminal, and no terminal is available.").ConfigureAwait(false);
            return;
        }
        var args = new List<string>(runnable.CargoArgs);
        if (runnable.ExecutableArgs.Count > 0) args.AddRange(["--", .. runnable.ExecutableArgs]);
        var env = runnable.Environment.ToDictionary(kv => kv.Key, kv => (string?)kv.Value, StringComparer.Ordinal);
        try
        {
            await terminals.StartAsync(new TaskTerminalRequest("cargo " + runnable.Label, Cargo(), args, runnable.Cwd ?? runnable.WorkspaceRoot, env, new TaskPresentation())
            {
                CommandLine = runnable.CommandLine,
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException)
        {
            if (Services.Window is { } w) await w.ShowMessageAsync(MessageSeverity.Error, $"'{runnable.Label}' could not be started: {ex.Message}").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Debugs the runnable: cargo builds it first (the <c>cargo</c> launch key: <c>cargo run</c> becomes <c>cargo build</c>, <c>cargo test</c> gets
    /// <c>--no-run</c>) and the executable it reports is launched with the runnable's arguments, under the Rust debugger
    /// (<c>cscode.debug.rustDebugger</c>: CodeLLDB on macOS).
    /// </summary>
    private async ValueTask DebugAsync(Runnable runnable)
    {
        if (Services.Debug?.Invoke() is not { } debug)
        {
            Status("The debugger is not available");
            return;
        }
        var cargoArgs = new List<string>(runnable.CargoArgs);
        if (runnable.IsRun) cargoArgs[0] = "build";
        var cargo = new JsonObject { ["args"] = new JsonArray([.. cargoArgs.Select(a => (JsonNode)JsonValue.Create(a))]) };
        if (runnable.ValueOf("--bin") is { } bin) cargo["filter"] = new JsonObject { ["name"] = bin, ["kind"] = "bin" };
        else if (runnable.ValueOf("--example") is { } example) cargo["filter"] = new JsonObject { ["name"] = example, ["kind"] = "example" };
        if (runnable.Cwd is { } cwd) cargo["cwd"] = cwd;
        var env = new JsonObject();
        foreach (var (k, v) in runnable.Environment) env[k] = v;
        if (env.Count > 0) cargo["env"] = env.DeepClone();

        var launch = new JsonObject
        {
            ["type"] = RustDebugType(),
            ["request"] = "launch",
            ["name"] = "Debug " + runnable.Label,
            ["cargo"] = cargo,
            ["args"] = new JsonArray([.. runnable.ExecutableArgs.Select(a => (JsonNode)JsonValue.Create(a))]),
        };
        if (runnable.Cwd is { } dir) launch["cwd"] = dir;
        if (env.Count > 0) launch["env"] = env;
        var folder = FolderOf(runnable.WorkspaceRoot ?? runnable.Cwd);
        try { await debug.StartAsync(DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(launch), folder)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            if (Services.Window is { } w) await w.ShowMessageAsync(MessageSeverity.Error, $"Debugging '{runnable.Label}' failed: {ex.Message}").ConfigureAwait(false);
        }
    }

    /// <summary>The debug type for Rust, as the debugger's own templates choose it (docs/DEBUGGING-RUST-TS.md, "Which Rust debugger is the default").</summary>
    private string RustDebugType() =>
        (Services.Configuration?.Get<string>("cscode.debug.rustDebugger")?.Trim().ToLowerInvariant()) switch
        {
            "gdb" or "rust-gdb" => "gdb",
            "lldb-dap" => "lldb-dap",
            "lldb" or "codelldb" => "lldb",
            _ => OperatingSystem.IsMacOS() ? "lldb" : "gdb",
        };

    /// <summary>The workspace folder holding <paramref name="path"/> (<c>${workspaceFolder}</c> of the launch), else the path itself.</summary>
    private Uri? FolderOf(string? path)
    {
        if (path is null) return Services.Workspace?.PrimaryFolder;
        var folders = Services.Workspace?.Folders ?? [];
        var holding = folders.Where(f => f.IsFile && (path + "/").StartsWith(f.LocalPath.TrimEnd('/') + "/", StringComparison.Ordinal))
            .OrderByDescending(f => f.LocalPath.Length).FirstOrDefault();
        return holding ?? new Uri(path.TrimEnd('/') + "/");
    }

    /// <summary>cargo on CsCode's PATH (the login shell's on a Mac), else the plain name.</summary>
    private static string Cargo()
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
        return name;
    }
}
