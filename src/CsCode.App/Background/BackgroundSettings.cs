using System.Text.Json;
using CsCode.Core.Platform;
using CsCode.Sessions;
using CsCode.Sessions.Launcher;

namespace CsCode.App.Background;

/// <summary>The settings of background mode and sessions (registered so the Settings UI lists them; docs/SESSIONS.md).</summary>
public static class BackgroundSettings
{
    public const string KeepRunning = "cscode.background.keepRunning";
    public const string ShowTrayIcon = "cscode.background.showTrayIcon";
    public const string TrimAfterSeconds = "cscode.background.trimAfterSeconds";
    public const string IdleServerTimeoutMinutes = "cscode.background.idleServerTimeoutMinutes";
    public const string MaxResidentSessions = LaunchSettings.MaxResidentKey;
    public const string WarmSpare = LaunchSettings.WarmSpareKey;
    public const string RestoreOnLogin = LaunchSettings.RestoreOnLoginKey;
    public const string DefaultSession = LaunchSettings.DefaultSessionKey;
    public const string MaxBackupMb = "cscode.sessions.maxBackupMb";
    public const string HotExit = "files.hotExit";

    public const string HotExitOff = "off";
    public const string HotExitOnExit = "onExit";
    public const string HotExitOnExitAndWindowClose = "onExitAndWindowClose";

    public static IReadOnlyList<ConfigurationProperty> Properties { get; } =
    [
        new(KeepRunning, Json(true), "Closing a window hides it and keeps its session running in the background (shown again in a moment by `cscode`). File > Quit Session ends it."),
        new(ShowTrayIcon, Json(true), "Show a tray icon listing the running sessions while CsCode runs in the background."),
        new(TrimAfterSeconds, Json(30), "Seconds after a window was hidden until its editor views are released and its memory is trimmed."),
        new(IdleServerTimeoutMinutes, Json(15), "Minutes after a window was hidden until its language servers are stopped (restarted when it is shown again). 0 = never."),
        new(MaxResidentSessions, Json(3), "How many sessions stay resident; beyond it the least recently shown hidden session is saved and its process exits."),
        new(WarmSpare, Json(true), "Keep one pre-started CsCode process ready, so a session that is not running opens fast."),
        new(RestoreOnLogin, Json(false), "With `cscode --background` at login: also restart the sessions that were running before (hidden)."),
        new(DefaultSession, Json("last"), "What `cscode` with no session opens: `last` (the session shown last), `new` (a new window) or a session name."),
        new(MaxBackupMb, Json(200), "Upper bound (MB) of the unsaved-changes backups of one session; documents beyond it are not backed up and ask to be saved on quit."),
        new(HotExit, Json(HotExitOnExitAndWindowClose), "Back up unsaved changes when a session quits instead of asking: `off`, `onExit`, `onExitAndWindowClose`."),
    ];

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);

    public static HiddenPolicySettings HiddenPolicy(IConfigurationService? config)
    {
        var trim = Math.Max(0, config?.Get(TrimAfterSeconds, 30) ?? 30);
        var idle = config?.Get(IdleServerTimeoutMinutes, 15) ?? 15;
        return new HiddenPolicySettings(TimeSpan.FromSeconds(trim), idle <= 0 ? null : TimeSpan.FromMinutes(idle));
    }

    public static long MaxBackupBytes(IConfigurationService? config) => Math.Max(1, config?.Get(MaxBackupMb, 200) ?? 200) * 1024L * 1024L;
}
