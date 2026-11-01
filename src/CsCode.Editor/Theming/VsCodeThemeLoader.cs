using System.Text.Json;
using CsCode.Core.Platform;
using CsCode.Core.Theming;

namespace CsCode.Editor.Theming;

/// <summary>Reads theme files for <see cref="VsCodeThemeLoader"/>: the user's disk (through IFileSystem) or the embedded built-ins.</summary>
public interface IThemeFileReader
{
    /// <summary>Resolves <paramref name="relativePath"/> against the file at <paramref name="fromPath"/>; null when it does not exist.</summary>
    ValueTask<(string Path, string Text)?> ReadRelativeAsync(string fromPath, string relativePath, CancellationToken ct);
}

/// <summary>
/// Parses VS Code colour-theme JSON (JSONC: comments and trailing commas) into a Core <see cref="ColorTheme"/>:
/// <c>name</c>, <c>type</c>, <c>include</c> chains (the included theme first, then this one overrides), <c>colors</c> (workbench/editor keys),
/// <c>tokenColors</c> (scope string with commas, or an array; <c>settings</c> with foreground/background/fontStyle) and
/// <c>semanticTokenColors</c> and <c>semanticHighlighting</c>. The legacy top-level <c>settings</c> array is treated as <c>tokenColors</c>. A <c>tokenColors</c> that is a path to a
/// <c>.tmTheme</c> plist is not supported (ignored); values that are not valid hex colours are skipped.
/// </summary>
public static class VsCodeThemeLoader
{
    private const int MaxIncludeDepth = 8;
    private static readonly JsonDocumentOptions JsonOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private sealed class Raw
    {
        public string? Name;
        public ThemeKind? Kind;
        public readonly Dictionary<string, Rgba> Colors = new(StringComparer.Ordinal);
        public readonly List<TokenColorRule> TokenColors = [];
        public readonly Dictionary<string, TokenColorRule> Semantic = new(StringComparer.Ordinal);
        public bool? SemanticHighlighting;
    }

    /// <param name="id">Theme id (contribution id / file name).</param>
    /// <param name="label">Display name; null → the theme's own <c>name</c>, then the id.</param>
    /// <param name="uiTheme">Contribution's <c>vs | vs-dark | hc-black | hc-light</c>; overrides the file's <c>type</c> when given.</param>
    public static async ValueTask<ColorTheme> LoadAsync(
        string id, string? label, string? uiTheme, string text, string sourcePath, IThemeFileReader reader, CancellationToken ct = default)
    {
        var raw = await ParseAsync(text, sourcePath, reader, [sourcePath], 0, ct).ConfigureAwait(false);
        var kind = KindFromUiTheme(uiTheme) ?? raw.Kind ?? InferKind(raw.Colors);
        return new ColorTheme(id, label ?? raw.Name ?? id, kind, raw.Colors, raw.TokenColors, raw.Semantic.Count == 0 ? null : raw.Semantic) { SemanticHighlighting = raw.SemanticHighlighting == true };
    }

    /// <summary>Synchronous parse of a self-contained theme (no <c>include</c>), for tests and tools.</summary>
    public static ColorTheme Parse(string id, string? label, string? uiTheme, string text)
    {
        var raw = ParseOne(text, out var include);
        if (include is not null) throw new InvalidOperationException("Theme has an include; use LoadAsync with a reader.");
        var kind = KindFromUiTheme(uiTheme) ?? raw.Kind ?? InferKind(raw.Colors);
        return new ColorTheme(id, label ?? raw.Name ?? id, kind, raw.Colors, raw.TokenColors, raw.Semantic.Count == 0 ? null : raw.Semantic) { SemanticHighlighting = raw.SemanticHighlighting == true };
    }

    /// <summary>
    /// Fully synchronous load for in-memory sources (the embedded built-in themes), so startup never has to wait on a task.
    /// <paramref name="readRelative"/> maps (fromPath, relativePath) to the included file, or null when missing.
    /// </summary>
    public static ColorTheme LoadSync(
        string id, string? label, string? uiTheme, string text, string sourcePath, Func<string, string, (string Path, string Text)?> readRelative)
    {
        var raw = ParseSync(text, sourcePath, readRelative, [sourcePath], 0);
        var kind = KindFromUiTheme(uiTheme) ?? raw.Kind ?? InferKind(raw.Colors);
        return new ColorTheme(id, label ?? raw.Name ?? id, kind, raw.Colors, raw.TokenColors, raw.Semantic.Count == 0 ? null : raw.Semantic) { SemanticHighlighting = raw.SemanticHighlighting == true };
    }

    private static Raw ParseSync(string text, string path, Func<string, string, (string Path, string Text)?> readRelative, HashSet<string> visiting, int depth)
    {
        var own = ParseOne(text, out var include);
        if (string.IsNullOrEmpty(include)) return own;
        if (depth >= MaxIncludeDepth) throw new InvalidOperationException($"Theme include chain deeper than {MaxIncludeDepth} at '{path}'.");
        var included = readRelative(path, include) ?? throw new FileNotFoundException($"Theme '{path}' includes '{include}', which was not found.");
        if (!visiting.Add(included.Path)) throw new InvalidOperationException($"Theme include cycle through '{included.Path}'.");
        return Merge(ParseSync(included.Text, included.Path, readRelative, visiting, depth + 1), own);
    }

    private static async ValueTask<Raw> ParseAsync(string text, string path, IThemeFileReader reader, HashSet<string> visiting, int depth, CancellationToken ct)
    {
        var own = ParseOne(text, out var include);
        if (string.IsNullOrEmpty(include)) return own;
        if (depth >= MaxIncludeDepth) throw new InvalidOperationException($"Theme include chain deeper than {MaxIncludeDepth} at '{path}'.");
        var included = await reader.ReadRelativeAsync(path, include, ct).ConfigureAwait(false)
                       ?? throw new FileNotFoundException($"Theme '{path}' includes '{include}', which was not found.");
        if (!visiting.Add(included.Path)) throw new InvalidOperationException($"Theme include cycle through '{included.Path}'.");
        var parent = await ParseAsync(included.Text, included.Path, reader, visiting, depth + 1, ct).ConfigureAwait(false);
        return Merge(parent, own);
    }

    /// <summary>The including theme overrides: its colours win key by key; its token rules come AFTER the included ones (later rules win ties).</summary>
    private static Raw Merge(Raw parent, Raw own)
    {
        var merged = new Raw { Name = own.Name ?? parent.Name, Kind = own.Kind ?? parent.Kind, SemanticHighlighting = own.SemanticHighlighting ?? parent.SemanticHighlighting };
        foreach (var (k, v) in parent.Colors) merged.Colors[k] = v;
        foreach (var (k, v) in own.Colors) merged.Colors[k] = v;
        merged.TokenColors.AddRange(parent.TokenColors);
        merged.TokenColors.AddRange(own.TokenColors);
        foreach (var (k, v) in parent.Semantic) merged.Semantic[k] = v;
        foreach (var (k, v) in own.Semantic) merged.Semantic[k] = v;
        return merged;
    }

    private static Raw ParseOne(string text, out string? include)
    {
        include = null;
        var raw = new Raw();
        using var doc = JsonDocument.Parse(text, JsonOptions);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("A colour theme must be a JSON object.");

        raw.Name = Str(root, "name");
        raw.Kind = Str(root, "type")?.ToLowerInvariant() switch
        {
            "dark" => ThemeKind.Dark,
            "light" => ThemeKind.Light,
            "hc" or "hc-black" => ThemeKind.HighContrastDark,
            "hc-light" => ThemeKind.HighContrastLight,
            _ => null,
        };
        include = Str(root, "include");
        if (root.TryGetProperty("semanticHighlighting", out var semanticOn) && semanticOn.ValueKind is JsonValueKind.True or JsonValueKind.False)
            raw.SemanticHighlighting = semanticOn.ValueKind == JsonValueKind.True;

        if (root.TryGetProperty("colors", out var colors) && colors.ValueKind == JsonValueKind.Object)
            foreach (var p in colors.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String && Rgba.TryParse(p.Value.GetString(), out var c)) raw.Colors[p.Name] = c;

        // "tokenColors" (current) or the legacy "settings" array. A string value is a .tmTheme path: unsupported, ignored.
        JsonElement rules = default;
        var hasRules = (root.TryGetProperty("tokenColors", out rules) && rules.ValueKind == JsonValueKind.Array)
                       || (root.TryGetProperty("settings", out rules) && rules.ValueKind == JsonValueKind.Array);
        if (hasRules)
            foreach (var rule in rules.EnumerateArray())
                if (ParseRule(rule) is { } r) raw.TokenColors.Add(r);

        if (root.TryGetProperty("semanticTokenColors", out var sem) && sem.ValueKind == JsonValueKind.Object)
            foreach (var p in sem.EnumerateObject())
                if (ParseSemantic(p.Name, p.Value) is { } sr) raw.Semantic[p.Name] = sr;
        return raw;
    }

    private static TokenColorRule? ParseRule(JsonElement rule)
    {
        if (rule.ValueKind != JsonValueKind.Object) return null;
        if (!rule.TryGetProperty("settings", out var s) || s.ValueKind != JsonValueKind.Object) return null;
        var scopes = new List<string>();
        if (rule.TryGetProperty("scope", out var scope))
        {
            if (scope.ValueKind == JsonValueKind.String) Split(scope.GetString(), scopes);
            else if (scope.ValueKind == JsonValueKind.Array)
                foreach (var e in scope.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String) Split(e.GetString(), scopes);
        }
        var fg = HexOrNull(s, "foreground");
        var bg = HexOrNull(s, "background");
        var fontStyle = s.TryGetProperty("fontStyle", out var fs) && fs.ValueKind == JsonValueKind.String ? Normalise(fs.GetString()!) : null;
        if (fg is null && bg is null && fontStyle is null) return null; // a rule that sets nothing
        // No scope = the theme's global default (foreground/background of ordinary text): kept as an empty scope list.
        return new TokenColorRule(scopes, fg, bg, fontStyle);
    }

    private static TokenColorRule? ParseSemantic(string selector, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
            return Rgba.TryParse(value.GetString(), out _) ? new TokenColorRule([selector], value.GetString(), null, null) : null;
        if (value.ValueKind != JsonValueKind.Object) return null;
        var fg = HexOrNull(value, "foreground");
        string? style = null;
        if (value.TryGetProperty("fontStyle", out var fs) && fs.ValueKind == JsonValueKind.String) style = Normalise(fs.GetString()!);
        else
        {
            var parts = new List<string>();
            foreach (var flag in new[] { "italic", "bold", "underline", "strikethrough" })
                if (value.TryGetProperty(flag, out var b) && b.ValueKind == JsonValueKind.True) parts.Add(flag);
            if (parts.Count > 0) style = string.Join(' ', parts);
        }
        return fg is null && style is null ? null : new TokenColorRule([selector], fg, null, style);
    }

    /// <summary>"a, b ,c" → ["a","b","c"] (VS Code allows comma-separated selectors inside one string).</summary>
    private static void Split(string? s, List<string> into)
    {
        if (s is null) return;
        foreach (var part in s.Split(','))
        {
            var t = part.Trim();
            if (t.Length > 0) into.Add(t);
        }
    }

    private static string Normalise(string fontStyle) => string.Join(' ', fontStyle.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries));

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? HexOrNull(JsonElement obj, string name)
    {
        var s = Str(obj, name);
        return s is not null && Rgba.TryParse(s, out _) ? s : null;
    }

    /// <summary>
    /// A theme file with neither <c>type</c> nor a contribution <c>uiTheme</c> (VS Code's own light_vs.json has neither: the extension
    /// manifest carries it): decide from the editor background's luminance.
    /// </summary>
    private static ThemeKind InferKind(IReadOnlyDictionary<string, Rgba> colors)
    {
        if (!colors.TryGetValue("editor.background", out var bg)) return ThemeKind.Dark;
        var luminance = (0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B) / 255.0;
        return luminance > 0.5 ? ThemeKind.Light : ThemeKind.Dark;
    }

    public static ThemeKind? KindFromUiTheme(string? uiTheme) => uiTheme switch
    {
        "vs" => ThemeKind.Light,
        "vs-dark" => ThemeKind.Dark,
        "hc-black" => ThemeKind.HighContrastDark,
        "hc-light" => ThemeKind.HighContrastLight,
        _ => null,
    };

    /// <summary>Reads files next to each other through <see cref="IFileSystem"/> (user theme folders).</summary>
    public sealed class FileSystemReader(IFileSystem fs) : IThemeFileReader
    {
        public async ValueTask<(string Path, string Text)?> ReadRelativeAsync(string fromPath, string relativePath, CancellationToken ct)
        {
            var dir = Path.GetDirectoryName(fromPath) ?? string.Empty;
            var full = Path.GetFullPath(Path.Combine(dir, relativePath));
            var uri = new Uri(full);
            if (await fs.StatAsync(uri, ct).ConfigureAwait(false) is null) return null;
            return (full, System.Text.Encoding.UTF8.GetString(await fs.ReadFileAsync(uri, ct).ConfigureAwait(false)));
        }
    }
}
