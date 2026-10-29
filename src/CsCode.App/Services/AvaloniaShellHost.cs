using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using CsCode.Core.Platform;

namespace CsCode.App.Services;

/// <summary>Native dialogs, clipboard and URI launching through the main window's <see cref="TopLevel"/> (StorageProvider / Clipboard / Launcher).</summary>
public sealed class AvaloniaShellHost(Func<TopLevel?> topLevel) : IShellHost
{
    public async ValueTask<IReadOnlyList<Uri>?> PickOpenAsync(OpenDialogOptions options, CancellationToken ct)
    {
        if (topLevel() is not { } tl) return null;
        var sp = tl.StorageProvider;
        IStorageFolder? start = options.DefaultUri is { } d ? await sp.TryGetFolderFromPathAsync(d).ConfigureAwait(true) : null;
        if (options.CanSelectFolders && !options.CanSelectFiles)
        {
            var folders = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = options.Title, AllowMultiple = options.CanSelectMany, SuggestedStartLocation = start,
            }).ConfigureAwait(true);
            return folders.Count == 0 ? null : folders.Select(f => f.Path).ToArray();
        }
        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = options.Title, AllowMultiple = options.CanSelectMany, SuggestedStartLocation = start,
            FileTypeFilter = options.Filters?.Select(f => new FilePickerFileType(f.Name) { Patterns = f.Extensions.Select(e => "*." + e.TrimStart('.')).ToArray() }).ToArray(),
        }).ConfigureAwait(true);
        return files.Count == 0 ? null : files.Select(f => f.Path).ToArray();
    }

    public async ValueTask<Uri?> PickSaveAsync(SaveDialogOptions options, CancellationToken ct)
    {
        if (topLevel() is not { } tl) return null;
        var file = await tl.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = options.Title,
            FileTypeChoices = options.Filters?.Select(f => new FilePickerFileType(f.Name) { Patterns = f.Extensions.Select(e => "*." + e.TrimStart('.')).ToArray() }).ToArray(),
        }).ConfigureAwait(true);
        return file?.Path;
    }

    public async ValueTask CopyTextAsync(string text)
    {
        if (topLevel()?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text).ConfigureAwait(true);
    }

    public async ValueTask<bool> LaunchAsync(Uri uri)
    {
        if (topLevel()?.Launcher is not { } launcher) return false;
        return await launcher.LaunchUriAsync(uri).ConfigureAwait(true);
    }
}
