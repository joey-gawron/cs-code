using System.Text;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCode.Macros;

/// <summary>
/// <c>{UserDataDir}/macros.json</c>: the saved macros. Reading, parsing, formatting and writing all happen off the UI thread (file I/O through
/// <see cref="IFileSystem"/>, which runs it on the IO lane; JSON on the thread pool). The file is watched: an edit saved by the user (or another
/// CsCode window) is reloaded and published through <see cref="Changed"/>. Saving a macro rewrites only that macro's object in the file, so the
/// user's comments and layout stay. A file with errors is never overwritten: its valid macros still load and the errors are reported.
/// </summary>
public sealed class MacroStore : IDisposable
{
    public static readonly TimeSpan ReloadDelay = TimeSpan.FromMilliseconds(150);

    private readonly IFileSystem _fs;
    private readonly IFileWatcher? _watcher;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly Emitter<MacroFile> _changed = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DisposableStore _disposables = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _timerGate = new();
    private ITimer? _reloadTimer;
    private MacroFile _current = MacroFile.Empty;
    private string? _lastText;
    private bool _disposed;

    public MacroStore(IAppPaths paths, IFileSystem fs, IFileWatcher? watcher = null, ILogger? log = null, TimeProvider? time = null)
    {
        _fs = fs;
        _watcher = watcher;
        _log = log ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        Directory = paths.UserDataDir;
        FilePath = Path.Combine(paths.UserDataDir, MacroCommands.FileName);
        FileUri = new Uri(FilePath);
    }

    public string Directory { get; }
    public string FilePath { get; }
    public Uri FileUri { get; }

    /// <summary>The macros of the last successful read (empty before the first).</summary>
    public MacroFile Current => Volatile.Read(ref _current);

    /// <summary>Fires (on a pool thread) after a read changed <see cref="Current"/>.</summary>
    public IEventSource<MacroFile> Changed => _changed;

    /// <summary>Most steps a macro of the file may have (the recording limit, read when the file is parsed).</summary>
    public Func<int> MaxStepsPerMacro { get; set; } = () => MacroSettings.DefaultMaxRecordedSteps;

    /// <summary>Reads the file now (missing = no macros). Never throws for I/O or JSON problems: they are reported in <see cref="MacroFile.Errors"/>.</summary>
    public async Task<MacroFile> ReloadAsync(CancellationToken ct = default)
    {
        string? text;
        try { text = await ReadAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not read {Path}", FilePath);
            var failed = new MacroFile(Current.Macros, [$"Could not read {FilePath}: {ex.Message}"]);
            Publish(failed, null);
            return failed;
        }
        if (text is not null && text == Volatile.Read(ref _lastText)) return Current; // our own write coming back through the watcher
        var max = MaxStepsPerMacro();
        var file = await Task.Run(() => MacroJson.ParseFile(text ?? "", max), ct).ConfigureAwait(false);
        Publish(file, text);
        return file;
    }

    private void Publish(MacroFile file, string? text)
    {
        if (_disposed) return;
        Volatile.Write(ref _lastText, text);
        Volatile.Write(ref _current, file);
        _changed.Fire(file);
    }

    /// <summary>Starts the live reload (idempotent). The directory may not exist yet: the watch waits for it.</summary>
    public void StartWatching()
    {
        if (_watcher is null || _disposed || _watching) return;
        _watching = true;
        try
        {
            _disposables.Add(_watcher.Watch(new Uri(Path.TrimEndingDirectorySeparator(Directory) + "/"), recursive: false, excludeGlobs: null, OnChanges));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _log.LogWarning(ex, "Could not watch {Directory}; macros.json is read at startup and after saves only", Directory);
        }
    }

    private bool _watching;

    private void OnChanges(IReadOnlyList<FileChange> changes)
    {
        foreach (var c in changes)
        {
            if (!c.Uri.IsFile || !string.Equals(Path.GetFileName(c.Uri.LocalPath), MacroCommands.FileName, StringComparison.Ordinal)) continue;
            ScheduleReload();
            return;
        }
    }

    /// <summary>Coalesces bursts (an editor's save is several events) into one read.</summary>
    private void ScheduleReload()
    {
        lock (_timerGate)
        {
            if (_disposed) return;
            _reloadTimer?.Dispose();
            _reloadTimer = _time.CreateTimer(_ => _ = ReloadQuietlyAsync(), null, ReloadDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task ReloadQuietlyAsync()
    {
        try { await ReloadAsync(_cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogWarning(ex, "Reloading {Path} failed", FilePath); }
    }

    /// <summary>Adds <paramref name="macro"/> to the file, or replaces the macro of the same name (comments and layout kept).</summary>
    /// <exception cref="MacroFileException">The file has errors (it is not touched).</exception>
    public async Task SaveAsync(Macro macro, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var text = await ReadAsync(ct).ConfigureAwait(false);
            var updated = await Task.Run(() => MacroFileEditor.Upsert(text, macro), ct).ConfigureAwait(false);
            var max = MaxStepsPerMacro();
            var parsed = await Task.Run(() => MacroJson.ParseFile(updated, max), ct).ConfigureAwait(false);
            // checked before writing: a file that would not load the saved macro (an edit that broke the JSON) is never written over the user's
            if (!parsed.Macros.Any(m => m.Name == macro.Name))
                throw new MacroFileException($"the changed {MacroCommands.FileName} would not load it ({parsed.Errors.FirstOrDefault() ?? "no reason given"}); the file was not changed.");
            await _fs.WriteFileAsync(FileUri, Encoding.UTF8.GetBytes(updated), createParents: true, ct).ConfigureAwait(false);
            Publish(parsed, updated);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Removes the macro <paramref name="name"/> from the file (nothing when it is not there).</summary>
    public async Task RemoveAsync(string name, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var text = await ReadAsync(ct).ConfigureAwait(false);
            if (text is null) return;
            var updated = await Task.Run(() => MacroFileEditor.Remove(text, name), ct).ConfigureAwait(false);
            if (updated == text) return;
            await _fs.WriteFileAsync(FileUri, Encoding.UTF8.GetBytes(updated), createParents: true, ct).ConfigureAwait(false);
            var max = MaxStepsPerMacro();
            Publish(await Task.Run(() => MacroJson.ParseFile(updated, max), ct).ConfigureAwait(false), updated);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Creates the file with its guide when it does not exist (Tools > Macros > Edit). Returns its URI.</summary>
    public async Task<Uri> EnsureExistsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await _fs.StatAsync(FileUri, ct).ConfigureAwait(false) is null)
                await _fs.WriteFileAsync(FileUri, Encoding.UTF8.GetBytes(MacroJson.FormatFile([])), createParents: true, ct).ConfigureAwait(false);
            return FileUri;
        }
        finally { _gate.Release(); }
    }

    private async Task<string?> ReadAsync(CancellationToken ct)
    {
        if (await _fs.StatAsync(FileUri, ct).ConfigureAwait(false) is not { Type: FileType.File } stat) return null;
        if (stat.Size > MacroJson.MaxFileBytes) throw new IOException($"{MacroCommands.FileName} is larger than {MacroJson.MaxFileBytes / (1024 * 1024)} MB");
        var bytes = await _fs.ReadFileAsync(FileUri, ct).ConfigureAwait(false);
        var text = Encoding.UTF8.GetString(bytes);
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }

    public void Dispose()
    {
        lock (_timerGate)
        {
            if (_disposed) return;
            _disposed = true;
            _reloadTimer?.Dispose();
            _reloadTimer = null;
        }
        _cts.Cancel();
        _disposables.Dispose();
        _changed.Clear();
        _cts.Dispose();
    }
}
