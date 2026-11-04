using System.Text;
using CsCode.Core.History;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Scm;
using CsCode.Core.Text;
using CsCode.Core.Timeline;
using CsCode.Panels.Timeline.LocalHistory;
using CsCode.Ui.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CsCode.Panels.Timeline;

/// <summary>
/// The Timeline window's composition point: the providers, VS Code's local history commands (<c>workbench.action.localHistory.*</c>) and the
/// Timeline's own (<c>timeline.focus</c>, <c>timeline.refresh</c>, <c>timeline.toggleFollowActiveEditor</c>), and the actions behind the rows'
/// menus (compare, restore, rename, delete). Resolved at startup through the tool window, which is what registers the commands and arms the save
/// hook (the local history service it holds). Every service but the local history is optional.
/// </summary>
public sealed class TimelineHost : IDisposable
{
    public const string ViewId = "timeline";
    public const string Focus = "timeline.focus";
    public const string Refresh = "timeline.refresh";
    public const string ToggleFollow = "timeline.toggleFollowActiveEditor";
    public const string Create = "workbench.action.localHistory.create";
    public const string RestoreViaPicker = "workbench.action.localHistory.restoreViaPicker";
    public const string DeleteAll = "workbench.action.localHistory.deleteAll";
    public const string CompareWithFile = "workbench.action.localHistory.compareWithFile";
    public const string CompareWithPrevious = "workbench.action.localHistory.compareWithPrevious";
    public const string Open = "workbench.action.localHistory.open";
    public const string Restore = "workbench.action.localHistory.restore";
    public const string Rename = "workbench.action.localHistory.rename";
    public const string Delete = "workbench.action.localHistory.delete";
    /// <summary>CsCode's: every entry of the Timeline's file (VS Code's Delete All deletes the history of every file).</summary>
    public const string DeleteAllOfFile = "cscode.localHistory.deleteAllOfFile";

    private const string ExcludedSourcesKey = "timeline.excludedSources";
    private const string Category = "Local History";

    private readonly IServiceProvider _sp;
    private readonly DisposableStore _store = new();
    private readonly IUiDispatcher _ui;
    private readonly Lazy<IReadOnlyList<ITimelineProvider>> _providers;
    private readonly HashSet<string> _excluded = new(StringComparer.Ordinal);
    private bool _follow = true;
    private bool _disposed;

    public TimelineHost(IServiceProvider services, ILocalHistoryService? history, IUiDispatcher? ui = null)
    {
        _sp = services;
        History = history;
        _ui = ui ?? services.GetService<IUiDispatcher>() ?? AvaloniaUiDispatcher.Instance;
        Windows = services.GetService<IWindowService>();
        Navigation = services.GetService<INavigationService>();
        Diff = services.GetService<IDiffEditorService>();
        Documents = services.GetService<IDocumentManager>();
        Config = services.GetService<IConfigurationService>();
        FileSystem = services.GetService<IFileSystem>() ?? services.GetService<IWorkspaceService>()?.FileSystem;
        State = services.GetService<IStateStorage>();
        Time = services.GetService<TimeProvider>() ?? TimeProvider.System;
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger("CsCode.Timeline");
        if (logger is not null) Log = message => logger.LogInformation("{Message}", message);
        // Resolved on first use: a provider's module may itself resolve services that are being built at startup.
        _providers = new(() => services.GetServices<ITimelineProvider>().ToArray());
        if (State?.Get<string[]>(ExcludedSourcesKey) is { } excluded) _excluded.UnionWith(excluded);
        if (services.GetService<ICommandService>() is { } commands) RegisterCommands(commands);
    }

    public ILocalHistoryService? History { get; }
    public IWindowService? Windows { get; }
    public INavigationService? Navigation { get; }
    public IDiffEditorService? Diff { get; }
    public IDocumentManager? Documents { get; }
    public IConfigurationService? Config { get; }
    public IFileSystem? FileSystem { get; }
    public IStateStorage? State { get; }
    public TimeProvider Time { get; }
    internal Action<string>? Log { get; }

    public IReadOnlyList<ITimelineProvider> Providers => _providers.Value;

    /// <summary>The window's view-model while it is shown.</summary>
    public TimelineViewModel? Live { get; private set; }

    /// <summary>The file the Timeline showed last (shown again when the window reopens without an active editor).</summary>
    internal Uri? LastResource { get; set; }

    internal TimelineViewModel CreateViewModel()
    {
        var vm = new TimelineViewModel(this, _ui);
        Live = vm;
        return vm;
    }

    internal void ReleaseViewModel(TimelineViewModel vm)
    {
        if (ReferenceEquals(Live, vm)) Live = null;
        vm.Dispose();
    }

    // ------------------------------------------------------------------------------------------------ filter and follow

    public bool IsSourceIncluded(string providerId) => !_excluded.Contains(providerId);

    /// <summary>Turns a source on or off in the Timeline (VS Code's Filter Timeline menu); remembered across restarts.</summary>
    public void SetSourceIncluded(string providerId, bool included)
    {
        if (included ? !_excluded.Remove(providerId) : !_excluded.Add(providerId)) return;
        State?.Set(ExcludedSourcesKey, _excluded.Count == 0 ? null : _excluded.Order(StringComparer.Ordinal).ToArray());
        _ = Live?.ReloadAsync();
    }

    public bool FollowActiveEditor
    {
        get => _follow;
        set
        {
            if (_follow == value) return;
            _follow = value;
            Live?.OnFollowChanged(Navigation?.ActiveDocument?.Uri);
        }
    }

    // ------------------------------------------------------------------------------------------------ commands

    private void RegisterCommands(ICommandService commands)
    {
        void Reg(string id, string title, string? category, Func<CommandInvocation, Task> run, string? enablement = null)
        {
            if (commands.Has(id)) return;
            _store.Add(commands.Register(id, async inv => { await run(inv).ConfigureAwait(true); return null; }, new CommandMetadata(id, title, category, Enablement: enablement)));
        }
        const string enabled = "config." + LocalHistorySettings.Enabled;
        Reg(Focus, "Focus on Timeline View", "View", _ => ShowViewAsync());
        Reg(Refresh, "Refresh", "Timeline", _ => Live?.ReloadAsync() ?? Task.CompletedTask);
        Reg(ToggleFollow, "Toggle Active Editor Following", "Timeline", _ => { FollowActiveEditor = !FollowActiveEditor; return Task.CompletedTask; });
        Reg(Create, "Create Entry", Category, _ => CreateEntryAsync(), enabled);
        Reg(RestoreViaPicker, "Find Entry to Restore", Category, _ => RestoreViaPickerAsync());
        Reg(DeleteAll, "Delete All", Category, _ => DeleteAllAsync());
        Reg(DeleteAllOfFile, "Delete All Entries of the File", Category, inv => DeleteAllOfFileAsync(UriOf(inv) ?? Live?.Resource));
        Reg(CompareWithFile, "Compare with File", Category, inv => WithItem(inv, CompareWithFileAsync));
        Reg(CompareWithPrevious, "Compare with Previous", Category, inv => WithItem(inv, CompareWithPreviousAsync));
        Reg(Open, "Show Contents", Category, inv => WithItem(inv, ShowContentsAsync));
        Reg(Restore, "Restore Contents", Category, inv => WithEntry(inv, e => RestoreAsync(e)));
        Reg(Rename, "Rename", Category, inv => WithEntry(inv, RenameAsync));
        Reg(Delete, "Delete", Category, inv => WithEntry(inv, DeleteAsync));
    }

    /// <summary>The command's item: its argument (a timeline item or a local history entry), else the Timeline's selected row.</summary>
    private TimelineItem? ItemOf(CommandInvocation inv) => inv.Args.Count > 0 ? inv.Args[0] switch
    {
        TimelineItem item => item,
        LocalHistoryEntry entry => LocalHistoryTimelineProvider.ToItem(entry),
        _ => null,
    } : Live?.Selected?.Item;

    /// <summary>A file argument: a URI or a URI string (keybindings and macros pass strings), else null.</summary>
    private static Uri? UriOf(CommandInvocation inv) => inv.Args.Count > 0 ? inv.Args[0] switch
    {
        Uri uri => uri,
        string s when Uri.TryCreate(s, UriKind.Absolute, out var parsed) => parsed,
        System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } j when Uri.TryCreate(j.GetString(), UriKind.Absolute, out var parsed) => parsed,
        _ => null,
    } : null;

    private Task WithItem(CommandInvocation inv, Func<TimelineItem, Task> run) =>
        ItemOf(inv) is { } item ? run(item) : Status("Select an entry in the Timeline first");

    private Task WithEntry(CommandInvocation inv, Func<LocalHistoryEntry, Task> run) =>
        ItemOf(inv)?.Data is LocalHistoryEntry entry ? run(entry) : Status("Select a local history entry in the Timeline first");

    internal async Task ShowViewAsync()
    {
        if (_sp.GetService<IViewActivator>() is { } views) await views.ShowViewAsync(ViewId).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------------------------------------ comparing

    private ITimelineProvider? ProviderOf(TimelineItem item) => Providers.FirstOrDefault(p => p.Id == item.ProviderId);

    /// <summary>The item ↔ the file as it is now (the open editor's text, else the disk), as VS Code's Compare with File.</summary>
    public async Task CompareWithFileAsync(TimelineItem item)
    {
        if (Diff is null || ProviderOf(item) is not { } provider) return;
        if (await provider.GetContentAsync(item).ConfigureAwait(true) is not { } content) { await Status("That version cannot be shown").ConfigureAwait(true); return; }
        var name = LocalHistoryTimelineProvider.FileName(item.Resource);
        await Diff.OpenDiffAsync(new DiffEditorRequest(Side(content), new DiffSide(name, Uri: item.Resource), $"{content.Label} ↔ {name}", name)).ConfigureAwait(true);
    }

    /// <summary>The version before the item ↔ the item (a commit's change; an entry against the entry before it). With nothing before it, the item alone.</summary>
    public async Task CompareWithPreviousAsync(TimelineItem item)
    {
        if (Diff is null || ProviderOf(item) is not { } provider) return;
        if (await provider.GetContentAsync(item).ConfigureAwait(true) is not { } content) { await Status("That version cannot be shown").ConfigureAwait(true); return; }
        var name = LocalHistoryTimelineProvider.FileName(item.Resource);
        var previous = await provider.GetPreviousContentAsync(item).ConfigureAwait(true);
        var request = previous is null
            ? new DiffEditorRequest(new DiffSide(content.Label), Side(content), content.Label, name)
            : new DiffEditorRequest(Side(previous), Side(content), $"{previous.Label} ↔ {content.Label}", name);
        await Diff.OpenDiffAsync(request).ConfigureAwait(true);
    }

    /// <summary>The item's version alone, read-only (VS Code's Show Contents).</summary>
    public async Task ShowContentsAsync(TimelineItem item)
    {
        if (Diff is null || ProviderOf(item) is not { } provider) return;
        if (await provider.GetContentAsync(item).ConfigureAwait(true) is not { } content) { await Status("That version cannot be shown").ConfigureAwait(true); return; }
        await Diff.OpenDiffAsync(new DiffEditorRequest(new DiffSide(content.Label), Side(content), content.Label, LocalHistoryTimelineProvider.FileName(item.Resource))).ConfigureAwait(true);
    }

    private static DiffSide Side(TimelineContent c) => new(c.Label, c.Uri, c.Uri is null ? c.Text ?? "" : null);

    // ------------------------------------------------------------------------------------------------ local history actions

    /// <summary>
    /// Puts an entry's content back, after asking. A file open in an editor gets the content as one edit (Undo takes it back) and is saved; a
    /// file that is not open is written (its content before is kept as a "Before Restore" entry) and opened. Either way a "File Restored" entry
    /// follows. Returns whether it was restored.
    /// </summary>
    public async Task<bool> RestoreAsync(LocalHistoryEntry entry, bool ask = true)
    {
        if (History is null) return false;
        var name = LocalHistoryTimelineProvider.FileName(entry.Resource);
        if (ask && Windows is not null)
        {
            var open = OpenDocument(entry.Resource) is not null;
            var message = $"Do you want to restore the contents of '{name}' to the entry '{entry.SourceLabel}' of {LocalHistoryTimelineProvider.FormatTime(entry.Timestamp)}?"
                          + (open ? " The editor's text is replaced (Undo takes it back) and the file is saved." : " The file is overwritten; its current content is kept in local history.");
            var answer = await Windows.ShowMessageAsync(MessageSeverity.Warning, message, [new MessageAction("Restore"), new MessageAction("Cancel", IsCloseAffordance: true)]).ConfigureAwait(true);
            if (answer != "Restore") return false;
        }
        var bytes = await History.ReadAsync(entry).ConfigureAwait(true);
        if (bytes is null)
        {
            await Message(MessageSeverity.Error, $"The local history entry of '{name}' no longer exists.").ConfigureAwait(true);
            return false;
        }
        try
        {
            if (OpenDocument(entry.Resource) is { IsReadOnly: false } doc)
            {
                var text = Decode(bytes, doc.EncodingName);
                if (!await ReplaceTextAsync(doc, text).ConfigureAwait(true))
                {
                    await Message(MessageSeverity.Error, $"'{name}' changed while it was being restored; nothing was restored.").ConfigureAwait(true);
                    return false;
                }
                History.SetNextSaveSource(entry.Resource, LocalHistorySources.FileRestored);
                await doc.SaveAsync().ConfigureAwait(true);
            }
            else
            {
                if (FileSystem is null) return false;
                await History.AddEntryAsync(entry.Resource, LocalHistorySources.BeforeRestore).ConfigureAwait(true);
                await FileSystem.WriteFileAsync(entry.Resource, bytes, createParents: true).ConfigureAwait(true);
                await History.AddEntryAsync(entry.Resource, LocalHistorySources.FileRestored).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            await Message(MessageSeverity.Error, $"Could not restore '{name}': {ex.Message}").ConfigureAwait(true);
            return false;
        }
        if (Navigation is not null) await Navigation.OpenAsync(entry.Resource).ConfigureAwait(true);
        return true;
    }

    private IEditorDocument? OpenDocument(Uri resource) =>
        Documents?.Get(resource) as IEditorDocument
        ?? Documents?.Documents.OfType<IEditorDocument>().FirstOrDefault(d => d.Uri.IsFile == resource.IsFile && HistoryFormat.Key(d.Uri) == HistoryFormat.Key(resource));

    /// <summary>
    /// Replaces the document's text with <paramref name="text"/> as one edit covering only what differs (the caret and folds outside it stay).
    /// The comparison runs off the UI thread; the edit is applied only if the document did not change meanwhile.
    /// </summary>
    internal static async Task<bool> ReplaceTextAsync(ITextDocument doc, string text)
    {
        var version = doc.Version;
        var current = doc.GetText();
        var change = await Task.Run(() => MinimalChange(current, text)).ConfigureAwait(true);
        if (change is not { } c) return true; // already that text
        if (doc.Version != version) return false;
        var edit = new TextEdit(new TextRange(doc.PositionAt(c.Start), doc.PositionAt(c.End)), c.Insert);
        return doc.ApplyEdits([edit]);
    }

    /// <summary>The one replacement that turns <paramref name="current"/> into <paramref name="target"/>: the span between their common prefix and suffix.</summary>
    internal static (int Start, int End, string Insert)? MinimalChange(string current, string target)
    {
        if (string.Equals(current, target, StringComparison.Ordinal)) return null;
        var max = Math.Min(current.Length, target.Length);
        var prefix = current.AsSpan().CommonPrefixLength(target);
        // never split a CRLF or a surrogate pair
        if (prefix > 0 && (char.IsHighSurrogate(current[prefix - 1]) || current[prefix - 1] == '\r')) prefix--;
        var suffix = 0;
        while (suffix < max - prefix && current[current.Length - 1 - suffix] == target[target.Length - 1 - suffix]) suffix++;
        if (suffix > 0)
        {
            var first = current.Length - suffix;
            if (char.IsLowSurrogate(current[first]) || (current[first] == '\n' && first > prefix && current[first - 1] == '\r')) suffix--;
        }
        return (prefix, current.Length - suffix, target.Substring(prefix, target.Length - suffix - prefix));
    }

    /// <summary>The bytes of an entry as text, in the document's encoding (a BOM decides first).</summary>
    internal static string Decode(byte[] bytes, string? encodingName)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        var encoding = (encodingName ?? "").Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal).ToLowerInvariant() switch
        {
            "utf16le" => Encoding.Unicode,
            "utf16be" => Encoding.BigEndianUnicode,
            "iso88591" or "latin1" => Encoding.Latin1,
            "windows1252" or "cp1252" => Windows1252(),
            _ => Encoding.UTF8,
        };
        return encoding.GetString(bytes);
    }

    private static Encoding Windows1252()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // idempotent
        return Encoding.GetEncoding(1252);
    }

    /// <summary>VS Code's Rename: the entry's label (its source) becomes the name typed.</summary>
    public async Task RenameAsync(LocalHistoryEntry entry)
    {
        if (History is null || Windows is null) return;
        var name = await Windows.ShowInputBoxAsync(new InputBoxOptions("Rename Local History Entry", $"Enter the new name of the local history entry of '{LocalHistoryTimelineProvider.FileName(entry.Resource)}'",
            Value: entry.SourceLabel)).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name) || name == entry.SourceLabel) return;
        await History.RenameEntryAsync(entry, name.Trim()).ConfigureAwait(true);
    }

    public async Task DeleteAsync(LocalHistoryEntry entry)
    {
        if (History is null) return;
        if (Windows is not null)
        {
            var answer = await Windows.ShowMessageAsync(MessageSeverity.Warning,
                $"Do you want to delete the local history entry '{entry.SourceLabel}' of '{LocalHistoryTimelineProvider.FileName(entry.Resource)}' from {LocalHistoryTimelineProvider.FormatTime(entry.Timestamp)}? This cannot be undone.",
                [new MessageAction("Delete"), new MessageAction("Cancel", IsCloseAffordance: true)]).ConfigureAwait(true);
            if (answer != "Delete") return;
        }
        await History.RemoveEntryAsync(entry).ConfigureAwait(true);
    }

    public async Task DeleteAllOfFileAsync(Uri? resource)
    {
        if (History is null) return;
        if (resource is null) { await Status("Open a file in the Timeline first").ConfigureAwait(true); return; }
        if (Windows is not null)
        {
            var answer = await Windows.ShowMessageAsync(MessageSeverity.Warning,
                $"Do you want to delete every local history entry of '{LocalHistoryTimelineProvider.FileName(resource)}'? This cannot be undone.",
                [new MessageAction("Delete All"), new MessageAction("Cancel", IsCloseAffordance: true)]).ConfigureAwait(true);
            if (answer != "Delete All") return;
        }
        await History.RemoveAllAsync(resource).ConfigureAwait(true);
    }

    /// <summary>VS Code's Delete All: the local history of every file.</summary>
    public async Task DeleteAllAsync()
    {
        if (History is null) return;
        if (Windows is not null)
        {
            var answer = await Windows.ShowMessageAsync(MessageSeverity.Warning, "Do you want to delete all entries of all files in local history? This cannot be undone.",
                [new MessageAction("Delete All"), new MessageAction("Cancel", IsCloseAffordance: true)]).ConfigureAwait(true);
            if (answer != "Delete All") return;
        }
        await History.RemoveAllAsync(null).ConfigureAwait(true);
    }

    /// <summary>VS Code's Create Entry: the active editor's file as it is on disk, under a name the user types.</summary>
    public async Task CreateEntryAsync()
    {
        if (History is null) return;
        var resource = Navigation?.ActiveDocument?.Uri;
        if (resource is null || !History.CanTrack(resource)) { await Status("Open a file to create a local history entry of it").ConfigureAwait(true); return; }
        if (!History.IsEnabled) { await Status("Local history is turned off (workbench.localHistory.enabled)").ConfigureAwait(true); return; }
        string? name = null;
        if (Windows is not null)
        {
            name = await Windows.ShowInputBoxAsync(new InputBoxOptions("Create Local History Entry", $"Enter the name of the local history entry of '{LocalHistoryTimelineProvider.FileName(resource)}'")).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(name)) return;
        }
        if (await History.AddEntryAsync(resource, name?.Trim()).ConfigureAwait(true) is null)
            await Status("No entry was created: the file is excluded, too large, missing, or unchanged since its last entry of that name").ConfigureAwait(true);
    }

    /// <summary>VS Code's Find Entry to Restore: pick a file with history, then one of its entries; it is compared with the file, then restored after asking.</summary>
    public async Task<bool> RestoreViaPickerAsync()
    {
        if (History is null || Windows is null) return false;
        var resources = await History.GetResourcesAsync().ConfigureAwait(true);
        if (resources.Count == 0) { await Message(MessageSeverity.Info, "Local history has no entries.").ConfigureAwait(true); return false; }
        var files = resources.Select(r => new QuickPickItem(LocalHistoryTimelineProvider.FileName(r), Describe(r), Tag: r)).ToList();
        var file = await Windows.ShowQuickPickAsync(files, new QuickPickOptions("Find Entry to Restore", "Select the file to show local history entries for")).ConfigureAwait(true);
        if (file?.Tag is not Uri resource) return false;
        var entries = await History.GetEntriesAsync(resource).ConfigureAwait(true);
        if (entries.Count == 0) return false;
        var now = Time.GetUtcNow();
        var picks = entries.Reverse().Select(e => new QuickPickItem(e.SourceLabel, $"{RelativeTime.Format(e.Timestamp, now)} · {LocalHistoryTimelineProvider.FormatTime(e.Timestamp)}", Tag: e)).ToList();
        var pick = await Windows.ShowQuickPickAsync(picks, new QuickPickOptions("Find Entry to Restore", $"Select the local history entry of '{file.Label}' to open")).ConfigureAwait(true);
        if (pick?.Tag is not LocalHistoryEntry entry) return false;
        await CompareWithFileAsync(LocalHistoryTimelineProvider.ToItem(entry)).ConfigureAwait(true);
        return await RestoreAsync(entry).ConfigureAwait(true);
    }

    private string Describe(Uri resource)
    {
        var full = HistoryFormat.Describe(resource);
        if (_sp.GetService<IWorkspaceService>()?.Folders is { } folders)
            foreach (var f in folders)
            {
                var root = HistoryFormat.Describe(f).TrimEnd('/', '\\');
                if (full.StartsWith(root + "/", StringComparison.Ordinal) || full.StartsWith(root + "\\", StringComparison.Ordinal)) return full[(root.Length + 1)..];
            }
        return full;
    }

    // ------------------------------------------------------------------------------------------------ messages

    private Task Status(string text)
    {
        Windows?.SetStatusBarMessage(text, TimeSpan.FromSeconds(4));
        return Task.CompletedTask;
    }

    private async Task Message(MessageSeverity severity, string text)
    {
        if (Windows is not null) await Windows.ShowMessageAsync(severity, text).ConfigureAwait(true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _store.Dispose();
        if (Live is { } live) ReleaseViewModel(live);
    }
}
