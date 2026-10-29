using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;

namespace CsCode.Panels.Search.Tests;

/// <summary>
/// Behaves like the shell's toast-based <see cref="IWindowService"/>: a message WITH actions waits for <see cref="OnConfirm"/> (the user's
/// click, which may come much later), a message WITHOUT actions resolves only when <see cref="ResolveToasts"/> is called (the shell demotes it
/// 8-20 s later, or never when newer toasts push it off the stack).
/// </summary>
public sealed class ScriptedWindowService : IWindowService
{
    private readonly TaskCompletionSource _toasts = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<(MessageSeverity Severity, string Message)> Messages { get; } = new();
    public List<string> StatusMessages { get; } = new();

    /// <summary>Runs when a confirmation is shown; its result is the clicked action. Null answers nothing (dismissed).</summary>
    public Func<Task<string?>>? OnConfirm { get; set; }

    public async ValueTask<string?> ShowMessageAsync(MessageSeverity severity, string message, IReadOnlyList<MessageAction>? actions = null, CancellationToken ct = default)
    {
        lock (Messages) Messages.Add((severity, message));
        if (actions is { Count: > 0 }) return OnConfirm is { } confirm ? await confirm() : null;
        await _toasts.Task;
        return null;
    }

    public void ResolveToasts() => _toasts.TrySetResult();

    public ValueTask<QuickPickItem?> ShowQuickPickAsync(IReadOnlyList<QuickPickItem> items, QuickPickOptions? options = null, CancellationToken ct = default) =>
        ValueTask.FromResult<QuickPickItem?>(null);
    public ValueTask<IReadOnlyList<QuickPickItem>?> ShowQuickPickManyAsync(IReadOnlyList<QuickPickItem> items, QuickPickOptions? options = null, CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<QuickPickItem>?>(null);
    public ValueTask<string?> ShowInputBoxAsync(InputBoxOptions? options = null, CancellationToken ct = default) => ValueTask.FromResult<string?>(null);
    public ValueTask<IReadOnlyList<Uri>?> ShowOpenDialogAsync(OpenDialogOptions options, CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<Uri>?>(null);
    public ValueTask<Uri?> ShowSaveDialogAsync(SaveDialogOptions options, CancellationToken ct = default) => ValueTask.FromResult<Uri?>(null);
    public Task<T> WithProgressAsync<T>(string title, bool cancellable, Func<IProgressReporter, CancellationToken, Task<T>> work) => work(new NullProgress(), CancellationToken.None);
    public IDisposable SetStatusBarMessage(string text, TimeSpan? timeout = null) { lock (StatusMessages) StatusMessages.Add(text); return Disposable.Empty; }
    public ValueTask<bool> OpenExternalAsync(Uri uri) => ValueTask.FromResult(true);
    public ValueTask CopyToClipboardAsync(string text) => ValueTask.CompletedTask;

    private sealed class NullProgress : IProgressReporter { public void Report(string? message = null, double? percent = null) { } }
}

/// <summary>Records the name of the thread every directory listing and file read is requested from (thread-placement tests).</summary>
public sealed class ThreadRecordingFileSystem(IFileSystem inner) : IFileSystem
{
    private readonly List<string> _threads = new();

    public IReadOnlyList<string> Threads { get { lock (_threads) return _threads.ToArray(); } }

    private void Record() { lock (_threads) _threads.Add(Thread.CurrentThread.Name ?? "<unnamed>"); }

    public ValueTask<FileStat?> StatAsync(Uri uri, CancellationToken ct = default) => inner.StatAsync(uri, ct);
    public ValueTask<IReadOnlyList<DirectoryEntry>> ReadDirectoryAsync(Uri uri, CancellationToken ct = default) { Record(); return inner.ReadDirectoryAsync(uri, ct); }
    public ValueTask<byte[]> ReadFileAsync(Uri uri, CancellationToken ct = default) { Record(); return inner.ReadFileAsync(uri, ct); }
    public ValueTask WriteFileAsync(Uri uri, ReadOnlyMemory<byte> content, bool createParents = true, CancellationToken ct = default) => inner.WriteFileAsync(uri, content, createParents, ct);
    public ValueTask CreateDirectoryAsync(Uri uri, CancellationToken ct = default) => inner.CreateDirectoryAsync(uri, ct);
    public ValueTask DeleteAsync(Uri uri, bool recursive = false, bool useTrash = true, CancellationToken ct = default) => inner.DeleteAsync(uri, recursive, useTrash, ct);
    public ValueTask RenameAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default) => inner.RenameAsync(source, target, overwrite, ct);
    public ValueTask CopyAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default) => inner.CopyAsync(source, target, overwrite, ct);
}
