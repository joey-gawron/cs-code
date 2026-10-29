using CommunityToolkit.Mvvm.ComponentModel;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Ui.Collections;

namespace CsCode.App.ToolWindows.Output;

/// <summary>
/// Output panel model: a channel dropdown plus the selected channel's text as virtualised lines. Appends arrive pre-batched (one
/// <see cref="IOutputChannel.Appended"/> per UI turn), are split into lines once, and the line list is capped so a long session cannot grow it.
/// Owned by the view and disposed with it (tool windows drop their views when hidden), which releases every channel subscription.
/// </summary>
public sealed class OutputViewModel : ObservableObject, IDisposable
{
    public const int MaxLines = 20_000;
    public const int MaxLineLength = 4_000;

    private readonly IOutputService _output;
    private readonly DisposableStore _subs = new();
    private IDisposable? _channelSub;
    private IOutputChannel? _channel;
    private string? _selected;
    private bool _partialLine;
    private bool _autoScroll = true;

    public OutputViewModel(IOutputService output)
    {
        _output = output;
        Refresh();
        _subs.Add(output.ChannelsChanged.Subscribe(_ => Refresh()));
    }

    public BulkObservableCollection<string> Channels { get; } = new();
    public BulkObservableCollection<string> Lines { get; } = new();

    public bool AutoScroll { get => _autoScroll; set => SetProperty(ref _autoScroll, value); }

    public string? SelectedChannel
    {
        get => _selected;
        set { if (SetProperty(ref _selected, value)) Attach(value); }
    }

    private void Refresh()
    {
        var names = _output.ChannelNames;
        Channels.ReplaceAll(names);
        if (_selected is null || !names.Contains(_selected)) SelectedChannel = names.FirstOrDefault();
    }

    private void Attach(string? name)
    {
        _channelSub?.Dispose();
        _channelSub = null;
        _channel = null;
        _partialLine = false;
        Lines.Clear();
        if (name is null) return;
        _channel = _output.GetOrCreate(name);
        var existing = Tail(_channel.GetText(), InitialLines);
        if (existing.Length > 0) AppendText(existing);
        _channelSub = _channel.Appended.Subscribe(t =>
        {
            if (t.Length == 0) Lines.Clear(); // clear signal
            else AppendText(t);
        });
    }

    /// <summary>Lines shown when a channel is first attached: the recent tail only. Splitting the whole retained 2 MB on the UI thread every time
    /// the panel opens would blow the per-callback budget; the full text stays in the channel ring.</summary>
    public const int InitialLines = 5_000;

    /// <summary>The last <paramref name="lines"/> lines of <paramref name="text"/> (found by scanning backwards; no full split).</summary>
    internal static string Tail(string text, int lines)
    {
        var count = 0;
        for (var i = text.Length - 1; i >= 0; i--)
        {
            if (text[i] != '\n' || i == text.Length - 1) continue;
            if (++count == lines) return text[(i + 1)..];
        }
        return text;
    }

    /// <summary>Splits text into lines and appends them in one bulk change; a trailing partial line is completed by the next batch.</summary>
    internal void AppendText(string text)
    {
        // A flood can deliver far more text in one batch than the view can ever show; keep only the tail so one UI turn never splits megabytes.
        if (text.Length > 200_000) { text = Tail(text, MaxLines / 4); _partialLine = false; }
        var fresh = new List<string>();
        var start = 0;
        var first = true;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] != '\n') continue;
            if (i == text.Length && start == text.Length) break;
            var line = Clip(text[start..i].TrimEnd('\r'));
            if (first && _partialLine && Lines.Count > 0)
            {
                Lines[^1] = Clip(Lines[^1] + line);
                _partialLine = i == text.Length; // still unterminated?
            }
            else
            {
                fresh.Add(line);
                _partialLine = i == text.Length;
            }
            first = false;
            start = i + 1;
        }
        if (fresh.Count > 0) Lines.AddRange(fresh);
        if (Lines.Count > MaxLines) Lines.RemoveRange(0, Lines.Count - MaxLines * 3 / 4);
    }

    private static string Clip(string s) => s.Length > MaxLineLength ? s[..MaxLineLength] + "…" : s;

    public void Clear() => _channel?.Clear();

    public void Dispose()
    {
        _channelSub?.Dispose();
        _subs.Dispose();
        Lines.Clear();
        Channels.Clear();
    }
}
