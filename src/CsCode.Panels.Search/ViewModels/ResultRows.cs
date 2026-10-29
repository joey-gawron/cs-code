using CommunityToolkit.Mvvm.ComponentModel;
using CsCode.Core.Lifecycle;
using CsCode.Panels.Search.Engine;

namespace CsCode.Panels.Search.ViewModels;

/// <summary>One row of the flattened (and therefore virtualisable) results tree: a file or one of its matches.</summary>
public abstract class ResultRow : ObservableObject
{
    public abstract bool IsFile { get; }
    public abstract int Level { get; }
}

public sealed class FileRow : ResultRow
{
    private int _matchCount;
    private bool _isExpanded = true;

    public FileRow(Uri uri, string name, string directory)
    {
        Uri = uri;
        Name = name;
        Directory = directory;
    }

    public Uri Uri { get; }
    public string Name { get; }
    /// <summary>Folder of the file relative to its workspace folder (shown dimmed after the name); empty for files in the root.</summary>
    public string Directory { get; }
    public string FullPath => Uri.IsFile ? Uri.LocalPath : Uri.ToString();
    public List<MatchRow> Matches { get; } = new();
    public override bool IsFile => true;
    public override int Level => 0;
    public bool FromMemory { get; init; }
    public int? DocumentVersion { get; set; }

    public int MatchCount { get => _matchCount; set => SetProperty(ref _matchCount, value); }
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }
}

/// <summary>
/// Shared by every match row of one results tree: the function that turns a matched text into its replacement (null while no replacement is
/// typed). Row views read it when they are realised and again on <see cref="Changed"/>, so a keystroke in the replace box costs O(visible rows),
/// never O(all results).
/// </summary>
public sealed class ReplacePreviewState
{
    private readonly Emitter<object?> _changed = new();

    public Func<string, string>? Previewer { get; private set; }
    public bool IsActive => Previewer is not null;
    public IEventSource<object?> Changed => _changed;

    public void Set(Func<string, string>? previewer)
    {
        Previewer = previewer;
        _changed.Fire(null);
    }

    public string? Preview(string matchedText) => Previewer?.Invoke(matchedText);
}

public sealed class MatchRow : ResultRow
{
    public MatchRow(FileRow file, SearchMatch match, ReplacePreviewState previews)
    {
        File = file;
        Match = match;
        Previews = previews;
    }

    public ReplacePreviewState Previews { get; }

    public FileRow File { get; }
    public SearchMatch Match { get; }
    public override bool IsFile => false;
    public override int Level => 1;
    /// <summary>1-based, as shown in the tree.</summary>
    public int LineNumber => Match.Range.Start.Line + 1;
    public string Preview => Match.Preview;
    public int PreviewMatchStart => Match.PreviewMatchStart;
    public int PreviewMatchLength => Match.PreviewMatchLength;
    public string MatchedText => Match.MatchedText;
}
