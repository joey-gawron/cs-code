using System.Text.Json;
using System.Text.RegularExpressions;
using CsCode.Core.Languages;

namespace CsCode.Editor.Languages;

/// <summary>An <c>onEnterRules</c> entry including the <c>previousLineText</c> guard the Core record has no slot for.</summary>
public sealed record EnterRuleEx(OnEnterRule Rule, string? PreviousLineText);

/// <summary>Editor-side extras of a language configuration that the Core record has no slot for.</summary>
public sealed record ExtendedLanguageConfiguration(LanguageConfiguration Base, string? AutoCloseBefore, IReadOnlyList<EnterRuleEx>? EnterRules = null)
{
    /// <summary>VS Code's default: auto-close a bracket only when the next char is one of these (or whitespace/end of line).</summary>
    public const string DefaultAutoCloseBefore = ";:.,=}])> \n\t";

    public string EffectiveAutoCloseBefore => AutoCloseBefore ?? DefaultAutoCloseBefore;
}

/// <summary>
/// Parses VS Code <c>language-configuration.json</c> (JSONC: comments and trailing commas allowed) into the Core
/// <see cref="LanguageConfiguration"/>. Regular expressions (JS syntax, plain string or <c>{pattern, flags}</c>) are validated here and
/// dropped when invalid, so consumers never have to guard against a bad pattern in the typing path.
/// </summary>
public static class LanguageConfigurationParser
{
    private static readonly JsonDocumentOptions Options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Default configuration used when a language has none: the bracket/quote pairs every editor expects.</summary>
    public static readonly LanguageConfiguration Default = new()
    {
        Brackets = [("{", "}"), ("[", "]"), ("(", ")")],
        AutoClosingPairs =
        [
            new AutoClosingPair("{", "}"), new AutoClosingPair("[", "]"), new AutoClosingPair("(", ")"),
            new AutoClosingPair("'", "'", ["string", "comment"]), new AutoClosingPair("\"", "\"", ["string"]), new AutoClosingPair("`", "`", ["string", "comment"]),
        ],
        SurroundingPairs = [("{", "}"), ("[", "]"), ("(", ")"), ("'", "'"), ("\"", "\""), ("`", "`")],
    };

    public static LanguageConfiguration Parse(string json) => ParseExtended(json).Base;

    public static bool TryParse(string json, out ExtendedLanguageConfiguration config)
    {
        try
        {
            config = ParseExtended(json);
            return true;
        }
        catch (JsonException)
        {
            config = new ExtendedLanguageConfiguration(new LanguageConfiguration(), null);
            return false;
        }
    }

    /// <exception cref="JsonException">The text is not valid JSON(C).</exception>
    public static ExtendedLanguageConfiguration ParseExtended(string json)
    {
        using var doc = JsonDocument.Parse(json, Options);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return new ExtendedLanguageConfiguration(new LanguageConfiguration(), null);

        string? lineComment = null;
        (string, string)? blockComment = null;
        if (root.TryGetProperty("comments", out var comments) && comments.ValueKind == JsonValueKind.Object)
        {
            lineComment = Str(comments, "lineComment");
            if (comments.TryGetProperty("blockComment", out var bc) && Pair(bc) is { } p) blockComment = p;
        }

        string? foldStart = null, foldEnd = null;
        var offside = false;
        if (root.TryGetProperty("folding", out var folding) && folding.ValueKind == JsonValueKind.Object)
        {
            offside = folding.TryGetProperty("offSide", out var o) && o.ValueKind == JsonValueKind.True;
            if (folding.TryGetProperty("markers", out var m) && m.ValueKind == JsonValueKind.Object)
            {
                foldStart = Rx(m, "start");
                foldEnd = Rx(m, "end");
            }
        }

        string? increase = null, decrease = null, nextLine = null, unIndented = null;
        if (root.TryGetProperty("indentationRules", out var ir) && ir.ValueKind == JsonValueKind.Object)
        {
            increase = Rx(ir, "increaseIndentPattern");
            decrease = Rx(ir, "decreaseIndentPattern");
            nextLine = Rx(ir, "indentNextLinePattern");
            unIndented = Rx(ir, "unIndentedLinePattern");
        }

        var onEnter = new List<OnEnterRule>();
        var onEnterEx = new List<EnterRuleEx>();
        if (root.TryGetProperty("onEnterRules", out var rules) && rules.ValueKind == JsonValueKind.Array)
            foreach (var rule in rules.EnumerateArray())
                if (ParseRule(rule) is { } r)
                {
                    onEnterEx.Add(r);
                    // The Core record cannot express the previousLineText guard; applying such a rule without it would mis-indent, so the
                    // Core list only carries the unguarded rules (the editor itself uses the full list, guards included).
                    if (r.PreviousLineText is null) onEnter.Add(r.Rule);
                }

        var config = new LanguageConfiguration
        {
            LineComment = lineComment,
            BlockComment = blockComment,
            Brackets = Pairs(root, "brackets"),
            AutoClosingPairs = AutoClosing(root),
            SurroundingPairs = Pairs(root, "surroundingPairs"),
            WordPattern = root.TryGetProperty("wordPattern", out var wp) ? RxValue(wp) : null,
            FoldingMarkersStart = foldStart,
            FoldingMarkersEnd = foldEnd,
            IncreaseIndentPattern = increase,
            DecreaseIndentPattern = decrease,
            IndentNextLinePattern = nextLine,
            UnIndentedLinePattern = unIndented,
            OnEnterRules = onEnter,
            OffsideFolding = offside,
        };
        return new ExtendedLanguageConfiguration(config, Str(root, "autoCloseBefore"), onEnterEx);
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static (string, string)? Pair(JsonElement e) =>
        e.ValueKind == JsonValueKind.Array && e.GetArrayLength() >= 2 && e[0].ValueKind == JsonValueKind.String && e[1].ValueKind == JsonValueKind.String
            ? (e[0].GetString()!, e[1].GetString()!)
            : null;

    /// <summary>Pairs written either as <c>["(", ")"]</c> or <c>{ "open": "(", "close": ")" }</c>.</summary>
    private static IReadOnlyList<(string Open, string Close)> Pairs(JsonElement root, string name)
    {
        var list = new List<(string, string)>();
        if (root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var item in arr.EnumerateArray())
            {
                if (Pair(item) is { } p) list.Add(p);
                else if (item.ValueKind == JsonValueKind.Object && Str(item, "open") is { } open && Str(item, "close") is { } close) list.Add((open, close));
            }
        return list;
    }

    private static IReadOnlyList<AutoClosingPair> AutoClosing(JsonElement root)
    {
        var list = new List<AutoClosingPair>();
        if (!root.TryGetProperty("autoClosingPairs", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in arr.EnumerateArray())
        {
            if (Pair(item) is { } p)
            {
                list.Add(new AutoClosingPair(p.Item1, p.Item2));
            }
            else if (item.ValueKind == JsonValueKind.Object && Str(item, "open") is { } open && Str(item, "close") is { } close)
            {
                IReadOnlyList<string>? notIn = null;
                if (item.TryGetProperty("notIn", out var n) && n.ValueKind == JsonValueKind.Array)
                    notIn = n.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray();
                list.Add(new AutoClosingPair(open, close, notIn));
            }
        }
        return list;
    }

    private static EnterRuleEx? ParseRule(JsonElement rule)
    {
        if (rule.ValueKind != JsonValueKind.Object) return null;
        string? previous = null;
        if (rule.TryGetProperty("previousLineText", out var prev))
        {
            previous = RxValue(prev);
            if (previous is null) return null; // a guard we cannot compile: do not apply the rule unguarded
        }
        if (!rule.TryGetProperty("beforeText", out var before) || RxValue(before) is not { } beforeText) return null;
        var afterText = rule.TryGetProperty("afterText", out var after) ? RxValue(after) : null;
        if (!rule.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.Object) return null;
        var kind = Str(action, "indent");
        if (kind is not ("none" or "indent" or "outdent" or "indentOutdent")) return null;
        var remove = action.TryGetProperty("removeText", out var rt) && rt.ValueKind == JsonValueKind.Number ? rt.GetInt32() : 0;
        return new EnterRuleEx(new OnEnterRule(beforeText, afterText, kind, Str(action, "appendText"), remove), previous);
    }

    private static string? Rx(JsonElement obj, string name) => obj.TryGetProperty(name, out var v) ? RxValue(v) : null;

    /// <summary>A regex given as a JS source string or <c>{ pattern, flags }</c>; returns a .NET-compilable pattern (flags inlined) or null.</summary>
    internal static string? RxValue(JsonElement v)
    {
        string? pattern = null, flags = null;
        if (v.ValueKind == JsonValueKind.String) pattern = v.GetString();
        else if (v.ValueKind == JsonValueKind.Object)
        {
            pattern = Str(v, "pattern");
            flags = Str(v, "flags");
        }
        if (string.IsNullOrEmpty(pattern)) return null;
        if (flags is { Length: > 0 })
        {
            var net = new string(flags.Where(c => c is 'i' or 'm' or 's').Distinct().ToArray());
            if (net.Length > 0) pattern = $"(?{net}){pattern}";
        }
        try
        {
            _ = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
            return pattern;
        }
        catch (ArgumentException)
        {
            return null; // JS-only syntax that .NET rejects: drop it rather than fail later
        }
    }
}
