using Avalonia.Input;
using Avalonia.Interactivity;
using CsCode.Core.Lifecycle;
using CsCode.Profiling;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.App.Services;

/// <summary>
/// The shell's part of memory profiling (docs/MEMORY.md): starts the memory monitor with the window (<c>StartCsCodeProfiling</c>: the sampling
/// thread and the "Memory" output channel), and reports the user's input — keys, text, pointer — as activity, so the idle trim
/// (<c>cscode.memory.trimOnIdle</c>) waits until the user has stopped. The handlers are tunnel handlers on the window that also see handled events
/// and cost one timestamp write per event. Disposing removes them and stops the channel writer; the container stops the monitor.
/// </summary>
internal static class ProfilingShell
{
    public static IDisposable Attach(IServiceProvider services)
    {
        var started = services.StartCsCodeProfiling();
        if (services.GetService<IUserActivityMonitor>() is not { } activity || services.GetService<WindowHolder>()?.Window is not { } window) return started;
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
            started.Dispose();
        });
    }
}
