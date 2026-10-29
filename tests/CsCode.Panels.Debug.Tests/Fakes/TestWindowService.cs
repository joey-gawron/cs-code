using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;

namespace CsCode.Panels.Debug.Tests.Fakes;

/// <summary>Window service that records the clipboard, status-bar messages and input-box prompts, answering prompts from a queue.</summary>
public sealed class TestWindowService : IWindowService
{
    public List<string> Clipboard { get; } = new();
    public List<string> StatusMessages { get; } = new();
    public List<InputBoxOptions?> InputPrompts { get; } = new();
    public Queue<string?> InputResponses { get; } = new();

    public List<string> Messages { get; } = new();

    public ValueTask<string?> ShowMessageAsync(MessageSeverity severity, string message, IReadOnlyList<MessageAction>? actions = null, CancellationToken ct = default)
    {
        lock (Messages) Messages.Add(message);
        return ValueTask.FromResult<string?>(null);
    }
    public ValueTask<QuickPickItem?> ShowQuickPickAsync(IReadOnlyList<QuickPickItem> items, QuickPickOptions? options = null, CancellationToken ct = default) => ValueTask.FromResult<QuickPickItem?>(null);
    public ValueTask<IReadOnlyList<QuickPickItem>?> ShowQuickPickManyAsync(IReadOnlyList<QuickPickItem> items, QuickPickOptions? options = null, CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<QuickPickItem>?>(null);

    public ValueTask<string?> ShowInputBoxAsync(InputBoxOptions? options = null, CancellationToken ct = default)
    {
        InputPrompts.Add(options);
        return ValueTask.FromResult(InputResponses.Count > 0 ? InputResponses.Dequeue() : null);
    }

    public ValueTask<IReadOnlyList<Uri>?> ShowOpenDialogAsync(OpenDialogOptions options, CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<Uri>?>(null);
    public ValueTask<Uri?> ShowSaveDialogAsync(SaveDialogOptions options, CancellationToken ct = default) => ValueTask.FromResult<Uri?>(null);
    public Task<T> WithProgressAsync<T>(string title, bool cancellable, Func<IProgressReporter, CancellationToken, Task<T>> work) => work(new NoProgress(), CancellationToken.None);

    public IDisposable SetStatusBarMessage(string text, TimeSpan? timeout = null)
    {
        lock (StatusMessages) StatusMessages.Add(text);
        return Disposable.Empty;
    }

    public ValueTask<bool> OpenExternalAsync(Uri uri) => ValueTask.FromResult(true);

    public ValueTask CopyToClipboardAsync(string text)
    {
        lock (Clipboard) Clipboard.Add(text);
        return ValueTask.CompletedTask;
    }

    private sealed class NoProgress : IProgressReporter { public void Report(string? message = null, double? percent = null) { } }
}
