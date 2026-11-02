using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CsCode.Core.Debugging;
using CsCode.Core.Platform;
using CsCode.Dap.Configuration;
using Microsoft.Extensions.Logging;

namespace CsCode.Dap.Adapters;

/// <summary>A Zig installation as the debuggers need it: the compiler, its version and its library directory (where its LLDB pretty printers are).</summary>
/// <param name="LibDir"><c>lib_dir</c> of <c>zig env</c>, absolute.</param>
internal sealed record ZigToolchain(string Executable, string Version, string LibDir)
{
    /// <summary>Zig's LLDB pretty printers (slices, optionals, error unions, tagged unions, std containers), shipped in the binary distribution
    /// since 0.12 as <c>lib/lldb/pretty_printers.py</c>; they need LLDB's Python to be 3.10 or newer (<c>match</c> statements).</summary>
    public string PrettyPrinters => Path.Combine(LibDir, "lldb", "pretty_printers.py");
}

/// <summary>
/// Finds the Zig compiler: setting <c>zig.path</c> (VS Code's Zig extension), then <c>zls.zig_exe_path</c> (the one zls is told to use), then
/// <c>zig</c> on PATH; and asks it <c>zig env</c> (bounded, cached per executable until it changes) for its version and library directory.
/// Shared by the CodeLLDB and lldb-dap factories.
/// </summary>
internal sealed class ZigToolchainLocator(Func<string, string?> environment, Func<string, string?> setting, ILogger logger)
{
    /// <summary>VS Code's Zig extension setting: the zig executable (or a directory holding it).</summary>
    public const string SettingPath = "zig.path";
    /// <summary>zls's own setting (forwarded by workspace/configuration): the zig zls runs.</summary>
    public const string ZlsSettingPath = "zls.zig_exe_path";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (DateTime Stamp, ZigToolchain? Toolchain)> _byExecutable = new(StringComparer.Ordinal);

    public static string ExecutableName => OperatingSystem.IsWindows() ? "zig.exe" : "zig";

    /// <summary>The zig executable without running anything; null when none is configured or on PATH.</summary>
    public string? LocateExecutable()
    {
        foreach (var key in new[] { SettingPath, ZlsSettingPath })
            if (setting(key) is { Length: > 0 } configured && Resolve(configured) is { } found) return found;
        return RustToolchainLocator.FindOnPath(environment, ExecutableName);
    }

    /// <summary>Accepts the executable itself or a directory holding it.</summary>
    internal static string? Resolve(string path)
    {
        try
        {
            if (File.Exists(path)) return Path.GetFullPath(path);
            if (Directory.Exists(path) && File.Exists(Path.Combine(path, ExecutableName))) return Path.GetFullPath(Path.Combine(path, ExecutableName));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { }
        return null;
    }

    /// <summary>The toolchain, or null without a zig (or when <c>zig env</c> fails).</summary>
    public async Task<ZigToolchain?> FindAsync(CancellationToken ct)
    {
        if (LocateExecutable() is not { } exe) return null;
        var stamp = File.GetLastWriteTimeUtc(exe);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_byExecutable.TryGetValue(exe, out var known) && known.Stamp == stamp) return known.Toolchain;
            ZigToolchain? found = null;
            // zig env prints lib_dir relative to its working directory when it can: run it beside the executable, so the base is known
            var dir = Path.GetDirectoryName(exe)!;
            if (await RunAsync(exe, ["env"], dir, ct).ConfigureAwait(false) is { } env && ParseEnv(env) is { Version: { } version, LibDir: { } lib })
                found = new ZigToolchain(exe, version, Path.GetFullPath(Path.Combine(dir, lib)));
            else logger.LogInformation("'{Zig} env' gave no version and library directory", exe);
            if (_byExecutable.Count >= 8) _byExecutable.Clear(); // bounded: a handful of zigs per session at most
            _byExecutable[exe] = (stamp, found);
            return found;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// <c>version</c> and <c>lib_dir</c> of <c>zig env</c>: ZON since Zig 0.15 (<c>.lib_dir = "…",</c>), JSON before (<c>"lib_dir": "…",</c>).
    /// </summary>
    internal static (string? Version, string? LibDir) ParseEnv(string text) => (Field(text, "version"), Field(text, "lib_dir"));

    private static string? Field(string text, string name)
    {
        var m = Regex.Match(text, $@"(?m)^\s*(?:\.{name}|""{name}"")\s*[=:]\s*""((?:[^""\\]|\\.)*)""", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        if (!m.Success) return null;
        try { return JsonSerializer.Deserialize<string>("\"" + m.Groups[1].Value + "\""); } // both formats escape like JSON for plain paths
        catch (JsonException) { return m.Groups[1].Value; }
    }

    private async Task<string?> RunAsync(string exe, IEnumerable<string> args, string cwd, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = cwd };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            var stdout = p.StandardOutput.ReadToEndAsync(timeout.Token);
            _ = p.StandardError.ReadToEndAsync(timeout.Token);
            try { await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                if (ct.IsCancellationRequested) throw;
                return null;
            }
            return p.ExitCode == 0 ? await stdout.ConfigureAwait(false) : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            logger.LogDebug(ex, "{Tool} failed", exe);
            return null;
        }
    }
}

/// <summary>
/// What the LLDB debuggers (CodeLLDB, lldb-dap) do for Zig: the launch templates of a <c>build.zig</c> project with their tasks.json, and the
/// <c>initCommands</c> that load Zig's own LLDB pretty printers through CsCode's helper <c>cscode_zig_lldb.py</c> (which checks LLDB's Python
/// first: the printers need 3.10, Apple's LLDB embeds 3.9).
/// </summary>
internal static class ZigDebugging
{
    /// <summary>CsCode's loader for Zig's pretty printers, copied next to the assembly.</summary>
    public const string HelperScript = "cscode_zig_lldb.py";
    public const string BuildTask = "zig: build";
    public const string TestBinaryTask = "zig: build test binary of the current file";
    /// <summary>Where the test-binary task emits (zig creates <c>.zig-cache</c> itself, unlike <c>zig-out/…</c>, so the first run works too).</summary>
    public const string TestBinary = "${workspaceFolder}/.zig-cache/${fileBasenameNoExtension}-test";

    public static string DefaultHelperDirectory => Path.Combine(AppContext.BaseDirectory, "Adapters", "Lldb");

    /// <summary>True for a Zig configuration: <c>sourceLanguages</c> names zig, or the folder is a <c>build.zig</c> project.</summary>
    public static bool IsZig(IReadOnlyDictionary<string, JsonElement> props, string? folderPath) =>
        (props.TryGetValue("sourceLanguages", out var langs) && langs.ValueKind == JsonValueKind.Array
            && langs.EnumerateArray().Any(l => l.ValueKind == JsonValueKind.String && string.Equals(l.GetString(), "zig", StringComparison.OrdinalIgnoreCase)))
        || (folderPath is not null && File.Exists(Path.Combine(folderPath, "build.zig")));

    /// <summary>
    /// The commands that load the pretty printers: import CsCode's helper, then let it import Zig's script and enable the <c>zig.lang</c> and
    /// <c>zig.std</c> categories (or print why not). Empty when the toolchain has no printers or the helper is missing.
    /// </summary>
    public static IReadOnlyList<string> PrettyPrinterCommands(ZigToolchain? zig, string helperDirectory)
    {
        var helper = Path.Combine(helperDirectory, HelperScript);
        if (zig is null || !File.Exists(zig.PrettyPrinters) || !File.Exists(helper)) return [];
        return
        [
            $"command script import {LldbQuote(helper)}",
            // a JSON string is a Python string literal for any path
            $"script cscode_zig_lldb.load(lldb.debugger, {JsonSerializer.Serialize(zig.PrettyPrinters)})",
        ];
    }

    /// <summary>Puts <paramref name="commands"/> before the user's own <c>initCommands</c>.</summary>
    public static void PrependInitCommands(Dictionary<string, JsonElement> props, IReadOnlyList<string> commands)
    {
        if (commands.Count == 0) return;
        var init = new JsonArray([.. commands.Select(c => (JsonNode)c)]);
        if (props.TryGetValue("initCommands", out var mine) && mine.ValueKind == JsonValueKind.Array)
            foreach (var c in mine.EnumerateArray()) init.Add(JsonNode.Parse(c.GetRawText()));
        props["initCommands"] = JsonSerializer.SerializeToElement(init);
    }

    /// <summary>An argument for an LLDB command line: double quotes, with backslashes and quotes escaped.</summary>
    internal static string LldbQuote(string text) => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    /// <summary>
    /// The templates of a <c>build.zig</c> project for debug type <paramref name="type"/>: each executable <c>zig build</c> installs into
    /// <c>zig-out/bin</c> (the <see cref="BuildTask"/> pre-launch task), the tests of the file in the editor (a binary <c>zig test --test-no-exec</c>
    /// emits, the <see cref="TestBinaryTask"/> pre-launch task) and attach. <paramref name="decorate"/> adds the adapter's own keys.
    /// </summary>
    public static IReadOnlyList<DebugConfiguration> Templates(ZigProject project, string type, string adapterLabel, Uri folder, Action<JsonObject>? decorate = null)
    {
        var configs = new List<JsonObject>();
        var exe = OperatingSystem.IsWindows() ? ".exe" : "";
        foreach (var name in project.Executables.Count > 0 ? project.Executables : [project.Name])
            configs.Add(new JsonObject
            {
                ["name"] = $"Zig: Debug {name} ({adapterLabel}, zig build)",
                ["type"] = type,
                ["request"] = "launch",
                ["program"] = $"${{workspaceFolder}}/zig-out/bin/{name}{exe}",
                ["args"] = new JsonArray(),
                ["cwd"] = "${workspaceFolder}",
                ["preLaunchTask"] = BuildTask,
            });
        configs.Add(new JsonObject
        {
            ["name"] = $"Zig: Debug the tests of the current file ({adapterLabel}, zig test)",
            ["type"] = type,
            ["request"] = "launch",
            ["program"] = TestBinary + exe,
            ["args"] = new JsonArray(),
            ["cwd"] = "${workspaceFolder}",
            ["preLaunchTask"] = TestBinaryTask,
        });
        foreach (var c in configs) decorate?.Invoke(c);
        configs.Add(new JsonObject
        {
            ["name"] = $"Zig: Attach to {project.Executables.FirstOrDefault() ?? project.Name} ({adapterLabel})",
            ["type"] = type,
            ["request"] = "attach",
            ["pid"] = "${command:pickProcess}",
        });
        return [.. configs.Select(c => DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(c), folder))];
    }

    /// <summary>
    /// The tasks the templates' preLaunchTask names: <c>zig build</c> (the default build task) and the current file's test binary, both PROCESS
    /// tasks with the <c>$zig</c> problem matcher. <paramref name="zig"/> is the command: <c>zig</c> when it is on PATH, else the configured path.
    /// </summary>
    public static string TasksJson(string zig)
    {
        var root = new JsonObject
        {
            ["version"] = "2.0.0",
            ["tasks"] = new JsonArray(
                new JsonObject
                {
                    ["label"] = BuildTask,
                    ["type"] = "process",
                    ["command"] = zig,
                    ["args"] = new JsonArray("build"),
                    ["options"] = new JsonObject { ["cwd"] = "${workspaceFolder}" },
                    ["problemMatcher"] = "$zig",
                    ["group"] = new JsonObject { ["kind"] = "build", ["isDefault"] = true },
                },
                new JsonObject
                {
                    ["label"] = TestBinaryTask,
                    ["type"] = "process",
                    ["command"] = zig,
                    ["args"] = new JsonArray("test", "${file}", "--test-no-exec", "-femit-bin=" + TestBinary + (OperatingSystem.IsWindows() ? ".exe" : "")),
                    ["options"] = new JsonObject { ["cwd"] = "${workspaceFolder}" },
                    ["problemMatcher"] = "$zig",
                    ["group"] = "test",
                }),
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    /// <summary>The command the tasks run: <c>zig</c> when PATH has it (portable tasks.json), else the absolute path the locator found.</summary>
    public static string TaskCommand(ZigToolchainLocator locator, Func<string, string?> environment) =>
        RustToolchainLocator.FindOnPath(environment, ZigToolchainLocator.ExecutableName) is not null || locator.LocateExecutable() is not { } exe ? "zig" : exe;
}

/// <summary>What the templates need from a folder's <c>build.zig</c>: the project's name (its folder) and the executables it adds.</summary>
internal sealed record ZigProject(string Name, IReadOnlyList<string> Executables)
{
    private const int MaxExecutables = 8;

    /// <summary>The folder's project, or null without a <c>build.zig</c> at its root.</summary>
    public static async Task<ZigProject?> ReadAsync(DapPlatform p, Uri? folder, CancellationToken ct)
    {
        if (folder is not { IsFile: true } || p.Workspace is not { } ws) return null;
        string text;
        try
        {
            var uri = new Uri(folder.ToString().TrimEnd('/') + "/build.zig");
            if (await ws.FileSystem.StatAsync(uri, ct) is not { Type: FileType.File }) return null;
            text = Encoding.UTF8.GetString(await ws.FileSystem.ReadFileAsync(uri, ct));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UriFormatException) { return null; }
        var name = Path.GetFileName(DapPlatform.NormalizeDir(folder.LocalPath).TrimEnd('/', '\\'));
        return new ZigProject(name.Length > 0 ? name : "app", ExecutableNames(text));
    }

    /// <summary>
    /// The literal <c>.name = "…"</c> of each <c>addExecutable(.{ … })</c> (at the top level of its options, so a nested module's options do not
    /// count), comments and strings skipped; a name that is not a literal is not known and left out.
    /// </summary>
    internal static IReadOnlyList<string> ExecutableNames(string buildZig)
    {
        var text = WithoutComments(buildZig);
        var names = new List<string>();
        const string call = "addExecutable(";
        for (var at = text.IndexOf(call, StringComparison.Ordinal); at >= 0 && names.Count < MaxExecutables; at = text.IndexOf(call, at + 1, StringComparison.Ordinal))
        {
            var depth = 0;
            for (var i = at + call.Length; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '"') { i = EndOfString(text, i); continue; }
                if (c is '(' or '{' or '[') { depth++; continue; }
                if (c is ')' or '}' or ']') { if (--depth < 0) break; continue; }
                if (depth != 1 || c != '.' || string.CompareOrdinal(text, i, ".name", 0, 5) != 0) continue;
                var m = Regex.Match(text[i..Math.Min(text.Length, i + 300)], @"^\.name\s*=\s*""((?:[^""\\\n]|\\.)*)""", RegexOptions.CultureInvariant);
                if (m.Success && m.Groups[1].Value.Length > 0 && !names.Contains(m.Groups[1].Value)) names.Add(m.Groups[1].Value);
                break;
            }
        }
        return names;
    }

    /// <summary>The text with each <c>//</c> comment blanked (strings respected), so positions are kept.</summary>
    private static string WithoutComments(string text)
    {
        var sb = new StringBuilder(text);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '"') { i = EndOfString(text, i); continue; }
            if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] == '\\')
            {
                // a multiline string line: to the end of the line, kept as it is
                var nl = text.IndexOf('\n', i);
                i = nl < 0 ? text.Length : nl;
                continue;
            }
            if (text[i] != '/' || i + 1 >= text.Length || text[i + 1] != '/') continue;
            var end = text.IndexOf('\n', i);
            if (end < 0) end = text.Length;
            for (var k = i; k < end; k++) sb[k] = ' ';
            i = end;
        }
        return sb.ToString();
    }

    /// <summary>The index of the closing quote of the string starting at <paramref name="open"/> (or the line's end).</summary>
    private static int EndOfString(string text, int open)
    {
        for (var i = open + 1; i < text.Length; i++)
        {
            if (text[i] == '\\') { i++; continue; }
            if (text[i] is '"' or '\n') return i;
        }
        return text.Length;
    }
}
