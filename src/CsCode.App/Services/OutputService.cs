using System.Text;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Ui.Threading;

namespace CsCode.App.Services;

/// <summary>
/// Output channels: each retains at most <see cref="MaxChars"/> characters (a ring over a StringBuilder, trimmed to a line boundary),
/// so a chatty tool cannot grow memory. Appends from any thread are buffered and flushed to subscribers as ONE batched
/// <see cref="IOutputChannel.Appended"/> per UI turn (coalesced), never one dispatcher post per line.
/// </summary>
public sealed class OutputService(IUiDispatcher ui) : IOutputService
{
    public const int MaxChars = 2 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly Dictionary<string, Channel> _channels = new(StringComparer.Ordinal);
    private readonly Emitter<string> _changed = new();

    public IEventSource<string> ChannelsChanged => _changed;

    public IReadOnlyList<string> ChannelNames
    {
        get { lock (_gate) return _channels.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray(); }
    }

    public IOutputChannel GetOrCreate(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Channel channel;
        bool created;
        lock (_gate)
        {
            created = !_channels.TryGetValue(name, out channel!);
            if (created) _channels[name] = channel = new Channel(name, ui, Remove);
        }
        if (created) ui.PostCoalesced("output.channels", () => _changed.Fire(name));
        return channel;
    }

    private void Remove(Channel channel)
    {
        lock (_gate)
            if (_channels.TryGetValue(channel.Name, out var cur) && ReferenceEquals(cur, channel)) _channels.Remove(channel.Name);
        ui.PostCoalesced("output.channels", () => _changed.Fire(channel.Name));
    }

    private sealed class Channel(string name, IUiDispatcher ui, Action<Channel> onDispose) : IOutputChannel
    {
        private readonly object _gate = new();
        private readonly StringBuilder _ring = new();
        private readonly StringBuilder _pending = new();
        private readonly Emitter<string> _appended = new();
        private bool _disposed;

        public string Name { get; } = name;
        public IEventSource<string> Appended => _appended;

        public void Append(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (_gate)
            {
                if (_disposed) return;
                _ring.Append(text);
                _pending.Append(text);
                if (_ring.Length > MaxChars) Trim(_ring, MaxChars * 3 / 4);
                // The subscriber only needs what is still retained; never let the pending buffer outgrow the ring.
                if (_pending.Length > MaxChars) Trim(_pending, MaxChars * 3 / 4);
            }
            ui.PostCoalesced("output.flush:" + Name, Flush);
        }

        public void AppendLine(string line) => Append(line + "\n");

        public void Clear()
        {
            lock (_gate)
            {
                _ring.Clear();
                _pending.Clear();
            }
            ui.PostCoalesced("output.flush:" + Name, () => _appended.Fire(string.Empty)); // empty = "cleared" signal
        }

        public string GetText()
        {
            lock (_gate) return _ring.ToString();
        }

        private void Flush()
        {
            string text;
            lock (_gate)
            {
                if (_pending.Length == 0) return;
                text = _pending.ToString();
                _pending.Clear();
            }
            _appended.Fire(text);
        }

        /// <summary>Drops the oldest characters down to <paramref name="keep"/>, snapping forward to the next line start.</summary>
        private static void Trim(StringBuilder sb, int keep)
        {
            var drop = sb.Length - keep;
            if (drop <= 0) return;
            var cut = drop;
            for (var i = drop; i < Math.Min(sb.Length, drop + 4096); i++)
                if (sb[i] == '\n') { cut = i + 1; break; }
            sb.Remove(0, cut);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _ring.Clear();
                _ring.Capacity = 16;
                _pending.Clear();
            }
            _appended.Clear();
            onDispose(this);
        }
    }
}
