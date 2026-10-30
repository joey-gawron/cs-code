using System.Text.Json;
using CsCode.Core.Platform;
using CsCode.Vim;

namespace CsCode.App.Vim;

/// <summary>Who gets a key: the Vim engine, or the shell (its key bindings, then the focused editor).</summary>
public enum VimKeyOwner { Vim, App }

/// <summary>What the editor has open at the caret; in Insert mode it decides who gets Tab, Enter, the arrows and Esc.</summary>
[Flags]
public enum EditorPopups { None = 0, Completion = 1, SignatureHelp = 2, Snippet = 4 }

/// <summary>
/// Ownership of the keys that arrive as key presses (characters arrive as text input and always go to Vim), resolved the way VSCodeVim resolves
/// them: Vim owns its Ctrl keys in Normal, Visual and Operator-pending mode, application chords (Ctrl+Shift+…, Alt+…, F-keys, Ctrl+S…) and
/// the clipboard keys Ctrl+C, Ctrl+Insert, Shift+Insert and Shift+Delete keep working everywhere, Insert mode leaves the editor's IntelliSense, snippet and editing keys alone, and <c>vim.handleKeys</c> overrides any key.
/// Pure and allocation-free per key (the handleKeys table is parsed when the setting changes).
/// </summary>
public static class VimKeyPolicy
{
    /// <summary>Ctrl keys Vim owns in Normal, Visual and Operator-pending mode (VSCodeVim's defaults).</summary>
    public const string NormalCtrlKeys = "dufbeyroivwaxnp[";

    /// <summary>Ctrl keys Vim owns in Insert and Replace mode. Ctrl+P stays Quick Open (outside the completion list); Ctrl+V/C/X/A/Z/S/F are the editor's.</summary>
    public const string InsertCtrlKeys = "[wuhtdron";

    /// <summary>Command-line mode: every Ctrl+letter goes to the command line except these application keys.</summary>
    public const string CommandLineAppCtrlKeys = "sq";

    public static VimKeyOwner Decide(in KeyStroke key, VimMode mode, EditorPopups popups, IReadOnlyDictionary<KeyStroke, bool>? handleKeys)
    {
        if (handleKeys is not null && handleKeys.TryGetValue(Normalize(key), out var vim)) return vim ? VimKeyOwner.Vim : VimKeyOwner.App;
        if (key.Meta || key.Alt) return VimKeyOwner.App;                               // Alt chords (menus, mnemonics), Win/Cmd
        if (key.Key.Length > 1 && key.Key[0] == 'f' && char.IsAsciiDigit(key.Key[1])) return VimKeyOwner.App; // F-keys (debugging, navigation)
        var insert = mode is VimMode.Insert or VimMode.Replace;

        if (key.Ctrl)
        {
            if (key.Shift) return VimKeyOwner.App;                                         // Ctrl+Shift+P, Ctrl+Shift+F ...
            if (key.Key.Length != 1) return VimKeyOwner.App;                               // Ctrl+Space (suggest), Ctrl+Enter, Ctrl+Backspace ...
            var c = key.Key[0];
            if (insert)
            {
                // with the completion list open, Ctrl+N / Ctrl+P move through it (the integration does that itself)
                if (c is 'n' or 'p' && (popups & EditorPopups.Completion) != 0) return VimKeyOwner.Vim;
                return InsertCtrlKeys.Contains(c) ? VimKeyOwner.Vim : VimKeyOwner.App;
            }
            if (mode == VimMode.CommandLine) return CommandLineAppCtrlKeys.Contains(c) ? VimKeyOwner.App : VimKeyOwner.Vim;
            return NormalCtrlKeys.Contains(c) ? VimKeyOwner.Vim : VimKeyOwner.App;
        }

        // Shift+Insert / Shift+Delete are the editor's paste and cut (the shell's clipboard keys); Vim's command line keeps them
        if (key.Shift && key.Key is "insert" or "delete" && mode != VimMode.CommandLine) return VimKeyOwner.App;

        if (insert)
        {
            var completion = (popups & EditorPopups.Completion) != 0;
            switch (key.Key)
            {
                case "tab" when completion || (popups & EditorPopups.Snippet) != 0: return VimKeyOwner.App;   // accept / next placeholder
                case "enter" when completion: return VimKeyOwner.App;                                         // accept
                case "up" or "down" or "pageup" or "pagedown" when completion: return VimKeyOwner.App;
                case "up" or "down" when (popups & EditorPopups.SignatureHelp) != 0 && !key.Shift: return VimKeyOwner.App; // overloads
                case "escape" when completion || (popups & EditorPopups.SignatureHelp) != 0: return VimKeyOwner.App; // closes the popup first
            }
        }
        return VimKeyOwner.Vim; // Esc, Enter, Tab, Backspace, Delete, arrows, Home/End, PageUp/PageDown: Vim keys in every mode
    }

    /// <summary>Text keys (no Ctrl/Alt/Meta, a printable key or Space) arrive as text input: the key press itself is left alone.</summary>
    public static bool IsTextKey(in KeyStroke key) =>
        !key.Ctrl && !key.Alt && !key.Meta && (key.Key.Length == 1 || key.Key == "space" || key.Key.StartsWith("numpad", StringComparison.Ordinal) && key.Key.Length == 7);

    /// <summary>Lookup key for <c>vim.handleKeys</c>: letters lower-case, Shift kept only where it is a separate key (Shift+Tab, Ctrl+Shift+x).</summary>
    public static KeyStroke Normalize(in KeyStroke key) => key with { Key = key.Key.ToLowerInvariant() };

    /// <summary>
    /// Parses <c>vim.handleKeys</c> (VSCodeVim format: <c>{"&lt;C-d&gt;": true, "&lt;C-c&gt;": false}</c>) into key strokes. Entries that are not a single key
    /// press with a key name this shell knows are ignored (characters are governed by the engine's own handling of the setting).
    /// </summary>
    public static IReadOnlyDictionary<KeyStroke, bool> ParseHandleKeys(JsonElement? setting)
    {
        var map = new Dictionary<KeyStroke, bool>();
        if (setting is not { ValueKind: JsonValueKind.Object } obj) return map;
        foreach (var p in obj.EnumerateObject())
        {
            if (p.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) continue;
            if (TryParseKey(p.Name, out var stroke)) map[Normalize(stroke)] = p.Value.GetBoolean();
        }
        return map;
    }

    /// <summary>One key in Vim notation (<c>&lt;C-d&gt;</c>, <c>&lt;C-S-p&gt;</c>, <c>&lt;A-x&gt;</c>, <c>&lt;D-c&gt;</c>, <c>&lt;Esc&gt;</c>, <c>&lt;S-Tab&gt;</c>, <c>&lt;F5&gt;</c>) as a key press.</summary>
    public static bool TryParseKey(string notation, out KeyStroke stroke)
    {
        stroke = default;
        if (notation.Length < 3 || notation[0] != '<' || notation[^1] != '>') return false;
        var inner = notation[1..^1];
        bool ctrl = false, shift = false, alt = false, meta = false;
        while (inner.Length > 2 && inner[1] == '-')
        {
            switch (char.ToUpperInvariant(inner[0]))
            {
                case 'C': ctrl = true; break;
                case 'S': shift = true; break;
                case 'A' or 'M': alt = true; break;
                case 'D': meta = true; break;
                default: return false;
            }
            inner = inner[2..];
        }
        string? name = inner.Length == 1 ? inner.ToLowerInvariant() : inner.ToLowerInvariant() switch
        {
            "esc" => "escape", "tab" => "tab", "cr" or "enter" or "return" => "enter", "bs" => "backspace", "del" => "delete",
            "space" => "space", "up" => "up", "down" => "down", "left" => "left", "right" => "right", "home" => "home", "end" => "end",
            "pageup" => "pageup", "pagedown" => "pagedown", "insert" => "insert", "lt" => ",",
            var f when f.Length is 2 or 3 && f[0] == 'f' && int.TryParse(f[1..], out var n) && n is >= 1 and <= 24 => "f" + n,
            _ => null,
        };
        if (name is null) return false;
        if (name.Length == 1 && char.IsAsciiLetterUpper(inner[0]) && !ctrl && !alt && !meta) shift = true; // <S-x> == X is not a key press here
        stroke = new KeyStroke(name, ctrl, shift, alt, meta);
        return ctrl || alt || meta || name.Length > 1; // plain characters arrive as text input: not a key press to decide on
    }
}
