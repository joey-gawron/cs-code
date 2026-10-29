using System.Text.Json;
using CsCode.Core.Lifecycle;

namespace CsCode.Core.Platform;

/// <summary>
/// One key press, UI-toolkit independent. <see cref="Key"/> uses VS Code key names, lower-case: a–z, 0–9, f1–f19,
/// up down left right pageup pagedown home end insert delete backspace tab enter escape space, punctuation as the
/// character itself ("[", "]", ";", "'", ",", ".", "/", "\\", "-", "=", "`"), numpad0–numpad9, numpad_add, …
/// The shell maps toolkit key events to this; <see cref="Meta"/> = Cmd on macOS / Win key elsewhere.
/// </summary>
public readonly record struct KeyStroke(string Key, bool Ctrl = false, bool Shift = false, bool Alt = false, bool Meta = false)
{
    public override string ToString() =>
        string.Join('+', new[] { Ctrl ? "ctrl" : null, Shift ? "shift" : null, Alt ? "alt" : null, Meta ? "meta" : null, Key }.Where(s => s is not null));
}

public enum KeyBindingSource { Default = 0, Extension = 1, User = 2 }

/// <param name="Chord">One stroke, or several for chords such as "ctrl+k ctrl+c".</param>
/// <param name="IsNegative">A user/extension entry of the form {"command":"-foo"} that removes a default binding.</param>
public sealed record KeyBinding(string Command, IReadOnlyList<KeyStroke> Chord, string? When, JsonElement? Args, KeyBindingSource Source, bool IsNegative = false);

public enum KeyResolution
{
    /// <summary>No binding matches; the key should be handled normally (typed into the editor, etc.).</summary>
    None,
    /// <summary>The stroke is a valid prefix of one or more chords; swallow it and wait for the next stroke.</summary>
    Pending,
    /// <summary>A binding matched; execute <see cref="KeyResolveResult.Binding"/>.</summary>
    Matched,
}

public sealed record KeyResolveResult(KeyResolution Kind, KeyBinding? Binding = null);

/// <summary>
/// Keybinding resolution with chord state, `when`-clause filtering (via <see cref="IContextKeyService"/>), and layered sources
/// (user &gt; extension &gt; default; later wins; negative bindings remove). Presets: "visualstudio" (default) and "vscode".
/// </summary>
public interface IKeybindingService
{
    /// <summary>Stateful: feeds one stroke into the chord matcher.</summary>
    KeyResolveResult Resolve(KeyStroke stroke);
    void ResetChord();
    IReadOnlyList<KeyStroke> PendingChord { get; }

    IReadOnlyList<KeyBinding> All { get; }
    IReadOnlyList<KeyBinding> GetBindingsFor(string commandId);
    /// <summary>Platform-formatted label of the best binding for menus, e.g. "Ctrl+K, Ctrl+C" or "⌘S"; null if unbound.</summary>
    string? GetLabel(string commandId);

    string ActivePreset { get; }
    ValueTask SetPresetAsync(string preset, CancellationToken ct = default);

    /// <summary>Fires after user keybindings.json, an extension contribution or the preset changes.</summary>
    IEventSource<object?> Changed { get; }
}

public static class KeyStrokeParser
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["esc"] = "escape", ["return"] = "enter", ["pgup"] = "pageup", ["pgdn"] = "pagedown", ["del"] = "delete",
        ["ins"] = "insert", ["bksp"] = "backspace", ["arrowup"] = "up", ["arrowdown"] = "down",
        ["arrowleft"] = "left", ["arrowright"] = "right",
    };

    /// <summary>Parses "ctrl+k ctrl+c", "shift+alt+f5", "cmd+s" (cmd/win/meta/super → Meta). Throws FormatException on garbage.</summary>
    public static IReadOnlyList<KeyStroke> ParseChord(string text)
    {
        var strokes = new List<KeyStroke>();
        foreach (var part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            strokes.Add(ParseStroke(part));
        if (strokes.Count == 0) throw new FormatException("Empty key binding");
        return strokes;
    }

    public static KeyStroke ParseStroke(string text)
    {
        bool ctrl = false, shift = false, alt = false, meta = false;
        string? key = null;
        // A literal '+' key is written "ctrl++"; split carefully.
        var tokens = new List<string>();
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '+' && sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(text[i]);
        }
        if (sb.Length > 0) tokens.Add(sb.ToString());
        foreach (var raw in tokens)
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": ctrl = true; break;
                case "shift": shift = true; break;
                case "alt" or "option" or "opt": alt = true; break;
                case "cmd" or "command" or "win" or "meta" or "super": meta = true; break;
                default:
                    if (key is not null) throw new FormatException($"Two keys in stroke '{text}'");
                    key = Aliases.TryGetValue(raw, out var a) ? a : raw.ToLowerInvariant();
                    break;
            }
        }
        if (key is null) throw new FormatException($"No key in stroke '{text}'");
        return new KeyStroke(key, ctrl, shift, alt, meta);
    }
}
