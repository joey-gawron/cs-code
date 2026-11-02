using CsCode.Core.Platform;
using CsCode.Ui.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.App.Docking;

/// <summary>
/// The shell's <see cref="IViewActivator"/>: panels call it from their commands (Find in Files, Open Settings, Open Keyboard Shortcuts, Find in
/// Folder) to bring a view to the front. A tool window is shown in its well (<see cref="ToolWindowManager.Show"/> creates the view); a
/// document-location window opens, or re-activates, its singleton document tab. Callable from any thread (one dispatcher hop, inline on the UI thread).
/// The window manager and the well are resolved on first use, never in the constructor: panels resolve this service while the tool windows are
/// being enumerated, and the window manager is built from that very enumeration.
/// </summary>
public sealed class ShellViewActivator(IServiceProvider services, IUiDispatcher ui) : IViewActivator
{
    public async ValueTask<bool> ShowViewAsync(string viewId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await ui.InvokeAsync(() => Show(viewId)).ConfigureAwait(false);
    }

    public async ValueTask<bool> CloseViewAsync(string viewId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await (await ui.InvokeAsync(() => CloseAsync(viewId)).ConfigureAwait(false)).ConfigureAwait(false);
    }

    /// <summary>UI thread: closes the document view <paramref name="viewId"/>'s tab (<see cref="DocumentWell.OpenView"/> keys it <c>view:id</c>).</summary>
    private async Task<bool> CloseAsync(string viewId)
    {
        var well = services.GetRequiredService<DocumentWell>();
        if (well.Tabs.FirstOrDefault(t => t.Id == "view:" + viewId) is not { } tab) return false;
        return await well.CloseAsync(tab, prompt: false).ConfigureAwait(true);
    }

    /// <summary>UI thread: shows the tool window, or opens the document view, <paramref name="viewId"/>. False when no such view is registered.</summary>
    public bool Show(string viewId)
    {
        ui.AssertUiThread();
        var tools = services.GetRequiredService<ToolWindowManager>();
        if (tools.Get(viewId) is not null)
        {
            tools.Show(viewId);
            return true;
        }
        if (tools.GetDocumentView(viewId) is not { } spec) return false;
        services.GetRequiredService<DocumentWell>().OpenView(spec.Id, spec.Title, () => spec.CreateView(services), spec.IconKey);
        return true;
    }
}
