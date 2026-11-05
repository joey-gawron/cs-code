using System.Text.Json;
using CsCode.Core.Platform;

namespace CsCode.Panels.Timeline.LocalHistory;

/// <summary>VS Code's local history settings (same keys, defaults and units), registered by <see cref="LocalHistoryService"/>.</summary>
public static class LocalHistorySettings
{
    public const string Enabled = "workbench.localHistory.enabled";
    /// <summary>In KB.</summary>
    public const string MaxFileSize = "workbench.localHistory.maxFileSize";
    public const string MaxFileEntries = "workbench.localHistory.maxFileEntries";
    /// <summary>An object of glob → true (VS Code's <c>files.exclude</c> shape).</summary>
    public const string Exclude = "workbench.localHistory.exclude";
    /// <summary>In seconds.</summary>
    public const string MergeWindow = "workbench.localHistory.mergeWindow";

    public const int DefaultMaxFileSizeKb = 256;
    public const int DefaultMaxFileEntries = 50;
    public const int DefaultMergeWindowSeconds = 10;

    public static IEnumerable<ConfigurationProperty> Properties()
    {
        static JsonElement J(object? v) => JsonSerializer.SerializeToElement(v);
        static JsonElement S(string json) => JsonDocument.Parse(json).RootElement.Clone();
        yield return new ConfigurationProperty(Enabled, J(true),
            "Keep local history: each save of a file stores its content, so earlier versions can be compared and restored from the Timeline. Turning it off keeps the entries already stored.",
            S("""{"type":"boolean"}"""));
        yield return new ConfigurationProperty(MaxFileSize, J(DefaultMaxFileSizeKb),
            "The largest file (in KB) local history stores. Saves of larger files add no entry; entries already stored are kept.",
            S("""{"type":"number","minimum":1}"""));
        yield return new ConfigurationProperty(MaxFileEntries, J(DefaultMaxFileEntries),
            "How many local history entries a file keeps. Past it the oldest entries are deleted.",
            S("""{"type":"number","minimum":0}"""));
        yield return new ConfigurationProperty(Exclude, J(new Dictionary<string, bool>()),
            "Glob patterns of files local history leaves out (an object of pattern → true). A pattern is matched against the path relative to its workspace folder, an absolute pattern against the full path. Entries already stored are kept.",
            S("""{"type":"object","additionalProperties":{"type":"boolean"}}"""));
        yield return new ConfigurationProperty(MergeWindow, J(DefaultMergeWindowSeconds),
            "Seconds within which a new entry replaces the newest entry of a file when both come from the same source (two saves in a row, auto save), so frequent saves do not push older history out.",
            S("""{"type":"number","minimum":1}"""));
    }
}
