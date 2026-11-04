using System.ComponentModel;
using System.Globalization;
using CsCode.Core.Timeline;

namespace CsCode.Panels.Timeline;

/// <summary>A row of the Timeline: an item (a local-history entry, a commit), or the "Load more" row at the end while a source has more.</summary>
public sealed class TimelineRow : INotifyPropertyChanged
{
    private string _time = "";

    private TimelineRow(TimelineItem? item, string label)
    {
        Item = item;
        Label = label;
    }

    public static TimelineRow For(TimelineItem item, DateTimeOffset now)
    {
        var row = new TimelineRow(item, item.Label);
        row.UpdateTime(now);
        return row;
    }

    public static TimelineRow LoadMore() => new(null, "Load more");

    public TimelineItem? Item { get; }
    public bool IsLoadMore => Item is null;
    public string Label { get; }
    public string? Description => Item?.Description;
    public string? Tooltip => Item?.Tooltip;
    public string IconKey => Item?.IconKey ?? "ellipsis";

    /// <summary>How long ago, as VS Code's Timeline says it ("now", "5 mins", "2 hrs", "3 days", "1 wk", "4 mos", "1 yr").</summary>
    public string Time
    {
        get => _time;
        private set
        {
            if (_time == value) return;
            _time = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Time)));
        }
    }

    public void UpdateTime(DateTimeOffset now) => Time = Item is { } i ? RelativeTime.Format(i.Timestamp, now) : "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString() => IsLoadMore ? Label : $"{Label} [{Item!.ProviderId}] {Time}";
}

/// <summary>Short relative times, as the Timeline shows them.</summary>
public static class RelativeTime
{
    public static string Format(DateTimeOffset when, DateTimeOffset now)
    {
        var seconds = (now - when).TotalSeconds;
        if (seconds < 60) return "now";
        static string N(double v, string one, string many)
        {
            var n = (long)Math.Floor(v);
            return n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many);
        }
        var minutes = seconds / 60;
        if (minutes < 60) return N(minutes, "min", "mins");
        var hours = minutes / 60;
        if (hours < 24) return N(hours, "hr", "hrs");
        var days = hours / 24;
        if (days < 7) return N(days, "day", "days");
        if (days < 30) return N(days / 7, "wk", "wks");
        if (days < 365) return N(days / 30, "mo", "mos");
        return N(days / 365, "yr", "yrs");
    }
}
