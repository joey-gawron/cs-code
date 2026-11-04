using Avalonia.Input;
using Avalonia.Platform.Storage;
using CsCode.Platform.Io;

namespace CsCode.App.ToolWindows.Explorer;

/// <summary>What an Explorer drag carries inside CsCode: the URIs of the items dragged (not the tree nodes, which a refresh replaces).</summary>
public sealed record ExplorerDragItems(IReadOnlyList<Uri> Uris);

/// <summary>
/// The data of a drag that starts in the Explorer, and what a drop anywhere in CsCode reads from a drag. An Explorer drag carries its items in an
/// in-process format (only CsCode reads it: a drop in the Explorer moves or copies them, a drop on the editor area opens the files) and, for
/// local items, as files (<see cref="DataFormat.File"/>), which other applications take: dropped in Finder or a Linux file manager they are copied
/// there. A drag from another application carries files only.
/// </summary>
public static class ExplorerDrag
{
    public static readonly DataFormat<ExplorerDragItems> Format = DataFormat.CreateInProcessFormat<ExplorerDragItems>("cscode-explorer-items");

    /// <summary>
    /// The effects an Explorer drag offers: Copy only. A drop in another application must never take the file out of the workspace (Finder
    /// moves what is offered as movable on the same volume), so the move inside the Explorer is decided by the drop target from the modifier keys,
    /// not by the platform's negotiated effect.
    /// </summary>
    public const DragDropEffects Offered = DragDropEffects.Copy;

    /// <summary>
    /// The items of the drag the Explorer is running, while it runs: a platform that does not hand the in-process data back to a drop in the same
    /// process still lets the drop see them. One drag at a time; cleared when it ends, so it holds only URIs, briefly.
    /// </summary>
    internal static ExplorerDragItems? Current { get; set; }

    /// <summary>
    /// The data an Explorer drag of <paramref name="uris"/> hands to the platform: the items for CsCode, and <paramref name="files"/> (the local
    /// ones as the platform's storage items, <see cref="StorageItemsAsync"/>) for other applications.
    /// </summary>
    public static DataTransfer Create(IReadOnlyList<Uri> uris, IReadOnlyList<IStorageItem> files)
    {
        var data = new DataTransfer();
        var payload = new ExplorerDragItems(uris);
        foreach (var file in files)
        {
            var item = DataTransferItem.CreateFile(file);
            if (data.Items.Count == 0) item.Set(Format, payload);
            data.Add(item);
        }
        if (data.Items.Count == 0) data.Add(DataTransferItem.Create(Format, payload)); // nothing local (a remote folder): only CsCode can read it
        return data;
    }

    /// <summary>
    /// The platform's storage items of the local <paramref name="uris"/> (Avalonia's file items cannot be made outside it): what Finder or a file
    /// manager takes from the drag. The platform looks each path up (one stat each; macOS's provider also checks its sandbox scope there), so this
    /// is the drag's only file access on the UI thread, once per drag. Paths it does not know are left out; without a provider (headless) there
    /// are none.
    /// </summary>
    public static async Task<IReadOnlyList<IStorageItem>> StorageItemsAsync(IStorageProvider? storage, IReadOnlyList<Uri> uris)
    {
        var items = new List<IStorageItem>();
        if (storage is null) return items;
        foreach (var uri in uris)
        {
            if (!uri.IsFile) continue;
            try
            {
                IStorageItem? item = uri.AbsoluteUri.EndsWith('/')
                    ? await storage.TryGetFolderFromPathAsync(uri).ConfigureAwait(true)
                    : await storage.TryGetFileFromPathAsync(uri).ConfigureAwait(true);
                if (item is not null) items.Add(item);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                // not exportable: the drag still carries it for CsCode
            }
        }
        return items;
    }

    /// <summary>The Explorer items a drag carries, or null when it did not start in the Explorer.</summary>
    public static IReadOnlyList<Uri>? Items(IDataTransfer data) => (data.TryGetValue(Format) ?? Current)?.Uris;

    /// <summary>The local files and folders a drag from another application carries (folders as <c>file:</c> URIs ending in '/').</summary>
    public static IReadOnlyList<Uri> Files(IDataTransfer data)
    {
        if (data.TryGetFiles() is not { Length: > 0 } items) return [];
        var uris = new List<Uri>(items.Length);
        foreach (var item in items)
            if (item.TryGetLocalPath() is { Length: > 0 } path)
                uris.Add(item is IStorageFolder ? FileUri.FromDirectoryPath(path) : FileUri.FromPath(Path.TrimEndingDirectorySeparator(path)));
        return uris;
    }

    /// <summary>Whether a drop copies rather than moves: ⌥ held on macOS, Ctrl elsewhere (VS Code's keys).</summary>
    public static bool IsCopy(KeyModifiers modifiers, bool mac) => (modifiers & (mac ? KeyModifiers.Alt : KeyModifiers.Control)) != 0;
}
