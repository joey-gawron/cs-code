using System.Text;
using Avalonia.Headless.XUnit;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.Documents;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.View;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>
/// Clipboard brief A6: a document of a remote workspace (an <c>ssh:</c> URI served by a remote file system) copies to and pastes from the LOCAL
/// clipboard service; the remote file system sees reads of the document only, never clipboard traffic.
/// </summary>
public class RemoteClipboardTests
{
    private sealed class RemoteFileSystem : IFileSystem
    {
        private readonly Dictionary<Uri, byte[]> _files = new();
        public List<string> Calls { get; } = new();
        public void Put(Uri uri, string text) => _files[uri] = Encoding.UTF8.GetBytes(text);

        public ValueTask<FileStat?> StatAsync(Uri uri, CancellationToken ct = default)
        {
            Calls.Add("stat " + uri);
            return ValueTask.FromResult<FileStat?>(_files.TryGetValue(uri, out var b) ? new FileStat(FileType.File, b.Length, DateTimeOffset.UtcNow) : null);
        }

        public ValueTask<byte[]> ReadFileAsync(Uri uri, CancellationToken ct = default)
        {
            Calls.Add("read " + uri);
            return ValueTask.FromResult(_files[uri]);
        }

        public ValueTask WriteFileAsync(Uri uri, ReadOnlyMemory<byte> content, bool createParents = true, CancellationToken ct = default) { Calls.Add("write " + uri); _files[uri] = content.ToArray(); return ValueTask.CompletedTask; }
        public ValueTask<IReadOnlyList<DirectoryEntry>> ReadDirectoryAsync(Uri uri, CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<DirectoryEntry>>([]);
        public ValueTask CreateDirectoryAsync(Uri uri, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask DeleteAsync(Uri uri, bool recursive = false, bool useTrash = true, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask RenameAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask CopyAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    private sealed class NoWatcher : IFileWatcher
    {
        public IDisposable Watch(Uri root, bool recursive, IReadOnlyList<string>? excludeGlobs, Action<IReadOnlyList<FileChange>> onChanges) => Disposable.Empty;
    }

    [AvaloniaFact]
    public async Task Copy_and_paste_in_a_remote_document_use_the_local_clipboard()
    {
        var remote = new RemoteFileSystem();
        var clipboard = new InMemoryClipboardService();
        var uri = new Uri("ssh://build-host/home/dev/src/main.cs");
        remote.Put(uri, "remote line one\nremote line two\n");
        using var host = ViewHost.Create(s =>
        {
            s.AddSingleton<IFileSystem>(remote);
            s.AddSingleton<IFileWatcher>(new NoWatcher());
            s.AddSingleton<IClipboardService>(clipboard);
        });
        var doc = (EditorDocument)await host.Factory.OpenDocumentAsync(uri);
        var view = (CodeEditorControl)host.Factory.CreateView(doc);
        var window = new Avalonia.Controls.Window { Width = 800, Height = 400, Content = view };
        window.Show();
        ViewHost.Pump();
        var readsBefore = remote.Calls.Count;

        view.Controller.SetSelection(TextRange.FromLines(0, 7, 0, 11)); // "line"
        await view.Clipboard.CopyAsync();
        Assert.Equal("line", clipboard.Content?.Text);
        clipboard.SetExternal("LOCAL");
        view.Controller.SetSelection(TextRange.At(1, 0));
        await view.Clipboard.PasteAsync();
        Assert.Equal("remote line one\nLOCALremote line two\n", doc.GetText());
        Assert.Equal(readsBefore, remote.Calls.Count); // the remote side saw nothing of the clipboard traffic
        window.Close();
    }
}
