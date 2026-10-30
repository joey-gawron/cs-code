using System.Buffers;
using System.Text;
using System.Text.Json;
using CsCode.Core.Platform;

namespace CsCode.Macros;

/// <summary>The parsed contents of macros.json: the macros that are valid and one message per problem (never fatal).</summary>
public sealed record MacroFile(IReadOnlyList<Macro> Macros, IReadOnlyList<string> Errors)
{
    public static readonly MacroFile Empty = new([], []);
}

/// <summary>
/// Reads and writes macros (JSONC: comments and trailing commas allowed). The file format, also used for the persisted last macro:
/// <code>
/// { "macros": [ { "name": "comment-line", "description": "...", "key": "ctrl+alt+m",
///                 "steps": [ { "key": "home" }, { "text": "// " }, { "command": "editor.action.formatDocument", "args": [ ... ] }, { "pause": 200 } ] } ] }
/// </code>
/// A step is an object with exactly one of <c>key</c> (one key press, VS Code syntax), <c>text</c>, <c>command</c> (with optional <c>args</c>: an
/// array of positional arguments, any other value is the single argument) or <c>pause</c> (milliseconds). A macro with a bad step is skipped
/// as a whole (a partial macro would do something else than recorded), and the problem is reported.
/// Pure and thread-safe; callers run it off the UI thread (it is JSON work).
/// </summary>
public static class MacroJson
{
    /// <summary>Most macros one file may hold (the rest are reported and ignored).</summary>
    public const int MaxMacros = 1000;
    /// <summary>Largest macros.json that is read (bytes).</summary>
    public const int MaxFileBytes = 16 * 1024 * 1024;

    private static readonly JsonDocumentOptions s_documentOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 64 };

    // ------------------------------------------------------------------------------------------------------------ reading

    public static MacroFile ParseFile(string text, int maxStepsPerMacro = MacroSettings.DefaultMaxRecordedSteps)
    {
        if (string.IsNullOrWhiteSpace(text)) return MacroFile.Empty;
        if (Encoding.UTF8.GetByteCount(text) > MaxFileBytes) return new([], [$"{MacroCommands.FileName} is larger than {MaxFileBytes / (1024 * 1024)} MB and was not read."]);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text, s_documentOptions); }
        catch (JsonException ex) { return new([], [$"{MacroCommands.FileName} is not valid JSON: {ex.Message}"]); }
        using (doc)
        {
            var errors = new List<string>();
            var macros = new List<Macro>();
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new([], [$"{MacroCommands.FileName} must contain an object with a \"macros\" array."]);
            if (!root.TryGetProperty("macros", out var list)) return MacroFile.Empty;
            if (list.ValueKind != JsonValueKind.Array) return new([], ["\"macros\" must be an array."]);
            var index = 0;
            var names = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var item in list.EnumerateArray())
            {
                index++;
                if (macros.Count >= MaxMacros)
                {
                    errors.Add($"More than {MaxMacros} macros: the rest are ignored.");
                    break;
                }
                var macro = ParseMacro(item, index, maxStepsPerMacro, errors, requireName: true);
                if (macro is null) continue;
                if (names.TryGetValue(macro.Name, out var at))
                {
                    errors.Add($"Macro {index}: the name '{macro.Name}' is also used by macro {at + 1}; the later one is used.");
                    macros[at] = macro;
                    continue;
                }
                names[macro.Name] = macros.Count;
                macros.Add(macro);
            }
            return new(macros, errors);
        }
    }

    /// <summary>Parses one macro object (<paramref name="requireName"/> false: the unnamed last macro). Null when invalid (reported).</summary>
    public static Macro? ParseMacro(JsonElement item, int index, int maxSteps, List<string> errors, bool requireName)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"Macro {index}: expected an object.");
            return null;
        }
        var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
        var label = name.Length > 0 ? $"Macro '{name}'" : $"Macro {index}";
        if (requireName && !MacroNames.IsValid(name))
        {
            errors.Add(name.Length == 0 ? $"{label}: \"name\" is missing." : $"{label}: invalid name. {MacroNames.Rule}");
            return null;
        }
        string? description = null, key = null;
        if (item.TryGetProperty("description", out var d))
        {
            if (d.ValueKind == JsonValueKind.String) description = d.GetString();
            else if (d.ValueKind != JsonValueKind.Null) errors.Add($"{label}: \"description\" must be a string (ignored).");
        }
        if (item.TryGetProperty("key", out var k))
        {
            if (k.ValueKind == JsonValueKind.String && k.GetString() is { Length: > 0 } keyText)
            {
                try
                {
                    KeyStrokeParser.ParseChord(keyText);
                    key = keyText;
                }
                catch (FormatException ex) { errors.Add($"{label}: \"key\" {ex.Message} (the macro is loaded without a key binding)."); }
            }
            else if (k.ValueKind != JsonValueKind.Null) errors.Add($"{label}: \"key\" must be a key binding such as \"ctrl+alt+m\" (ignored).");
        }
        if (!item.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"{label}: \"steps\" must be an array.");
            return null;
        }
        var parsed = new List<MacroStep>(steps.GetArrayLength());
        var i = 0;
        foreach (var s in steps.EnumerateArray())
        {
            i++;
            if (parsed.Count >= maxSteps)
            {
                errors.Add($"{label}: more than {maxSteps} steps ({MacroSettings.MaxRecordedSteps}); the macro is not loaded.");
                return null;
            }
            if (ParseStep(s, out var error) is { } step) parsed.Add(step);
            else
            {
                errors.Add($"{label}, step {i}: {error} The macro is not loaded.");
                return null;
            }
        }
        return new Macro(name, parsed) { Description = description, Key = key };
    }

    /// <summary>One step object; null with a reason when it is not a valid step.</summary>
    public static MacroStep? ParseStep(JsonElement s, out string? error)
    {
        error = null;
        if (s.ValueKind != JsonValueKind.Object)
        {
            error = "a step must be an object such as {\"key\": \"home\"}, {\"text\": \"abc\"}, {\"command\": \"id\"} or {\"pause\": 100}.";
            return null;
        }
        string? kind = null;
        JsonElement value = default, args = default;
        var hasArgs = false;
        foreach (var p in s.EnumerateObject())
        {
            switch (p.Name)
            {
                case "key" or "text" or "command" or "pause":
                    if (kind is not null)
                    {
                        error = $"a step has one of key, text, command or pause, not both \"{kind}\" and \"{p.Name}\".";
                        return null;
                    }
                    kind = p.Name;
                    value = p.Value;
                    break;
                case "args":
                    hasArgs = true;
                    args = p.Value;
                    break;
                default:
                    error = $"unknown property \"{p.Name}\".";
                    return null;
            }
        }
        if (hasArgs && kind != "command")
        {
            error = "\"args\" belongs to a command step.";
            return null;
        }
        switch (kind)
        {
            case "key":
                if (value.ValueKind != JsonValueKind.String) break;
                try
                {
                    var chord = KeyStrokeParser.ParseChord(value.GetString() ?? "");
                    if (chord.Count != 1)
                    {
                        error = $"\"{value.GetString()}\" is a chord; a key step is one key press (use a command step for what the chord runs).";
                        return null;
                    }
                    return new KeyStep(chord[0]);
                }
                catch (FormatException ex)
                {
                    error = ex.Message + ".";
                    return null;
                }
            case "text":
                if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text) return new TextStep(text);
                break;
            case "command":
                if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } id) break;
                if (!hasArgs || args.ValueKind == JsonValueKind.Undefined) return new CommandStep(id);
                return new CommandStep(id, args.ValueKind == JsonValueKind.Array ? args.EnumerateArray().Select(a => a.Clone()).ToArray() : [args.Clone()]);
            case "pause":
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var ms) && ms is >= 0 and <= PauseStep.MaxMilliseconds) return new PauseStep(ms);
                error = $"\"pause\" is a number of milliseconds from 0 to {PauseStep.MaxMilliseconds}.";
                return null;
            default:
                error = "a step needs one of \"key\", \"text\", \"command\" or \"pause\".";
                return null;
        }
        error = $"\"{kind}\" needs a non-empty string.";
        return null;
    }

    // ------------------------------------------------------------------------------------------------------------ writing

    /// <summary>One macro as an indented JSON object (steps one per line), the first line unindented, the rest indented by <paramref name="indent"/>.</summary>
    public static string FormatMacro(Macro macro, string indent = "    ", string unit = "  ", string newline = "\n")
    {
        var sb = new StringBuilder();
        sb.Append('{').Append(newline);
        var inner = indent + unit;
        var fields = new List<string>(4) { $"\"name\": {Quote(macro.Name)}" };
        if (macro.Description is { } description) fields.Add($"\"description\": {Quote(description)}");
        if (macro.Key is { } key) fields.Add($"\"key\": {Quote(key)}");
        foreach (var f in fields) sb.Append(inner).Append(f).Append(',').Append(newline);
        if (macro.Steps.Count == 0) sb.Append(inner).Append("\"steps\": []").Append(newline);
        else
        {
            sb.Append(inner).Append("\"steps\": [").Append(newline);
            for (var i = 0; i < macro.Steps.Count; i++)
            {
                sb.Append(inner).Append(unit).Append(FormatStep(macro.Steps[i]));
                if (i < macro.Steps.Count - 1) sb.Append(',');
                sb.Append(newline);
            }
            sb.Append(inner).Append(']').Append(newline);
        }
        sb.Append(indent).Append('}');
        return sb.ToString();
    }

    /// <summary>One step on one line: <c>{ "key": "ctrl+left" }</c>.</summary>
    public static string FormatStep(MacroStep step) => step switch
    {
        KeyStep k => $"{{ \"key\": {Quote(k.Key.ToString())} }}",
        TextStep t => $"{{ \"text\": {Quote(t.Text)} }}",
        CommandStep { Args.Count: 0 } c => $"{{ \"command\": {Quote(c.Command)} }}",
        CommandStep c => $"{{ \"command\": {Quote(c.Command)}, \"args\": {Compact(c.Args)} }}",
        PauseStep p => $"{{ \"pause\": {p.Milliseconds} }}",
        _ => throw new ArgumentOutOfRangeException(nameof(step)),
    };

    /// <summary>A whole file with the given macros (used when macros.json does not exist yet).</summary>
    public static string FormatFile(IReadOnlyList<Macro> macros, string newline = "\n")
    {
        var sb = new StringBuilder();
        sb.Append(FileHeader.Replace("\n", newline, StringComparison.Ordinal));
        sb.Append('{').Append(newline).Append("  \"macros\": [");
        if (macros.Count == 0) sb.Append(']').Append(newline);
        else
        {
            sb.Append(newline);
            for (var i = 0; i < macros.Count; i++)
            {
                sb.Append("    ").Append(FormatMacro(macros[i], "    ", "  ", newline));
                if (i < macros.Count - 1) sb.Append(',');
                sb.Append(newline);
            }
            sb.Append("  ]").Append(newline);
        }
        sb.Append('}').Append(newline);
        return sb.ToString();
    }

    /// <summary>The guide at the top of a new macros.json.</summary>
    public const string FileHeader = """
        // CsCode macros (Tools > Macros). Edit freely: the file is reloaded when it is saved. Comments are allowed.
        // Each macro runs as the command "macro.run.<name>" (palette: "Macros: Run <name>"); "key" binds it (keybindings.json works too).
        // Steps: { "key": "shift+end" }    a key press the editor handles (arrows, Home/End, Backspace, Enter, Tab...)
        //        { "text": "abc" }         typed text (typed character by character, with auto-closing and IntelliSense triggers)
        //        { "command": "editor.action.commentLine", "args": [ ... ] }    a command (what a key binding, menu or palette pick runs)
        //        { "pause": 250 }          wait (milliseconds)
        // A recorded search is { "command": "macro.find", "args": [ { "text": "foo", "regex": false, "matchCase": false, "wholeWord": false } ] }.

        """;

    public static string Quote(string s) => JsonSerializer.Serialize(s, s_relaxed);

    private static string Compact(IReadOnlyList<JsonElement> args)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = s_relaxed.Encoder, Indented = false }))
        {
            w.WriteStartArray();
            foreach (var a in args) a.WriteTo(w);
            w.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // Keep the file readable: non-ASCII text, quotes and '<' stay as typed (the default encoder escapes them as \uXXXX).
    private static readonly JsonSerializerOptions s_relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // ------------------------------------------------------------------------------------------------------------ the persisted last macro

    /// <summary>The steps of a macro as a compact JSON array (the persisted last macro).</summary>
    public static string FormatSteps(IReadOnlyList<MacroStep> steps)
    {
        var sb = new StringBuilder("[");
        for (var i = 0; i < steps.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(FormatStep(steps[i]));
        }
        return sb.Append(']').ToString();
    }

    /// <summary>Parses <see cref="FormatSteps"/> output; null when it is not a valid step list.</summary>
    public static IReadOnlyList<MacroStep>? ParseSteps(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, s_documentOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var steps = new List<MacroStep>();
            foreach (var s in doc.RootElement.EnumerateArray())
            {
                if (ParseStep(s, out _) is not { } step) return null;
                steps.Add(step);
            }
            return steps;
        }
        catch (JsonException) { return null; }
    }
}
