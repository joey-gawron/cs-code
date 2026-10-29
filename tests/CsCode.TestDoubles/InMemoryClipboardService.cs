using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;

namespace CsCode.TestDoubles;

/// <summary>
/// <see cref="IClipboardService"/> in memory, with a PRIMARY selection, for module tests: what was written, what another application "put" on the
/// clipboard (<see cref="SetExternal"/>), a gate that holds reads back (a slow clipboard owner) and a ring with the real limits.
/// </summary>
public sealed class InMemoryClipboardService : IClipboardService
{
    private readonly Emitter<ClipboardText> _ringChanged = new();
    private readonly List<ClipboardText> _ring = new();
    private readonly object _gate = new();
    private ClipboardText? _content;
    private Func<string>? _primary;
    private string? _externalPrimary;

    /// <summary>When set, reads wait for this task (simulates a clipboard owner that is slow to answer).</summary>
    public Task? ReadGate { get; set; }

    public bool PrimarySupported { get; set; } = true;
    public int Reads { get; private set; }
    public int Writes { get; private set; }

    public ClipboardText? Content { get { lock (_gate) return _content; } }

    /// <summary>Another application took the clipboard with <paramref name="text"/> (CsCode's shape metadata no longer applies).</summary>
    public void SetExternal(string? text) { lock (_gate) _content = text is null ? null : ClipboardText.Plain(text); }

    /// <summary>Another application took PRIMARY.</summary>
    public void SetExternalPrimary(string? text) { lock (_gate) { _primary = null; _externalPrimary = text; } }

    /// <summary>The text CsCode currently offers as PRIMARY (evaluates the lazy provider), or null.</summary>
    public string? OfferedPrimary { get { Func<string>? p; lock (_gate) p = _primary; return p?.Invoke(); } }

    public async ValueTask<ClipboardText?> ReadAsync(CancellationToken ct = default)
    {
        Reads++;
        if (ReadGate is { } gate) await gate.WaitAsync(ct).ConfigureAwait(false);
        lock (_gate) return _content;
    }

    public ValueTask WriteAsync(ClipboardText content, CancellationToken ct = default)
    {
        Writes++;
        lock (_gate)
        {
            _content = content;
            if ((long)content.Text.Length * sizeof(char) <= ClipboardLimits.RingEntryMaxBytes && content.Text.Length > 0)
            {
                _ring.RemoveAll(r => r.Text == content.Text);
                _ring.Insert(0, content);
                if (_ring.Count > ClipboardLimits.RingCapacity) _ring.RemoveAt(_ring.Count - 1);
            }
        }
        _ringChanged.Fire(content);
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> HasTextAsync(CancellationToken ct = default) { lock (_gate) return ValueTask.FromResult(_content is { Text.Length: > 0 }); }

    public IReadOnlyList<ClipboardText> Ring { get { lock (_gate) return _ring.ToArray(); } }
    public IEventSource<ClipboardText> RingChanged => _ringChanged;
    public bool IsPrimarySelectionSupported => PrimarySupported;

    public void SetPrimarySelection(Func<string>? provider) { lock (_gate) { _primary = provider; _externalPrimary = null; } }

    public ValueTask<string?> ReadPrimarySelectionAsync(CancellationToken ct = default)
    {
        Func<string>? p;
        string? external;
        lock (_gate) { p = _primary; external = _externalPrimary; }
        return ValueTask.FromResult(p is not null ? p() : external);
    }
}
