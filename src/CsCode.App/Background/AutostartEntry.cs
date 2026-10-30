using System.Text;
using CsCode.Sessions.Launcher;

namespace CsCode.App.Background;

/// <summary>
/// "Start CsCode in the background at login" from inside the app (Linux): an XDG autostart entry
/// (<c>$XDG_CONFIG_HOME/autostart/cscode-background.desktop</c>) running <c>cscode --background</c>, the same file
/// <c>tools/autostart/install-linux.sh --xdg</c> writes (the script also offers a systemd user unit). Only ever written after the user confirmed
/// (<see cref="SessionCommandIds.StartAtLogin"/>). Windows and macOS use the scripts in <c>tools/autostart/</c>. Synchronous file I/O: callers run
/// it on the session lane.
/// </summary>
public static class AutostartEntry
{
    public const string FileName = "cscode-background.desktop";

    /// <summary>The entry's path, or null on platforms where the app does not manage it itself.</summary>
    public static string? PathFor(Func<string, string?>? getEnv = null)
    {
        if (!OperatingSystem.IsLinux()) return null;
        getEnv ??= Environment.GetEnvironmentVariable;
        var config = getEnv("XDG_CONFIG_HOME") is { Length: > 0 } x && x.StartsWith('/')
            ? x
            : Path.Combine(getEnv("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(config, "autostart", FileName);
    }

    public static bool IsInstalled(Func<string, string?>? getEnv = null) => PathFor(getEnv) is { } p && File.Exists(p);

    /// <summary>The launcher to run at login: the installed <c>~/.local/bin/cscode</c> when there is one, else this executable.</summary>
    public static string DefaultExecutable(Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        var home = getEnv("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var installed = Path.Combine(home, ".local", "bin", "cscode");
        return File.Exists(installed) ? installed : Environment.ProcessPath ?? "cscode";
    }

    /// <summary>
    /// What <c>cscode --background</c> starts at login with these settings, for the confirmation: the warm spare is off by default, and without it
    /// (or <c>restoreOnLogin</c>) an autostart entry would start nothing.
    /// </summary>
    public static string WhatStartsAtLogin(LaunchSettings settings) => (settings.WarmSpare, settings.RestoreOnLogin) switch
    {
        (true, true) => "the warm spare and the sessions that were running, hidden; no window opens",
        (true, false) => "the warm spare; no window opens",
        (false, true) => "the sessions that were running, hidden; no window opens",
        _ => $"nothing yet: turn on {LaunchSettings.WarmSpareKey} (a pre-started process, about 100-130 MB) or {LaunchSettings.RestoreOnLoginKey} first",
    };

    public static string Content(string executable) => new StringBuilder()
        .Append("[Desktop Entry]\n")
        .Append("Type=Application\n")
        .Append("Name=CsCode (background)\n")
        .Append("Comment=Starts CsCode in the background so windows open instantly\n")
        .Append("Exec=").Append(Quote(executable)).Append(" --background\n")
        .Append("NoDisplay=true\n")
        .Append("X-GNOME-Autostart-enabled=true\n")
        .ToString();

    /// <summary>Desktop-entry Exec quoting: a path with spaces or quotes is double-quoted with its specials escaped.</summary>
    private static string Quote(string path) =>
        path.Any(c => c is ' ' or '"' or '\\' or '`' or '$') ? "\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$") + "\"" : path;

    public static void Install(string executable, Func<string, string?>? getEnv = null)
    {
        var path = PathFor(getEnv) ?? throw new PlatformNotSupportedException("use tools/autostart/ on this platform");
        Sessions.AtomicFile.Write(path, Encoding.UTF8.GetBytes(Content(executable)));
    }

    public static void Uninstall(Func<string, string?>? getEnv = null)
    {
        if (PathFor(getEnv) is { } path && File.Exists(path)) File.Delete(path);
    }
}
