using System.Text.Json;
using CsCode.Core.Platform;

namespace CsCode.Macros;

/// <summary>Command ids, context keys and settings of the macro module (docs/MACROS.md).</summary>
public static class MacroCommands
{
    /// <summary>Starts recording; while recording, stops it (one key for both).</summary>
    public const string Record = "macro.record";
    /// <summary>Stops recording, or the macro that is playing.</summary>
    public const string Stop = "macro.stop";
    /// <summary>Plays a macro. Argument (optional): <c>"times" | "untilEndOfFile" | "untilSearchFails" | "onSelectedLines"</c> or
    /// <c>{"name": "x", "times": 3, "until": "endOfFile" | "searchFails", "onSelectedLines": true}</c>; what is missing is asked.</summary>
    public const string Play = "macro.play";
    /// <summary>Plays the last recorded macro once (argument: a repeat count).</summary>
    public const string PlayLast = "macro.playLast";
    /// <summary>Plays a saved macro (argument: its name; without one a list is shown).</summary>
    public const string PlayNamed = "macro.playNamed";
    /// <summary>Saves the last recorded macro under a name (argument: the name; without one it is asked).</summary>
    public const string Save = "macro.save";
    /// <summary>Opens macros.json in the editor (created with a short guide when missing).</summary>
    public const string Edit = "macro.edit";
    /// <summary>A recorded search (registered by the shell's editor glue): argument <see cref="MacroFindArgs"/>; fails when nothing more is found.</summary>
    public const string Find = "macro.find";
    /// <summary>Prefix of the command of each saved macro: <c>macro.run.&lt;name&gt;</c>.</summary>
    public const string RunPrefix = "macro.run.";

    public const string Category = "Macros";

    // context keys (menus, palette enablement, key bindings)
    public const string RecordingContextKey = "cscode.macroRecording";
    public const string PlayingContextKey = "cscode.macroPlaying";
    public const string HasLastContextKey = "cscode.macroHasLast";
    /// <summary>While this context key is set (the integrated terminal has the keyboard), recording records nothing: keys, text, searches, commands.</summary>
    public const string RecordingPausedWhen = "terminalFocus";

    // status bar items
    public const string RecordingStatusId = "macros.recording";
    public const string PlayingStatusId = "macros.playing";
    public const string MessageStatusId = "macros.message";

    /// <summary>The file of saved macros under <see cref="IAppPaths.UserDataDir"/>.</summary>
    public const string FileName = "macros.json";

    /// <summary>Whether <paramref name="id"/> is one of this module's commands (never recorded into a macro).</summary>
    public static bool IsMacroCommand(string id) => id.StartsWith("macro.", StringComparison.Ordinal) && id != Find;
}

/// <summary>Settings (registered with <see cref="IConfigurationService"/>, read live).</summary>
public static class MacroSettings
{
    public const string MaxRecordedSteps = "macros.maxRecordedSteps";
    public const string MaxPlaybackSteps = "macros.maxPlaybackSteps";
    public const string MaxPlaybackSeconds = "macros.maxPlaybackSeconds";
    public const string RecordPauses = "macros.recordPauses";

    public const int DefaultMaxRecordedSteps = 100_000;
    public const long DefaultMaxPlaybackSteps = 5_000_000;
    public const int DefaultMaxPlaybackSeconds = 600;

    public static IEnumerable<ConfigurationProperty> Properties =>
    [
        new(MaxRecordedSteps, JsonSerializer.SerializeToElement(DefaultMaxRecordedSteps),
            "Most steps a macro recording keeps (typed text counts one step per run of characters); recording stops when it is full."),
        new(MaxPlaybackSteps, JsonSerializer.SerializeToElement(DefaultMaxPlaybackSteps),
            "Runaway guard: a macro playback (all repetitions and nested macros) stops after this many steps."),
        new(MaxPlaybackSeconds, JsonSerializer.SerializeToElement(DefaultMaxPlaybackSeconds),
            "Runaway guard: a macro playback stops after this many seconds (Esc stops it at any time)."),
        new(RecordPauses, JsonSerializer.SerializeToElement(false),
            "Record the pauses between steps (longer than a quarter of a second, at most 10 s each) and wait for them on playback."),
    ];
}

/// <summary>The argument of <see cref="MacroCommands.Find"/>: what a recorded search looks for.</summary>
public sealed record MacroFindArgs(string Text, bool Regex = false, bool MatchCase = false, bool WholeWord = false, bool Backwards = false)
{
    public JsonElement ToJson() => JsonSerializer.SerializeToElement(this, CoreJson.Options);
}

/// <summary>Thrown by a step that searched and found nothing: the macro stops (the normal end of "Play until search fails").</summary>
public sealed class MacroSearchFailedException(string message) : Exception(message);
