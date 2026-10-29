using System.Globalization;
using System.Text.Json;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CsCode.Core.Platform;

namespace CsCode.Panels.Search.Settings;

public enum SettingKind { Boolean, Integer, Number, String, Enum, Complex }

public sealed record EnumChoice(string Value, string Label, string? Description);

/// <summary>
/// One setting as the UI shows it. Built from <see cref="ConfigurationProperty"/> metadata (default + JSON-schema fragment); the concrete
/// subclass decides the control. All edits go back through <see cref="SettingsViewModel"/> (which owns scope, debouncing and writing).
/// </summary>
public abstract class SettingItemViewModel : ObservableObject
{
    private bool _isModified;
    private string? _error;
    private string? _alsoModifiedIn;

    protected SettingItemViewModel(SettingsViewModel owner, ConfigurationProperty property, string category, string title)
    {
        Owner = owner;
        Property = property;
        Category = category;
        Title = title;
        ResetCommand = new RelayCommand(() => _ = Owner.ResetAsync(this));
        CopyIdCommand = new RelayCommand(() => _ = Owner.CopyIdAsync(this));
        EditInJsonCommand = new RelayCommand(() => _ = Owner.EditInJsonAsync(this));
    }

    public SettingsViewModel Owner { get; }
    public ConfigurationProperty Property { get; }
    public string Key => Property.Key;
    public string Category { get; }
    public string Title { get; }
    public string Description => Property.Description ?? "";
    public abstract SettingKind Kind { get; }
    public JsonElement? Default => Property.Default;

    /// <summary>The value differs from the default in the selected scope (shows the blue bar and enables Reset).</summary>
    public bool IsModified { get => _isModified; internal set => SetProperty(ref _isModified, value); }
    /// <summary>Set when the setting is also customised in the other scope ("Also modified in: Workspace").</summary>
    public string? AlsoModifiedIn { get => _alsoModifiedIn; internal set => SetProperty(ref _alsoModifiedIn, value); }
    /// <summary>Validation message shown under the control; the value is not written while it is set.</summary>
    public string? Error { get => _error; protected set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => _error is not null;

    internal void ReportError(string message) => Error = message;

    public ICommand ResetCommand { get; }
    public ICommand CopyIdCommand { get; }
    public ICommand EditInJsonCommand { get; }

    /// <summary>Shows <paramref name="effective"/> (null = the default) without writing anything back.</summary>
    internal abstract void Load(JsonElement? effective);

    internal static string? SchemaString(JsonElement? schema, string name) =>
        schema is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static double? SchemaNumber(JsonElement? schema, string name) =>
        schema is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    /// <summary>Schema <c>type</c> (a string, or the first non-null entry of an array of types).</summary>
    internal static string? SchemaType(JsonElement? schema)
    {
        if (schema is not { ValueKind: JsonValueKind.Object } s || !s.TryGetProperty("type", out var t)) return null;
        if (t.ValueKind == JsonValueKind.String) return t.GetString();
        if (t.ValueKind == JsonValueKind.Array)
            foreach (var e in t.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String && e.GetString() != "null") return e.GetString();
        return null;
    }

    /// <summary>Picks the control for a property: boolean/integer/number/string/enum, everything else (array, object, unknown) is shown read-only.</summary>
    public static SettingItemViewModel Create(SettingsViewModel owner, ConfigurationProperty p)
    {
        var (category, title) = SettingsCategories.Describe(p.Key);
        if (p.Schema is { ValueKind: JsonValueKind.Object } schema && schema.TryGetProperty("enum", out var en) && en.ValueKind == JsonValueKind.Array)
            return new EnumSettingViewModel(owner, p, category, title);
        var type = SchemaType(p.Schema) ?? p.Default?.ValueKind switch
        {
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Number => p.Default.Value.TryGetInt64(out _) ? "integer" : "number",
            JsonValueKind.String => "string",
            JsonValueKind.Array => "array",
            JsonValueKind.Object => "object",
            _ => null,
        };
        return type switch
        {
            "boolean" => new BoolSettingViewModel(owner, p, category, title),
            "integer" => new NumberSettingViewModel(owner, p, category, title, integer: true),
            "number" => new NumberSettingViewModel(owner, p, category, title, integer: false),
            "string" => new StringSettingViewModel(owner, p, category, title),
            _ => new ComplexSettingViewModel(owner, p, category, title),
        };
    }
}

public sealed class BoolSettingViewModel(SettingsViewModel owner, ConfigurationProperty p, string category, string title) : SettingItemViewModel(owner, p, category, title)
{
    private bool _value;
    private bool _loading;

    public override SettingKind Kind => SettingKind.Boolean;

    public bool Value
    {
        get => _value;
        set
        {
            if (!SetProperty(ref _value, value) || _loading) return;
            Owner.Write(this, value, debounce: false);
        }
    }

    internal override void Load(JsonElement? effective)
    {
        _loading = true;
        Value = effective is { ValueKind: JsonValueKind.True };
        _loading = false;
    }
}

public sealed class NumberSettingViewModel : SettingItemViewModel
{
    private string _text = "";
    private bool _loading;

    public NumberSettingViewModel(SettingsViewModel owner, ConfigurationProperty p, string category, string title, bool integer) : base(owner, p, category, title)
    {
        IsInteger = integer;
        Minimum = SchemaNumber(p.Schema, "minimum");
        Maximum = SchemaNumber(p.Schema, "maximum");
    }

    public override SettingKind Kind => IsInteger ? SettingKind.Integer : SettingKind.Number;
    public bool IsInteger { get; }
    public double? Minimum { get; }
    public double? Maximum { get; }

    /// <summary>The text of the box. Invalid text sets <see cref="SettingItemViewModel.Error"/> and is not written.</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (!SetProperty(ref _text, value) || _loading) return;
            if (!TryParse(value, out var number, out var error)) { Error = error; return; }
            Error = null;
            Owner.Write(this, IsInteger ? (object)(long)number : number, debounce: true);
        }
    }

    internal bool TryParse(string text, out double number, out string? error)
    {
        error = null;
        number = 0;
        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) || double.IsNaN(number) || double.IsInfinity(number))
        {
            error = IsInteger ? "Enter a whole number." : "Enter a number.";
            return false;
        }
        if (IsInteger && number != Math.Floor(number)) { error = "Enter a whole number."; return false; }
        if (Minimum is { } min && number < min) { error = $"Must be at least {Format(min)}."; return false; }
        if (Maximum is { } max && number > max) { error = $"Must be at most {Format(max)}."; return false; }
        return true;
    }

    private static string Format(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

    internal override void Load(JsonElement? effective)
    {
        _loading = true;
        Error = null;
        Text = effective is { ValueKind: JsonValueKind.Number } n ? n.GetRawText() : "";
        _loading = false;
    }
}

public sealed class StringSettingViewModel(SettingsViewModel owner, ConfigurationProperty p, string category, string title) : SettingItemViewModel(owner, p, category, title)
{
    private string _value = "";
    private bool _loading;

    public override SettingKind Kind => SettingKind.String;
    public int? MaxLength => (int?)SchemaNumber(Property.Schema, "maxLength");

    public string Value
    {
        get => _value;
        set
        {
            if (!SetProperty(ref _value, value) || _loading) return;
            if (MaxLength is { } max && value.Length > max) { Error = $"At most {max} characters."; return; }
            if (SchemaString(Property.Schema, "pattern") is { } pattern)
            {
                try
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(value, pattern, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100))) { Error = "Does not match the required format."; return; }
                }
                catch (ArgumentException) { /* an invalid schema pattern must not block editing */ }
                catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { }
            }
            Error = null;
            Owner.Write(this, value, debounce: true);
        }
    }

    internal override void Load(JsonElement? effective)
    {
        _loading = true;
        Error = null;
        Value = effective is { ValueKind: JsonValueKind.String } s ? s.GetString() ?? "" : "";
        _loading = false;
    }
}

public sealed class EnumSettingViewModel : SettingItemViewModel
{
    private EnumChoice? _selected;
    private bool _loading;

    public EnumSettingViewModel(SettingsViewModel owner, ConfigurationProperty p, string category, string title) : base(owner, p, category, title)
    {
        var schema = p.Schema!.Value;
        var descriptions = schema.TryGetProperty("enumDescriptions", out var d) && d.ValueKind == JsonValueKind.Array ? d.EnumerateArray().Select(x => x.GetString()).ToList() : [];
        var choices = new List<EnumChoice>();
        var i = 0;
        foreach (var e in schema.GetProperty("enum").EnumerateArray())
        {
            var raw = e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.GetRawText();
            choices.Add(new EnumChoice(raw, raw, i < descriptions.Count ? descriptions[i] : null));
            i++;
        }
        Choices = choices;
    }

    public override SettingKind Kind => SettingKind.Enum;
    public IReadOnlyList<EnumChoice> Choices { get; }

    public EnumChoice? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value) || _loading || value is null) return;
            Owner.Write(this, value.Value, debounce: false);
        }
    }

    internal override void Load(JsonElement? effective)
    {
        _loading = true;
        var raw = effective is { } e ? (e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText()) : null;
        Selected = Choices.FirstOrDefault(c => c.Value == raw);
        _loading = false;
    }
}

/// <summary>Arrays, objects and unknown types: shown as a read-only summary with an "Edit in settings.json" link.</summary>
public sealed class ComplexSettingViewModel(SettingsViewModel owner, ConfigurationProperty p, string category, string title) : SettingItemViewModel(owner, p, category, title)
{
    private string _summary = "";

    public override SettingKind Kind => SettingKind.Complex;
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    internal override void Load(JsonElement? effective) => Summary = Summarise(effective);

    internal static string Summarise(JsonElement? v)
    {
        if (v is not { } e) return "(not set)";
        switch (e.ValueKind)
        {
            case JsonValueKind.Array: return e.GetArrayLength() switch { 0 => "(empty list)", 1 => "1 item", var n => $"{n} items" };
            case JsonValueKind.Object:
                var names = e.EnumerateObject().Select(x => x.Name).ToList();
                return names.Count == 0 ? "(empty object)" : $"{names.Count} entr{(names.Count == 1 ? "y" : "ies")}: " + string.Join(", ", names.Take(3)) + (names.Count > 3 ? ", …" : "");
            case JsonValueKind.Null: return "(not set)";
            default: return e.GetRawText();
        }
    }
}

/// <summary>Maps setting keys to the category tree ("editor.minimap.enabled" is Editor &gt; Minimap) and readable titles.</summary>
public static class SettingsCategories
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["editor"] = "Editor", ["files"] = "Files", ["workbench"] = "Workbench", ["memory"] = "Memory", ["lsp"] = "LSP", ["debug"] = "Debug",
        ["vim"] = "Vim", ["search"] = "Search", ["terminal"] = "Terminal", ["window"] = "Window", ["explorer"] = "Explorer", ["shell"] = "Shell",
        ["keymap"] = "Keymap", ["diagnostics"] = "Diagnostics", ["languageServers"] = "LSP", ["debuggers"] = "Debug", ["json"] = "JSON", ["http"] = "Network",
    };

    /// <summary>Top-level category and optional sub-category of a key. A leading "cscode." namespace is ignored.</summary>
    public static (string Top, string? Sub) Of(string key)
    {
        var parts = key.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var skip = parts.Length >= 3 && parts[0] == "cscode" ? 1 : 0;
        var rest = parts[skip..];
        if (rest.Length == 0) return ("Other", null);
        var top = Pretty(rest[0]);
        var sub = rest.Length >= 3 ? Pretty(rest[1]) : null;
        return (top, sub);
    }

    /// <summary>"Editor › Minimap" and "Enabled" for the key editor.minimap.enabled.</summary>
    public static (string Category, string Title) Describe(string key)
    {
        var parts = key.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var (top, sub) = Of(key);
        var name = Humanise(parts.Length > 0 ? parts[^1] : key);
        var category = sub is null ? top : top + " › " + sub;
        return (category, category + ": " + name);
    }

    private static string Pretty(string segment) => Names.TryGetValue(segment, out var n) ? n : Humanise(segment);

    /// <summary>camelCase to "Camel Case".</summary>
    public static string Humanise(string s)
    {
        if (s.Length == 0) return s;
        var sb = new System.Text.StringBuilder(s.Length + 4);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (i > 0 && char.IsUpper(c) && (char.IsLower(s[i - 1]) || (i + 1 < s.Length && char.IsLower(s[i + 1])))) sb.Append(' ');
            sb.Append(i == 0 ? char.ToUpperInvariant(c) : c);
        }
        return sb.ToString();
    }
}
