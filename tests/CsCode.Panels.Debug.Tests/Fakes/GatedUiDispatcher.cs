using Avalonia.Threading;
using CsCode.Ui.Threading;

namespace CsCode.Panels.Debug.Tests.Fakes;

/// <summary>
/// The Avalonia UI dispatcher, except that Background-priority posts (node creation and chunked row inserts of the variables tree) can be
/// held back and released on demand. Lets a test put another UI action exactly between "the adapter answered" and "the rows went in".
/// </summary>
public sealed class GatedUiDispatcher : UiDispatcherBase
{
    private readonly object _gate = new();
    private readonly List<Action> _held = new();
    private bool _hold;

    public bool HoldBackground
    {
        get { lock (_gate) return _hold; }
        set { lock (_gate) _hold = value; }
    }

    public int HeldCount { get { lock (_gate) return _held.Count; } }

    public override bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

    protected override void PostCore(Action action, UiPriority priority)
    {
        lock (_gate)
        {
            if (_hold && priority == UiPriority.Background)
            {
                _held.Add(action);
                return;
            }
        }
        Dispatcher.UIThread.Post(action, priority switch
        {
            UiPriority.Input => DispatcherPriority.Input,
            UiPriority.Background => DispatcherPriority.Background,
            UiPriority.Idle => DispatcherPriority.ApplicationIdle,
            _ => DispatcherPriority.Normal,
        });
    }

    /// <summary>Posts everything held so far (in order); new Background posts are still held while <see cref="HoldBackground"/> is set.</summary>
    public void ReleaseAll()
    {
        Action[] held;
        lock (_gate)
        {
            held = [.. _held];
            _held.Clear();
        }
        foreach (var action in held) Dispatcher.UIThread.Post(action, DispatcherPriority.Background);
    }
}
