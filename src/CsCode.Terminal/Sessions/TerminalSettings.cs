using System.Text.Json;
using CsCode.Core.Platform;

namespace CsCode.Terminal.Sessions;

/// <summary>
/// The <c>terminal.integrated.*</c> settings (VS Code's names and defaults; <c>allowOsc52</c> and <c>reflow</c> are CsCode's), declared by
/// <see cref="TerminalService"/> so the Settings page lists them. Read on demand (the configuration service is thread safe).
/// </summary>
public static class TerminalSettingKeys
{
    public const string Prefix = "terminal.integrated.";
    public const string DefaultProfileLinux = Prefix + "defaultProfile.linux";
    public const string DefaultProfileOsx = Prefix + "defaultProfile.osx";
    public const string DefaultProfileWindows = Prefix + "defaultProfile.windows";
    public const string ProfilesLinux = Prefix + "profiles.linux";
    public const string ProfilesOsx = Prefix + "profiles.osx";
    public const string ProfilesWindows = Prefix + "profiles.windows";
    public const string EnvLinux = Prefix + "env.linux";
    public const string EnvOsx = Prefix + "env.osx";
    public const string EnvWindows = Prefix + "env.windows";
    public const string Cwd = Prefix + "cwd";
    public const string InheritEnv = Prefix + "inheritEnv";
    public const string Scrollback = Prefix + "scrollback";
    public const string AllowOsc52 = Prefix + "allowOsc52";
    public const string Reflow = Prefix + "reflow";
    public const string ConfirmOnKill = Prefix + "confirmOnKill";
    public const string ConfirmOnExit = Prefix + "confirmOnExit";
    public const string FontFamily = Prefix + "fontFamily";
    public const string FontSize = Prefix + "fontSize";
    public const string LineHeight = Prefix + "lineHeight";
    public const string LetterSpacing = Prefix + "letterSpacing";
    public const string DrawBoldTextInBrightColors = Prefix + "drawBoldTextInBrightColors";
    public const string MinimumContrastRatio = Prefix + "minimumContrastRatio";
    public const string CursorStyle = Prefix + "cursorStyle";
    public const string CursorBlinking = Prefix + "cursorBlinking";
    public const string CommandsToSkipShell = Prefix + "commandsToSkipShell";
    public const string SendKeybindingsToShell = Prefix + "sendKeybindingsToShell";
    public const string AllowChords = Prefix + "allowChords";
    public const string WordSeparators = Prefix + "wordSeparators";
    public const string CopyOnSelection = Prefix + "copyOnSelection";
    public const string EnableMultiLinePasteWarning = Prefix + "enableMultiLinePasteWarning";
    public const string RightClickBehavior = Prefix + "rightClickBehavior";
    public const string AltClickMovesCursor = Prefix + "altClickMovesCursor";
    public const string PersistentSessionReviveProcess = Prefix + "persistentSessionReviveProcess";
    public const string SplitCwd = Prefix + "splitCwd";

    /// <summary>VS Code's default word separators for double-click selection.</summary>
    public const string DefaultWordSeparators = " ()[]{}',\"`─‘’“”|";

    public static string DefaultProfileKey => OperatingSystem.IsWindows() ? DefaultProfileWindows : OperatingSystem.IsMacOS() ? DefaultProfileOsx : DefaultProfileLinux;
    public static string ProfilesKey => OperatingSystem.IsWindows() ? ProfilesWindows : OperatingSystem.IsMacOS() ? ProfilesOsx : ProfilesLinux;
    public static string EnvKey => OperatingSystem.IsWindows() ? EnvWindows : OperatingSystem.IsMacOS() ? EnvOsx : EnvLinux;

    private static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value);

    /// <summary>Every setting the terminal reads, with VS Code's default (for the schema-driven Settings page).</summary>
    public static IReadOnlyList<ConfigurationProperty> Properties { get; } =
    [
        new(DefaultProfileLinux, null, "The default terminal profile on Linux (a name from terminal.integrated.profiles.linux). Default: $SHELL, else bash, zsh, fish, pwsh, sh."),
        new(DefaultProfileOsx, null, "The default terminal profile on macOS."),
        new(DefaultProfileWindows, null, "The default terminal profile on Windows."),
        new(ProfilesLinux, null, "Terminal profiles on Linux: { \"name\": { \"path\", \"args\", \"env\", \"icon\" } } (null removes a detected profile)."),
        new(ProfilesOsx, null, "Terminal profiles on macOS."),
        new(ProfilesWindows, null, "Terminal profiles on Windows."),
        new(EnvLinux, null, "Environment variables added to terminals on Linux (a null value removes a variable)."),
        new(EnvOsx, null, "Environment variables added to terminals on macOS."),
        new(EnvWindows, null, "Environment variables added to terminals on Windows."),
        new(Cwd, J(""), "Where new terminals start (default: the workspace folder, else the home directory). ${workspaceFolder} and ~ are expanded."),
        new(InheritEnv, J(true), "Whether new terminals inherit CsCode's environment."),
        new(Scrollback, J(1000), "Lines kept in a terminal's scrollback (a hard memory cap applies as well)."),
        new(AllowOsc52, J(false), "Let programs write the clipboard with OSC 52 (reading it is never allowed)."),
        new(Reflow, J(true), "Re-wrap lines when the terminal's width changes."),
        new(ConfirmOnKill, J("editor"), "Confirm killing terminals that have child processes: never, editor, panel, always."),
        new(ConfirmOnExit, J("never"), "Confirm closing the window while terminals have child processes: never, always, hasChildProcesses."),
        new(FontFamily, J(""), "Font of the terminal (default: editor.fontFamily)."),
        new(FontSize, J(0), "Font size of the terminal in pixels (0: editor.fontSize)."),
        new(LineHeight, J(1.0), "Line height as a multiple of the font size."),
        new(LetterSpacing, J(0.0), "Extra pixels between characters."),
        new(DrawBoldTextInBrightColors, J(true), "Draw bold text in the bright variant of the 8 ANSI colours."),
        new(MinimumContrastRatio, J(4.5), "Raise the foreground's contrast to at least this ratio (1: off)."),
        new(CursorStyle, J("block"), "Cursor style: block, line, underline."),
        new(CursorBlinking, J(false), "Blink the cursor."),
        new(CommandsToSkipShell, J(Array.Empty<string>()), "Commands whose keybindings always go to CsCode instead of the shell ('-id' removes one of the defaults)."),
        new(SendKeybindingsToShell, J(false), "Send keys to the shell instead of running their keybindings (except commandsToSkipShell)."),
        new(AllowChords, J(true), "Keys that start a chord go to CsCode while the terminal has focus."),
        new(WordSeparators, J(DefaultWordSeparators), "Characters that end a word when double-clicking."),
        new(CopyOnSelection, J(false), "Copy the selection to the clipboard as soon as it is made."),
        new(EnableMultiLinePasteWarning, J("auto"), "Warn before pasting several lines: auto (only without bracketed paste), always, never."),
        new(RightClickBehavior, J("default"), "Right-click: default (context menu), copyPaste, paste, selectWord, nothing."),
        new(AltClickMovesCursor, J(true), "Alt+click moves the prompt's cursor (not implemented: the click is passed on)."),
        new(PersistentSessionReviveProcess, J("never"), "CsCode does not restore terminals after a restart; only 'never' is honoured."),
        new(SplitCwd, J("inherited"), "Where a split terminal starts: inherited (the current directory of the terminal it splits), initial (where that terminal started), workspaceRoot."),
    ];
}

/// <summary>A profile after merging the user's settings with the detected shells.</summary>
public sealed record TerminalProfile(string Name, string Path, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string?>? Env = null, string? Icon = null);
