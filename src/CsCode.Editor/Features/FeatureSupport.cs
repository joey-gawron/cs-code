using AvaloniaEdit.Document;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Editor.View;
using Microsoft.Extensions.Logging;

namespace CsCode.Editor.Features;

/// <summary>Small helpers shared by the language-feature components.</summary>
internal static class FeatureSupport
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Calls a provider OFF the UI thread (a provider that does synchronous work before its first await must not do it on the UI thread) with
    /// a deadline and the caller's cancellation. Cancellation, timeout and provider exceptions all end as <c>default</c>: a bad provider is
    /// "no result", never an editor failure. The continuation resumes on the UI thread.
    /// </summary>
    public static async Task<T?> CallAsync<T>(CodeEditorControl view, Func<CancellationToken, ValueTask<T?>> call, CancellationToken ct, TimeSpan? timeout = null)
    {
        try
        {
            var limit = timeout ?? DefaultTimeout;
            return await Task.Run(async () =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(limit);
                return await call(linked.Token).ConfigureAwait(false);
            }, CancellationToken.None).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return default;
        }
        catch (Exception ex)
        {
            view.Services.LoggerFactory.CreateLogger("CsCode.Editor.Providers").LogDebug(ex, "Language provider call failed");
            return default;
        }
    }

    /// <summary>
    /// Applies a workspace edit through <see cref="Core.Platform.INavigationService"/> (open documents edited in-editor as one undo group, closed
    /// files on disk); standalone — no shell — only the edits of this view's document are applied.
    /// </summary>
    public static async Task<bool> ApplyWorkspaceEditAsync(CodeEditorControl view, WorkspaceEdit edit, string? label, CancellationToken ct = default)
    {
        if (edit.IsEmpty) return false;
        if (view.Services.Navigation is { } navigation) return await navigation.ApplyWorkspaceEditAsync(edit, label, ct).ConfigureAwait(true);
        if (view.Document.IsReadOnly) return false;
        return edit.Changes.TryGetValue(view.Document.Uri, out var edits) && view.Document.ApplyEdits(edits.ToList());
    }

    /// <summary>Opens <paramref name="uri"/> at <paramref name="selection"/>: in this view when it is the same document (or there is no shell), otherwise through the shell.</summary>
    public static async Task OpenLocationAsync(CodeEditorControl view, Uri uri, TextRange selection)
    {
        if (view.Services.Navigation is { } nav && !SameFile(uri, view.Document.Uri))
        {
            await nav.OpenAsync(uri, selection).ConfigureAwait(true);
            return;
        }
        if (!SameFile(uri, view.Document.Uri)) return;
        view.Controller.SetSelection(selection, reveal: false);
        view.Controller.Reveal(selection, center: true);
        view.Controller.Focus();
    }

    public static bool SameFile(Uri a, Uri b) => a == b || (a.IsFile && b.IsFile && string.Equals(a.LocalPath, b.LocalPath, StringComparison.Ordinal));

    public static void Status(CodeEditorControl view, string text) => view.Services.Window?.SetStatusBarMessage(text, TimeSpan.FromSeconds(4));

    public static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>The identifier at (or touching the left of) <paramref name="offset"/>; start == end when there is none.</summary>
    public static (int Start, int End) WordAt(TextDocument doc, int offset)
    {
        offset = Math.Clamp(offset, 0, doc.TextLength);
        var a = offset;
        var b = offset;
        while (a > 0 && IsWordChar(doc.GetCharAt(a - 1))) a--;
        while (b < doc.TextLength && IsWordChar(doc.GetCharAt(b))) b++;
        return (a, b);
    }

    public static TextRange RangeOf(CodeEditorControl view, int start, int end) => new(view.PositionOf(start), view.PositionOf(end));

    /// <summary>The selection, or the caret as an empty range.</summary>
    public static TextRange SelectionOrCaret(CodeEditorControl view) => view.Controller.Selections[0];
}
