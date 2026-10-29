using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CsCode.Core.Debugging;
using CsCode.Panels.Debug.Infrastructure;

namespace CsCode.Panels.Debug.Exceptions;

/// <summary>One exception of a chain: its type, message, stack and the exceptions it wraps.</summary>
public sealed record ExceptionDetails(string TypeName, string Message, string? StackTrace, IReadOnlyList<ExceptionDetails> Inner)
{
    /// <summary>The whole chain as plain text (what "Copy Details" puts on the clipboard).</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        Append(sb, this, 0);
        return sb.ToString().TrimEnd();
    }

    private static void Append(StringBuilder sb, ExceptionDetails e, int depth)
    {
        var pad = new string(' ', depth * 2);
        sb.Append(pad).Append(e.TypeName);
        if (e.Message.Length > 0) sb.Append(": ").Append(e.Message);
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(e.StackTrace))
            foreach (var line in e.StackTrace.Split('\n')) sb.Append(pad).Append("  ").AppendLine(line.TrimEnd('\r'));
        foreach (var inner in e.Inner)
        {
            sb.Append(pad).AppendLine("--- inner exception ---");
            Append(sb, inner, depth + 1);
        }
    }
}

/// <summary>
/// The Exception Helper: when the debuggee stops with reason <c>exception</c> it fetches the exception's type, message, stack and inner
/// exceptions with the DAP <c>exceptionInfo</c> request (when the session supports it; otherwise the adapter's own stop description is
/// shown) and offers "Copy Details" and "Continue". It is a model plus a card view so the editor can host it as a popup next to the stop
/// location; this module hosts it as the <c>debug.exception</c> tool window, which auto-shows while an exception is the reason for the stop.
/// </summary>
public sealed class ExceptionHelperViewModel : ObservableObject, IDisposable
{
    public const int MaxStackChars = 20_000;
    public const int MaxInnerDepth = 8;

    private readonly DebugPanelServices _s;
    private readonly DebugFocusTracker _tracker;
    private CancellationTokenSource _cts = new();
    private long _generation;
    private long _shownEpoch = -1;
    private ExceptionDetails? _details;
    private string _title = "";
    private string? _status = "No exception.";
    private int _disposed;

    public ExceptionHelperViewModel(DebugPanelServices services)
    {
        _s = services;
        CopyCommand = new UiCommand(CopyAsync, () => _details is not null);
        ContinueCommand = new UiCommand(ContinueAsync, () => _details is not null);
        _tracker = new DebugFocusTracker(services, OnFocus);
        _tracker.Start();
    }

    /// <summary>"Exception Thrown", "Unhandled Exception"… (from the adapter's break mode).</summary>
    public string Title { get => _title; private set => SetProperty(ref _title, value); }
    public ExceptionDetails? Details { get => _details; private set { if (SetProperty(ref _details, value)) { OnPropertyChanged(nameof(HasException)); OnPropertyChanged(nameof(ShowStatus)); CopyCommand.RaiseCanExecuteChanged(); ContinueCommand.RaiseCanExecuteChanged(); } } }
    public bool HasException => _details is not null;
    public string? StatusText { get => _status; private set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(ShowStatus)); } }
    public bool ShowStatus => _details is null && !string.IsNullOrEmpty(_status);

    /// <summary>The full chain, flattened for the card (outer exception first, inner ones indented by <see cref="ChainItem.Depth"/>).</summary>
    public IReadOnlyList<ChainItem> Chain { get; private set; } = [];

    public UiCommand CopyCommand { get; }
    public UiCommand ContinueCommand { get; }
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public sealed record ChainItem(int Depth, string TypeName, string Message, string? StackTrace)
    {
        public Avalonia.Thickness Indent => new(Depth * 14, 0, 0, 0);
        public bool HasStack => !string.IsNullOrWhiteSpace(StackTrace);
        public string Heading => Message.Length > 0 ? TypeName + ": " + Message : TypeName;
    }

    private void OnFocus(DebugFocus focus)
    {
        if (IsDisposed) return;
        var reason = focus.StopReason ?? (focus.IsStopped && _s.Debug.StopLocation is { Reason: "exception" } loc && ReferenceEquals(loc.Session, focus.Session) ? "exception" : null);
        if (!focus.IsStopped || reason != "exception")
        {
            Clear(focus.IsStopped ? "Stopped, not on an exception." : "No exception.");
            return;
        }
        if (focus.Epoch == _shownEpoch) return;
        _shownEpoch = focus.Epoch;
        _ = LoadAsync(focus);
    }

    private void Clear(string status)
    {
        Interlocked.Increment(ref _generation);
        var old = _cts;
        _cts = new CancellationTokenSource();
        try { old.Cancel(); }
        catch (AggregateException) { }
        old.Dispose();
        _shownEpoch = -1;
        Details = null;
        Chain = [];
        OnPropertyChanged(nameof(Chain));
        StatusText = status;
    }

    internal async Task LoadAsync(DebugFocus focus)
    {
        var session = focus.Session!;
        var generation = Interlocked.Increment(ref _generation);
        var ct = _cts.Token;
        var threadId = focus.StopThreadId ?? focus.ThreadId ?? session.FocusedThreadId ?? 0;
        StatusText = "Loading exception details…";
        ExceptionDetails? details = null;
        var title = "Exception Thrown";
        try
        {
            if (session.Capabilities.SupportsExceptionInfo)
            {
                var args = JsonSerializer.SerializeToElement(new { threadId });
                var body = await _s.Ui.OnUi(session.CustomRequestAsync("exceptionInfo", args, ct), ct);
                if (generation != Volatile.Read(ref _generation) || IsDisposed) return;
                if (body is { ValueKind: JsonValueKind.Object } json)
                {
                    details = Parse(json, out title);
                }
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception) { /* fall back to the stop description below */ }
        if (generation != Volatile.Read(ref _generation) || IsDisposed) return;

        details ??= new ExceptionDetails("Exception", focus.StopDescription ?? "The debuggee stopped on an exception.", null, []);
        Title = title;
        var chain = new List<ChainItem>();
        Flatten(details, 0, chain);
        Chain = chain;
        OnPropertyChanged(nameof(Chain));
        StatusText = null;
        Details = details;
    }

    private static void Flatten(ExceptionDetails e, int depth, List<ChainItem> into)
    {
        if (depth > MaxInnerDepth) return;
        into.Add(new ChainItem(depth, e.TypeName, e.Message, e.StackTrace));
        foreach (var inner in e.Inner) Flatten(inner, depth + 1, into);
    }

    /// <summary>Maps a DAP <c>ExceptionInfoResponse</c> body (<c>exceptionId</c>, <c>description</c>, <c>breakMode</c>, <c>details</c>).</summary>
    internal static ExceptionDetails Parse(JsonElement body, out string title)
    {
        string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        title = Str(body, "breakMode") switch
        {
            "unhandled" => "Unhandled Exception",
            "userUnhandled" => "User-Unhandled Exception",
            _ => "Exception Thrown",
        };
        var details = body.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
        var type = Str(details, "fullTypeName") ?? Str(details, "typeName") ?? Str(body, "exceptionId") ?? "Exception";
        var message = Str(details, "message") ?? Str(body, "description") ?? "";
        return new ExceptionDetails(type, message, Clip(Str(details, "stackTrace")), ParseInner(details, 0));
    }

    private static List<ExceptionDetails> ParseInner(JsonElement details, int depth)
    {
        var result = new List<ExceptionDetails>();
        if (depth >= MaxInnerDepth || details.ValueKind != JsonValueKind.Object || !details.TryGetProperty("innerException", out var inner) || inner.ValueKind != JsonValueKind.Array) return result;
        foreach (var e in inner.EnumerateArray().Take(8))
        {
            string? Str(string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            result.Add(new ExceptionDetails(Str("fullTypeName") ?? Str("typeName") ?? "Exception", Str("message") ?? "", Clip(Str("stackTrace")), ParseInner(e, depth + 1)));
        }
        return result;
    }

    private static string? Clip(string? text) => text is { Length: > MaxStackChars } ? text[..MaxStackChars] + "\n…" : text;

    private async Task CopyAsync()
    {
        if (_details is { } d && _s.Window is { } window) await window.CopyToClipboardAsync(d.ToText());
    }

    private async Task ContinueAsync()
    {
        var session = _s.Debug.ActiveSession;
        if (session is null) return;
        if (_s.Commands is { } commands && commands.Has(Core.Platform.KnownCommands.DebugContinue))
            await commands.ExecuteAsync(Core.Platform.KnownCommands.DebugContinue);
        else await _s.Ui.OnUi(session.ContinueAsync(null, _cts.Token), _cts.Token);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _tracker.Dispose();
        try { _cts.Cancel(); }
        catch (AggregateException) { }
        _cts.Dispose();
        Chain = [];
        _details = null;
    }
}
