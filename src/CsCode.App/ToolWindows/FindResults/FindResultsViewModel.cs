using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Ui.Collections;

namespace CsCode.App.ToolWindows.FindResults;

/// <summary>One reference / search hit. The preview line is read lazily when the row is first shown.</summary>
public sealed class FindResultRow : ObservableObject
{
    private string? _preview;
    private bool _loading;

    public FindResultRow(Location location, bool isHeader = false, string? header = null, int count = 0)
    {
        Location = location;
        IsHeader = isHeader;
        HeaderText = header;
        Count = count;
    }

    public Location Location { get; }
    public bool IsHeader { get; }
    public string? HeaderText { get; }
    public int Count { get; }
    public int Line => Location.Range.Start.Line + 1;
    public int Column => Location.Range.Start.Character + 1;
    public string? Preview { get => _preview; internal set => SetProperty(ref _preview, value); }
    internal bool TryBeginLoad()
    {
        if (_loading || _preview is not null) return false;
        return _loading = true;
    }
}

/// <summary>
/// "Find Results": hosts locations delivered through the <c>cscode.references.show</c> command (the editor's Find References with many hits).
/// Results are grouped by file; the text of each hit's line is read on demand from the open document or from disk, with a small bounded cache
/// of file contents, so showing 5,000 references does not read 5,000 lines up front.
/// </summary>
public sealed class FindResultsViewModel : ObservableObject
{
    public const int MaxResults = 20_000;
    private const int MaxCachedFiles = 16;

    private readonly INavigationService? _navigation;
    private readonly IFileSystem? _fs;
    private readonly IDocumentManager? _documents;
    private readonly Dictionary<Uri, string[]> _lineCache = new();
    private readonly Queue<Uri> _cacheOrder = new();
    private string _title = "No results";
    private int _hitCount;

    public FindResultsViewModel(INavigationService? navigation = null, IFileSystem? fileSystem = null, IDocumentManager? documents = null)
    {
        _navigation = navigation;
        _fs = fileSystem;
        _documents = documents;
    }

    public BulkObservableCollection<FindResultRow> Rows { get; } = new();
    public string Title { get => _title; private set => SetProperty(ref _title, value); }
    public int HitCount { get => _hitCount; private set => SetProperty(ref _hitCount, value); }
    public bool Truncated { get; private set; }

    /// <summary>Replaces the shown results. Accepts Core <see cref="Location"/>s, or LSP-shaped JSON (uri + range) as sent through the command bus.</summary>
    public void Show(string? title, IReadOnlyList<Location> locations)
    {
        _lineCache.Clear();
        _cacheOrder.Clear();
        Truncated = locations.Count > MaxResults;
        var capped = Truncated ? locations.Take(MaxResults).ToList() : locations;
        var rows = new List<FindResultRow>(capped.Count + 16);
        foreach (var group in capped.GroupBy(l => l.Uri).OrderBy(g => g.Key.ToString(), StringComparer.OrdinalIgnoreCase))
        {
            var path = group.Key.IsFile ? group.Key.LocalPath : group.Key.ToString();
            rows.Add(new FindResultRow(new Location(group.Key, default), true, $"{Path.GetFileName(path)}  ({group.Count()})  {Path.GetDirectoryName(path)}", group.Count()));
            rows.AddRange(group.OrderBy(l => l.Range.Start).Select(l => new FindResultRow(l)));
        }
        Rows.ReplaceAll(rows);
        HitCount = capped.Count;
        Title = (title ?? "References") + $" - {capped.Count} result{(capped.Count == 1 ? "" : "s")}" + (Truncated ? $" (first {MaxResults:N0} shown)" : "");
    }

    /// <summary>Entry point for the <c>cscode.references.show</c> command arguments.</summary>
    public void ShowFromCommand(IReadOnlyList<object?> args)
    {
        string? title = null;
        var locations = new List<Location>();
        foreach (var a in args)
        {
            switch (a)
            {
                case string s: title = s; break;
                case IEnumerable<Location> list: locations.AddRange(list); break;
                case JsonElement { ValueKind: JsonValueKind.Array } arr: foreach (var item in arr.EnumerateArray()) if (TryParse(item, out var l)) locations.Add(l); break;
                case JsonElement { ValueKind: JsonValueKind.String } js: title = js.GetString(); break;
            }
        }
        Show(title, locations);
    }

    private static bool TryParse(JsonElement e, out Location location)
    {
        location = default!;
        try
        {
            if (!e.TryGetProperty("uri", out var u) || !e.TryGetProperty("range", out var r)) return false;
            var start = r.GetProperty("start");
            var end = r.GetProperty("end");
            location = new Location(new Uri(u.GetString()!),
                new TextRange(new(start.GetProperty("line").GetInt32(), start.GetProperty("character").GetInt32()), new(end.GetProperty("line").GetInt32(), end.GetProperty("character").GetInt32())));
            return true;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException or UriFormatException) { return false; }
    }

    /// <summary>Reads the row's line text if it has not been loaded (called when the row scrolls into view).</summary>
    public async Task LoadPreviewAsync(FindResultRow row, CancellationToken ct = default)
    {
        if (row.IsHeader || !row.TryBeginLoad()) return;
        try
        {
            var lines = await LinesOfAsync(row.Location.Uri, ct).ConfigureAwait(true);
            var n = row.Location.Range.Start.Line;
            row.Preview = lines is not null && n < lines.Length ? lines[n].Trim() : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            row.Preview = "";
        }
    }

    private async Task<string[]?> LinesOfAsync(Uri uri, CancellationToken ct)
    {
        if (_lineCache.TryGetValue(uri, out var cached)) return cached;
        string[]? lines = null;
        if (_documents?.Get(uri) is { } doc)
            lines = await Task.Run(() => doc.GetText().Split('\n'), ct).ConfigureAwait(true);
        else if (_fs is not null)
        {
            var bytes = await _fs.ReadFileAsync(uri, ct).ConfigureAwait(true);
            lines = await Task.Run(() => System.Text.Encoding.UTF8.GetString(bytes).Split('\n'), ct).ConfigureAwait(true);
        }
        if (lines is null) return null;
        _lineCache[uri] = lines;
        _cacheOrder.Enqueue(uri);
        while (_cacheOrder.Count > MaxCachedFiles) _lineCache.Remove(_cacheOrder.Dequeue()); // bounded: file contents are re-readable
        return lines;
    }

    public async Task OpenAsync(FindResultRow row)
    {
        if (_navigation is null || row.IsHeader) return;
        await _navigation.OpenAsync(row.Location.Uri, row.Location.Range).ConfigureAwait(true);
    }

    public void Clear()
    {
        Rows.Clear();
        _lineCache.Clear();
        _cacheOrder.Clear();
        HitCount = 0;
        Title = "No results";
    }
}
