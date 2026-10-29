using CsCode.Core.Platform;
using CsCode.TestDoubles;

namespace CsCode.Editor.Tests.Support;

/// <summary>The shared <see cref="RecordingWindowService"/> plus a record of what was opened externally (browser links).</summary>
public sealed class ExternalOpenRecorder(RecordingWindowService inner) : IWindowService
{
    public List<Uri> OpenedExternally { get; } = new();

    public ValueTask<bool> OpenExternalAsync(Uri uri)
    {
        lock (OpenedExternally) OpenedExternally.Add(uri);
        return ValueTask.FromResult(true);
    }

    public ValueTask<string?> ShowMessageAsync(MessageSeverity severity, string message, IReadOnlyList<MessageAction>? actions = null, CancellationToken ct = default) => inner.ShowMessageAsync(severity, message, actions, ct);
    public ValueTask<QuickPickItem?> ShowQuickPickAsync(IReadOnlyList<QuickPickItem> items, QuickPickOptions? options = null, CancellationToken ct = default) => inner.ShowQuickPickAsync(items, options, ct);
    public ValueTask<IReadOnlyList<QuickPickItem>?> ShowQuickPickManyAsync(IReadOnlyList<QuickPickItem> items, QuickPickOptions? options = null, CancellationToken ct = default) => inner.ShowQuickPickManyAsync(items, options, ct);
    public ValueTask<string?> ShowInputBoxAsync(InputBoxOptions? options = null, CancellationToken ct = default) => inner.ShowInputBoxAsync(options, ct);
    public ValueTask<IReadOnlyList<Uri>?> ShowOpenDialogAsync(OpenDialogOptions options, CancellationToken ct = default) => inner.ShowOpenDialogAsync(options, ct);
    public ValueTask<Uri?> ShowSaveDialogAsync(SaveDialogOptions options, CancellationToken ct = default) => inner.ShowSaveDialogAsync(options, ct);
    public Task<T> WithProgressAsync<T>(string title, bool cancellable, Func<IProgressReporter, CancellationToken, Task<T>> work) => inner.WithProgressAsync(title, cancellable, work);
    public IDisposable SetStatusBarMessage(string text, TimeSpan? timeout = null) => inner.SetStatusBarMessage(text, timeout);
    public ValueTask CopyToClipboardAsync(string text) => inner.CopyToClipboardAsync(text);
}
