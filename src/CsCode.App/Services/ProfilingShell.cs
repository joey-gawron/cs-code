using Avalonia.Input;
using Avalonia.Interactivity;
using CsCode.Core.Lifecycle;
using CsCode.Profiling;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.App.Services;

/// <summary>
/// The shell's part of memory profiling (docs/MEMORY.md): starts the memory monitor with the window (<c>StartCsCodeProfiling</c>: the sampling
/// thread and the "Memory" output channel), and reports the user's input — keys, text, pointer — as activity, so the idle trim
/// (<c>cscode.memory.trimOnIdle</c>) waits until the user has stopped. The handlers are tunnel handlers on every shell window (the main one and each
/// floating one: input is routed through the window that holds the focused control) that also see handled events and cost one timestamp write
/// per event. Starting also restarts the monitor's growth grace whenever the workspace's folders change. Disposing removes the handlers, that
/// subscription and the channel writer; the container stops the monitor.
/// </summary>
internal static class ProfilingShell
{
    /// <summary>Starts the monitor's shell part; dispose to stop the channel writer.</summary>
    public static IDisposable Start(IServiceProvider services) => services.StartCsCodeProfiling();

    /// <summary>Reports <paramref name="window"/>'s input as user activity. Dispose to remove the handlers.</summary>
    public static IDisposable AttachWindow(IUserActivityMonitor activity, Interactive window)
    {
        EventHandler<KeyEventArgs> key = (_, _) => activity.NotifyActivity();
        EventHandler<TextInputEventArgs> text = (_, _) => activity.NotifyActivity();
        EventHandler<PointerPressedEventArgs> press = (_, _) => activity.NotifyActivity();
        EventHandler<PointerEventArgs> move = (_, _) => activity.NotifyActivity();
        EventHandler<PointerWheelEventArgs> wheel = (_, _) => activity.NotifyActivity();
        const RoutingStrategies tunnel = RoutingStrategies.Tunnel;
        window.AddHandler(InputElement.KeyDownEvent, key, tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.TextInputEvent, text, tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.PointerPressedEvent, press, tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.PointerMovedEvent, move, tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.PointerWheelChangedEvent, wheel, tunnel, handledEventsToo: true);
        return Disposable.Create(() =>
        {
            window.RemoveHandler(InputElement.KeyDownEvent, key);
            window.RemoveHandler(InputElement.TextInputEvent, text);
            window.RemoveHandler(InputElement.PointerPressedEvent, press);
            window.RemoveHandler(InputElement.PointerMovedEvent, move);
            window.RemoveHandler(InputElement.PointerWheelChangedEvent, wheel);
        });
    }
}
