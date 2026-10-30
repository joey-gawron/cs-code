using System.Security.Cryptography;

namespace CsCode.Terminal.Sessions;

/// <summary>
/// VS Code's shell-integration injection, with CsCode's own scripts (shipped next to the application in <c>shellIntegration/</c>): bash, zsh,
/// fish and pwsh are started so that they read the user's startup files as usual and then install OSC 633 hooks (prompt, command line,
/// output start, exit code, cwd). Nothing in the user's home is written or changed:
/// <list type="bullet">
/// <item>bash: <c>--init-file cscode.bash</c> (bash then reads no rc file itself; the script reads /etc/bash.bashrc and ~/.bashrc — or for
/// <c>-l</c> the profile files — first; the profile's <c>--rcfile</c>, <c>--norc</c>, <c>--noprofile</c> are honoured through variables).</item>
/// <item>zsh: <c>ZDOTDIR</c> points at <c>shellIntegration/zsh</c>, whose dot files read the user's from <c>CSCODE_USER_ZDOTDIR</c> and hand
/// ZDOTDIR back.</item>
/// <item>fish: <c>--init-command "source cscode.fish"</c> (runs after fish's own configuration).</item>
/// <item>pwsh: <c>-NoExit -Command ". cscode.ps1"</c> (runs after the profile).</item>
/// </list>
/// As in VS Code, only a shell started with no arguments or with arguments known to keep it an interactive shell is injected (a profile with
/// <c>-c</c>, a script or unknown flags runs untouched). <c>CSCODE_NONCE</c> lets the emulator trust the command lines the script reports.
/// </summary>
public static class ShellIntegrationInjector
{
    /// <summary>Folder (next to the application) holding the scripts.</summary>
    public const string FolderName = "shellIntegration";

    public const string NonceVariable = "CSCODE_NONCE";
    public const string InjectionVariable = "CSCODE_INJECTION";

    /// <summary>The shipped scripts' folder, or null when it is missing (then nothing is injected).</summary>
    public static string? DefaultScriptsDirectory()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, FolderName);
        return File.Exists(Path.Combine(dir, "cscode.bash")) ? dir : null;
    }

    /// <summary>What to start instead: the arguments and the variables to add (a null value removes one).</summary>
    public sealed record Injection(string Shell, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string?> Environment);

    /// <summary>The shell's kind by its file name ("bash", "zsh", "fish", "pwsh"), or null for any other program.</summary>
    public static string? ShellKind(string executable)
    {
        var name = Path.GetFileName(executable);
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name switch
        {
            "bash" => "bash",
            "zsh" => "zsh",
            "fish" => "fish",
            "pwsh" or "pwsh-preview" => "pwsh",
            _ => null,
        };
    }

    /// <summary>A new random nonce (hex) for one terminal.</summary>
    public static string NewNonce() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// The injected start of <paramref name="executable"/> with <paramref name="args"/>, or null when the shell is not one CsCode integrates,
    /// its arguments do not leave it a plain interactive shell, or the scripts are missing.
    /// </summary>
    public static Injection? For(string executable, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> environment, string scriptsDirectory, string nonce)
    {
        var kind = ShellKind(executable);
        if (kind is null || !Directory.Exists(scriptsDirectory)) return null;
        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [InjectionVariable] = "1",
            [NonceVariable] = nonce,
        };
        switch (kind)
        {
            case "bash":
            {
                var script = Path.Combine(scriptsDirectory, "cscode.bash");
                if (!File.Exists(script)) return null;
                var norc = false;
                for (var i = 0; i < args.Count; i++)
                {
                    switch (args[i])
                    {
                        case "-l" or "--login": env["CSCODE_SHELL_LOGIN"] = "1"; break;
                        case "-i": break;
                        case "--noprofile": env["CSCODE_BASH_NOPROFILE"] = "1"; break;
                        case "--norc": norc = true; break;
                        case "--rcfile" or "--init-file" when i + 1 < args.Count: env["CSCODE_BASH_RCFILE"] = args[++i]; break;
                        default: return null; // -c, a script, --posix...: not a plain interactive shell
                    }
                }
                if (norc)
                {
                    // --norc also turns --init-file off. bash keeps its own arguments and startup (none of the rc files), and the first
                    // prompt sources the script through PROMPT_COMMAND, which nothing can have replaced (the script un-exports it).
                    var bootstrap = new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        [InjectionVariable] = "1",
                        [NonceVariable] = nonce,
                        ["CSCODE_BASH_BOOTSTRAP"] = "1",
                        ["PROMPT_COMMAND"] = "builtin source '" + script.Replace("'", "'\\''", StringComparison.Ordinal) + "'",
                    };
                    // a PROMPT_COMMAND the environment already had is handed over and kept (the script restores it)
                    if (environment.GetValueOrDefault("PROMPT_COMMAND") is { Length: > 0 } own) bootstrap["CSCODE_BASH_PROMPT_COMMAND"] = own;
                    return new Injection(kind, [.. args], bootstrap);
                }
                // A login shell ignores --init-file: CsCode starts it as an interactive shell and the script reads the profile files. Note
                // that bash itself still reads its system-wide rc file first where it was built with one (Debian's /etc/bash.bashrc).
                return new Injection(kind, ["--init-file", script], env);
            }
            case "zsh":
            {
                var dir = Path.Combine(scriptsDirectory, "zsh");
                if (!File.Exists(Path.Combine(dir, ".zshrc")) || args.Any(a => a is not ("-l" or "--login" or "-i" or "--interactive"))) return null;
                var user = environment.GetValueOrDefault("ZDOTDIR");
                env["CSCODE_USER_ZDOTDIR"] = string.IsNullOrEmpty(user) ? environment.GetValueOrDefault("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : user;
                if (string.IsNullOrEmpty(user)) env["CSCODE_ZDOTDIR_UNSET"] = "1";
                env["ZDOTDIR"] = dir;
                return new Injection(kind, [.. args], env);
            }
            case "fish":
            {
                var script = Path.Combine(scriptsDirectory, "cscode.fish");
                if (!File.Exists(script) || args.Any(a => a is not ("-l" or "--login" or "-i" or "--interactive" or "-N" or "--no-config"))) return null;
                // fish single quotes: \\ and \' are the only escapes
                var quoted = "'" + script.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";
                return new Injection(kind, [.. args, "--init-command", "source " + quoted], env);
            }
            case "pwsh":
            {
                var script = Path.Combine(scriptsDirectory, "cscode.ps1");
                static bool Known(string a) => a.ToLowerInvariant() is "-l" or "-login" or "-nol" or "-nologo" or "-nop" or "-noprofile";
                if (!File.Exists(script) || !args.All(Known)) return null;
                // PowerShell single quotes: '' is a quote
                var quoted = "'" + script.Replace("'", "''", StringComparison.Ordinal) + "'";
                return new Injection(kind, [.. args, "-NoExit", "-Command", ". " + quoted], env);
            }
        }
        return null;
    }
}
