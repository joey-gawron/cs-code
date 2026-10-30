using System.Text;
using System.Text.Json;
using CsCode.Core.Platform;

namespace CsCode.Macros;

/// <summary>
/// Collects the steps of one recording (UI thread only; no locking). Consecutive typed text is merged into one <see cref="TextStep"/>. Bounded:
/// at most <see cref="MaxSteps"/> steps and <see cref="MaxTextChars"/> characters of text; once full, every further step is refused
/// (<see cref="IsFull"/>) so the caller can stop and say so. With <see cref="PauseThreshold"/> set, a gap of at least that long before a step
/// is recorded as a <see cref="PauseStep"/> (at most <see cref="MaxPause"/>).
/// </summary>
public sealed class MacroRecorder
{
    public const int MaxTextChars = 1_000_000;
    public static readonly TimeSpan MaxPause = TimeSpan.FromSeconds(10);

    private readonly List<MacroStep> _steps = new();
    private readonly StringBuilder _text = new();
    private readonly TimeProvider _time;
    private long _lastStep;
    private int _textChars;

    public MacroRecorder(int maxSteps = MacroSettings.DefaultMaxRecordedSteps, TimeSpan? pauseThreshold = null, TimeProvider? time = null)
    {
        MaxSteps = Math.Max(1, maxSteps);
        PauseThreshold = pauseThreshold;
        _time = time ?? TimeProvider.System;
        _lastStep = _time.GetTimestamp();
    }

    public int MaxSteps { get; }
    public TimeSpan? PauseThreshold { get; }

    /// <summary>Steps so far (the pending text run counts as one).</summary>
    public int Count => _steps.Count + (_text.Length > 0 ? 1 : 0);

    /// <summary>No further step fits.</summary>
    public bool IsFull { get; private set; }

    public bool AddKey(KeyStroke key) => Add(new KeyStep(key));

    public bool AddCommand(string id, IReadOnlyList<JsonElement> args) => Add(new CommandStep(id, args));

    /// <summary>Appends typed text (merged with text typed right before it).</summary>
    public bool AddText(string text)
    {
        if (string.IsNullOrEmpty(text) || IsFull) return false;
        if (_text.Length == 0)
        {
            AddPause();
            if (_steps.Count + 1 > MaxSteps) return Full();
        }
        if (_textChars + text.Length > MaxTextChars) return Full();
        _text.Append(text);
        _textChars += text.Length;
        _lastStep = _time.GetTimestamp();
        return true;
    }

    private bool Add(MacroStep step)
    {
        if (IsFull) return false;
        FlushText();
        AddPause();
        if (_steps.Count + 1 > MaxSteps) return Full();
        _steps.Add(step);
        _lastStep = _time.GetTimestamp();
        return true;
    }

    private void AddPause()
    {
        if (PauseThreshold is not { } threshold) return;
        var gap = _time.GetElapsedTime(_lastStep);
        if (gap < threshold || _steps.Count == 0 && _text.Length == 0) return; // nothing to wait for before the first step
        FlushText();
        if (_steps.Count + 2 > MaxSteps) return;
        _steps.Add(new PauseStep((int)Math.Min(gap.TotalMilliseconds, MaxPause.TotalMilliseconds)));
    }

    private void FlushText()
    {
        if (_text.Length == 0) return;
        _steps.Add(new TextStep(_text.ToString()));
        _text.Clear();
    }

    private bool Full()
    {
        IsFull = true;
        return false;
    }

    /// <summary>The recorded steps (the pending text run included). The recorder can continue afterwards.</summary>
    public IReadOnlyList<MacroStep> ToSteps()
    {
        FlushText();
        return _steps.ToArray();
    }
}
