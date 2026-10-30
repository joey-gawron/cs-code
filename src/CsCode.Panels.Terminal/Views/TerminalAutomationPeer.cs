using System.Diagnostics;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using CsCode.Terminal.Emulator;

namespace CsCode.Panels.Terminal.Views;

/// <summary>
/// What a screen reader sees of a terminal: a read-only document named after the terminal ("Terminal 1: bash") whose value is the text of the
/// visible rows (soft-wrapped rows joined into their line), whose help text is the last command shell integration reported (its line and
/// exit code), with value changes announced at most twice a second while the screen changes. The whole scrollback is in the accessible buffer
/// (Alt+F2), the end of a finished command's output in the Terminal window's live region. Built only when assistive technology asks.
/// </summary>
public sealed class TerminalAutomationPeer : ControlAutomationPeer, IValueProvider
{
    private static readonly TimeSpan AnnounceInterval = TimeSpan.FromMilliseconds(500);
    private string? _lastValue;
    private long _lastRaised;

    public TerminalAutomationPeer(TerminalControl owner) : base(owner) => Terminal = owner;

    public TerminalControl Terminal { get; }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;

    protected override string GetLocalizedControlTypeCore() => "terminal";

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;

    protected override string? GetHelpTextCore() => Terminal.Session?.LastCommand is { } last
        ? $"Last command: {last.Command.CommandLine ?? "(unknown)"}" + (last.Command.ExitCode is { } code ? $", exit code {code}." : ".")
        : base.GetHelpTextCore();

    /// <summary>The visible text (the snapshot the terminal drew last).</summary>
    public string Value => Terminal.Snapshot is { } snap ? TerminalAccessibility.VisibleText(snap).Text : "";

    public bool IsReadOnly => true;

    public void SetValue(string? value) => throw new InvalidOperationException("The terminal's text cannot be set; type into the terminal instead.");

    /// <summary>A frame was drawn: tell assistive technology the value changed (throttled).</summary>
    internal void OnFrame()
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastRaised != 0 && Stopwatch.GetElapsedTime(_lastRaised, now) < AnnounceInterval) return;
        var value = Value;
        if (value == _lastValue) return;
        var old = _lastValue;
        _lastValue = value;
        _lastRaised = now;
        RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, old, value);
    }
}
