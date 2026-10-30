using System.Collections;
using System.Text.Json;
using System.Text.RegularExpressions;
using CsCode.Core.Platform;
using CsCode.Core.Terminal;
using CsCode.Terminal.Pty;

namespace CsCode.Terminal.Sessions;

/// <summary>
/// Turns <see cref="TerminalCreateOptions"/> into a <see cref="PtyStartInfo"/> the way VS Code does: profiles (<c>terminal.integrated.profiles.*</c>
/// merged over the detected shells, <c>defaultProfile.*</c>, else <c>$SHELL</c>, else bash, zsh, fish, pwsh, sh), the environment (CsCode's,
/// the profile's, <c>terminal.integrated.env.*</c>, the caller's; a null value removes a variable; <c>TERM=xterm-256color</c>,
/// <c>COLORTERM=truecolor</c>, <c>TERM_PROGRAM=cscode</c>) and the working directory (<c>terminal.integrated.cwd</c>, the workspace folder,
/// home). Login shells (<c>-l</c>) only on macOS, as VS Code's default profiles do. Pure apart from reading the file system and settings: it
/// runs off the caller's thread (<see cref="TerminalService.CreateAsync"/>).
/// </summary>
public sealed partial class TerminalLaunchResolver(IConfigurationService? config, IWorkspaceService? workspace, Func<IReadOnlyDictionary<string, string>>? environment = null)
{
    private readonly Func<IReadOnlyDictionary<string, string>> _environment = environment ?? ProcessEnvironment;

    private static readonly string[] DetectionOrder = ["bash", "zsh", "fish", "pwsh", "sh"];

    public static IReadOnlyDictionary<string, string> ProcessEnvironment()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry e in System.Environment.GetEnvironmentVariables())
            if (e.Key is string k && e.Value is string v) env[k] = v;
        return env;
    }

    /// <summary>The profiles offered in the New Terminal dropdown: detected shells overlaid with the user's (null removes one).</summary>
    public IReadOnlyList<TerminalProfile> Profiles()
    {
        var env = _environment();
        var path = env.GetValueOrDefault("PATH");
        var profiles = new Dictionary<string, TerminalProfile>(StringComparer.Ordinal);
        if (OperatingSystem.IsWindows())
        {
            foreach (var (name, exe) in new[] { ("PowerShell", "powershell.exe"), ("Command Prompt", "cmd.exe"), ("pwsh", "pwsh.exe") })
                if (Which(exe, path) is { } p) profiles[name] = new TerminalProfile(name, p, []);
        }
        else
        {
            foreach (var name in DetectionOrder.Append("tmux"))
                if (Which(name, path) is { } p)
                    profiles[name] = new TerminalProfile(name, p, OperatingSystem.IsMacOS() && name is "bash" or "zsh" ? ["-l"] : [], Icon: "terminal-" + (name == "pwsh" ? "powershell" : name));
        }
        if (config?.GetRaw(TerminalSettingKeys.ProfilesKey) is { ValueKind: JsonValueKind.Object } user)
        {
            foreach (var p in user.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Null) { profiles.Remove(p.Name); continue; }
                if (p.Value.ValueKind != JsonValueKind.Object) continue;
                var exe = p.Value.TryGetProperty("path", out var pp) ? pp.ValueKind == JsonValueKind.Array ? pp.EnumerateArray().Select(x => x.GetString()).FirstOrDefault(x => x is not null && Which(x, path) is not null) : pp.GetString() : null;
                if (string.IsNullOrEmpty(exe)) continue;
                var args = p.Value.TryGetProperty("args", out var aa) && aa.ValueKind == JsonValueKind.Array ? aa.EnumerateArray().Select(x => x.GetString() ?? "").ToArray()
                    : aa.ValueKind == JsonValueKind.String ? [aa.GetString()!] : [];
                Dictionary<string, string?>? penv = null;
                if (p.Value.TryGetProperty("env", out var ee) && ee.ValueKind == JsonValueKind.Object)
                    penv = ee.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.ValueKind == JsonValueKind.Null ? null : x.Value.ToString());
                var icon = p.Value.TryGetProperty("icon", out var ii) ? ii.GetString() : null;
                profiles[p.Name] = new TerminalProfile(p.Name, exe, args, penv, icon);
            }
        }
        return profiles.Values.ToList();
    }

    /// <summary>The profile "New Terminal" uses.</summary>
    public TerminalProfile? DefaultProfile()
    {
        var profiles = Profiles();
        if (config?.Get<string>(TerminalSettingKeys.DefaultProfileKey) is { Length: > 0 } name && profiles.FirstOrDefault(p => p.Name == name) is { } chosen) return chosen;
        var env = _environment();
        if (!OperatingSystem.IsWindows() && env.GetValueOrDefault("SHELL") is { Length: > 0 } shell && Which(shell, env.GetValueOrDefault("PATH")) is { } shellPath)
            return profiles.FirstOrDefault(p => p.Path == shellPath) ?? new TerminalProfile(System.IO.Path.GetFileName(shellPath), shellPath, OperatingSystem.IsMacOS() ? ["-l"] : []);
        foreach (var n in DetectionOrder)
            if (profiles.FirstOrDefault(p => p.Name == n) is { } p) return p;
        return profiles.FirstOrDefault();
    }

    /// <summary>The final program, arguments, directory and environment; <see cref="PtySpawnException"/> when no program can be found.</summary>
    public PtyStartInfo Resolve(TerminalCreateOptions options, int columns, int rows)
    {
        TerminalProfile? profile = null;
        if (options.ShellPath is null)
        {
            profile = options.ProfileName is { } pn ? Profiles().FirstOrDefault(p => p.Name == pn) : null;
            profile ??= DefaultProfile() ?? throw new PtySpawnException("No shell was found (set terminal.integrated.defaultProfile or $SHELL).");
        }
        var env = Environment(options, profile);
        var program = options.ShellPath ?? profile!.Path;
        var file = Which(ExpandHome(program, env), env.GetValueOrDefault("PATH")) ?? throw new PtySpawnException($"The terminal program '{program}' was not found.");
        var args = options.ShellArgs ?? profile?.Args ?? [];
        return new PtyStartInfo(file, args.ToArray(), WorkingDirectory(options.Cwd, env), env, columns, rows);
    }

    public Dictionary<string, string> Environment(TerminalCreateOptions options, TerminalProfile? profile)
    {
        var inherit = config?.Get(TerminalSettingKeys.InheritEnv, true) ?? true;
        var source = _environment();
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (inherit) foreach (var (k, v) in source) env[k] = v;
        else foreach (var k in new[] { "PATH", "HOME", "USER", "LOGNAME", "SHELL", "LANG", "DISPLAY", "WAYLAND_DISPLAY", "XDG_RUNTIME_DIR" })
            if (source.TryGetValue(k, out var v)) env[k] = v;
        // A terminal that CsCode itself was started from must not leak into the new one's identity.
        env.Remove("TERM_PROGRAM_VERSION");
        env["TERM"] = "xterm-256color";
        env["COLORTERM"] = "truecolor";
        env["TERM_PROGRAM"] = "cscode";
        env["TERM_PROGRAM_VERSION"] = typeof(TerminalLaunchResolver).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        if (!env.ContainsKey("LANG") && !env.ContainsKey("LC_ALL") && !env.ContainsKey("LC_CTYPE")) env["LANG"] = "C.UTF-8";
        Apply(env, profile?.Env);
        if (config?.GetRaw(TerminalSettingKeys.EnvKey) is { ValueKind: JsonValueKind.Object } user)
            Apply(env, user.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.ToString()));
        Apply(env, options.Env);
        return env;
    }

    private void Apply(Dictionary<string, string> env, IReadOnlyDictionary<string, string?>? overrides)
    {
        if (overrides is null) return;
        foreach (var (k, v) in overrides)
        {
            if (v is null) env.Remove(k);
            else env[k] = Substitute(v, env);
        }
    }

    /// <summary>${env:NAME}, ${workspaceFolder}, ${userHome} (the variables VS Code resolves in terminal settings).</summary>
    public string Substitute(string value, IReadOnlyDictionary<string, string> env) =>
        VariablePattern().Replace(value, m => m.Groups[1].Value switch
        {
            var v when v.StartsWith("env:", StringComparison.Ordinal) => env.GetValueOrDefault(v[4..]) ?? System.Environment.GetEnvironmentVariable(v[4..]) ?? "",
            "workspaceFolder" => WorkspaceFolder() ?? "",
            "userHome" => Home(env),
            _ => m.Value,
        });

    [GeneratedRegex(@"\$\{([^}]+)\}")]
    private static partial Regex VariablePattern();

    private string? WorkspaceFolder() => workspace?.PrimaryFolder is { IsFile: true } f ? f.LocalPath.TrimEnd('/') is { Length: > 0 } p ? p : "/" : null;

    private static string Home(IReadOnlyDictionary<string, string> env) =>
        env.GetValueOrDefault("HOME") is { Length: > 0 } h ? h : System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);

    public string WorkingDirectory(string? requested, IReadOnlyDictionary<string, string> env)
    {
        var home = Home(env);
        foreach (var candidate in new[] { requested, config?.Get<string>(TerminalSettingKeys.Cwd), WorkspaceFolder() })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var path = ExpandHome(Substitute(candidate, env), env);
            if (!System.IO.Path.IsPathRooted(path)) path = System.IO.Path.Combine(WorkspaceFolder() ?? home, path);
            if (Directory.Exists(path)) return path;
            if (File.Exists(path) && System.IO.Path.GetDirectoryName(path) is { } parent && Directory.Exists(parent)) return parent; // "Open in Terminal" on a file
        }
        return Directory.Exists(home) ? home : "/";
    }

    private static string ExpandHome(string path, IReadOnlyDictionary<string, string> env) =>
        path == "~" ? Home(env) : path.StartsWith("~/", StringComparison.Ordinal) ? System.IO.Path.Combine(Home(env), path[2..]) : path;

    /// <summary>An executable file by absolute/relative path, or searched on <paramref name="path"/>.</summary>
    public static string? Which(string name, string? path)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (name.Contains('/') || (OperatingSystem.IsWindows() && name.Contains('\\')))
            return File.Exists(name) ? System.IO.Path.GetFullPath(name) : null;
        foreach (var dir in (path ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = System.IO.Path.Combine(dir, name);
            if (File.Exists(candidate) && IsExecutable(candidate)) return candidate;
        }
        return null;
    }

    private static bool IsExecutable(string file) =>
        OperatingSystem.IsWindows() || (File.GetUnixFileMode(file) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
}
