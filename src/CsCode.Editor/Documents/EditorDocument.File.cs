using System.Security.Cryptography;
using AvaloniaEdit.Document;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.Threading;
using Microsoft.Extensions.Logging;

namespace CsCode.Editor.Documents;

// File half of EditorDocument: load, atomic save, reload, external-change handling, encoding/EOL state.
public sealed partial class EditorDocument
{
    private const int MaxSaveParticipantRetries = 3;

    private EditorServices? _svc;
    private FileEncoding _encoding = FileEncoding.Utf8;
    private string _eol = "\n";
    private EditorConfigProperties _editorConfig = EditorConfigProperties.None;
    private IDisposable? _registration;
    private IDisposable? _watch;
    private byte[]? _diskHash;
    private FileStat? _lastKnownStat;
    private readonly SemaphoreSlim _ioGate = new(1, 1); // serialises save / reload / external-change handling
    private readonly object _stateGate = new();
    private bool _recheckExternal;
    private bool _externalRunning;
    private volatile bool _externalChangePending;
    private Func<Uri, string?, string>? _detectLanguage;
    private readonly Emitter<(Uri Old, Uri New)> _uriChanged = new();

    /// <summary>The .editorconfig facts resolved for this file when it was opened/saved (indent style/size etc.).</summary>
    internal EditorConfigProperties EditorConfigProperties => _editorConfig;

    public string EncodingName => _encoding.Name;
    public string EndOfLine => _eol;

    /// <summary>True when the file exceeded the large-file limits: language is plaintext, no tokenisation/folding/squiggles/bracket matching.</summary>
    public bool IsLargeFile { get; private set; }

    /// <summary>User-facing reason for large-file mode (for the status bar / info banner), null otherwise.</summary>
    public string? LargeFileReason { get; private set; }

    /// <summary>
    /// True while the "changed on disk and you have unsaved changes" question waits for an answer. A save meanwhile would wait for the answer
    /// (the check holds the file's lock) and then overwrite what the user may be about to reload, so auto save leaves the document alone.
    /// </summary>
    public bool IsExternalChangePending => _externalChangePending;

    /// <summary>
    /// The offsets of the carets in the views of this document (UI thread; set by the view factory): an auto save after a delay
    /// (<see cref="DocumentSaveReason.AfterDelay"/>) leaves their lines alone, as VS Code does, so the space just typed is not trimmed away.
    /// </summary>
    internal Func<IReadOnlyList<int>>? CaretOffsets { get; set; }

    /// <summary>Fires after Save As changed <see cref="Uri"/> (the factory re-keys its table).</summary>
    internal IEventSource<(Uri Old, Uri New)> UriChanged => _uriChanged;

    // ---- construction from disk / as untitled ----

    private sealed record Prepared(TextDocument Buffer, FileEncoding Encoding, string Eol, bool Large, string? LargeReason, byte[] Hash, string FirstLine);

    /// <summary>Opens <paramref name="uri"/>: stat, size guard, read, decode (off the UI thread), build the rope, register with the document manager.</summary>
    internal static async Task<EditorDocument> OpenAsync(EditorServices svc, Uri uri, Func<Uri, string?, string> detectLanguage, CancellationToken ct)
    {
        var settings = FileSettings.Read(svc.Config);
        var stat = await svc.FileSystem.StatAsync(uri, ct).ConfigureAwait(false)
                   ?? throw new FileNotFoundException($"File not found: {uri.LocalPath}", uri.LocalPath);
        if (stat.Type == FileType.Directory) throw new IOException($"'{uri.LocalPath}' is a directory.");
        if (stat.Size > settings.MaxFileSizeBytes) throw new FileTooLargeException(uri, stat.Size, settings.MaxFileSizeBytes);

        var bytes = await svc.FileSystem.ReadFileAsync(uri, ct).ConfigureAwait(false);
        var ec = await svc.EditorConfig.ResolveAsync(uri, ct).ConfigureAwait(false);
        var owner = await OwnerThreadAsync(svc.Ui).ConfigureAwait(false);
        var prepared = await svc.Io.Run(() => Prepare(bytes, settings, ec, owner, null), ct).ConfigureAwait(false);

        var language = prepared.Large ? "plaintext" : detectLanguage(uri, prepared.FirstLine);
        var doc = await OnUi(svc.Ui, () => new EditorDocument(uri, language, prepared.Buffer, svc.Ui, settings.UndoLimit)).ConfigureAwait(false);
        doc.AttachFile(svc, detectLanguage, prepared, ec, stat);
        await OnUi(svc.Ui, () =>
        {
            doc._registration = svc.Documents.Register(doc);
            return 0;
        }).ConfigureAwait(false);
        doc.StartWatching();
        return doc;
    }

    /// <summary>A new empty "Untitled-N" buffer; registered with the document manager. Must be called on the UI thread.</summary>
    internal static EditorDocument CreateUntitled(EditorServices svc, int number, string languageId, Func<Uri, string?, string> detectLanguage)
    {
        var settings = FileSettings.Read(svc.Config, languageId);
        var doc = new EditorDocument(new Uri($"untitled:Untitled-{number}"), languageId, CreateBuffer(string.Empty, svc.Ui), svc.Ui, settings.UndoLimit)
        {
            _svc = svc,
            _detectLanguage = detectLanguage,
            _eol = settings.DefaultEol,
            _languageExplicit = languageId != "plaintext",
        };
        doc._registration = svc.Documents.Register(doc);
        return doc;
    }

    private static Task<Thread> OwnerThreadAsync(IUiThread ui)
    {
        if (ui.CheckAccess()) return Task.FromResult(ui.OwnerThread);
        var tcs = new TaskCompletionSource<Thread>(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Post(() => tcs.SetResult(ui.OwnerThread));
        return tcs.Task;
    }

    private static Task<T> OnUi<T>(IUiThread ui, Func<T> work)
    {
        if (ui.CheckAccess()) return Task.FromResult(work());
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Post(() =>
        {
            try
            {
                tcs.SetResult(work());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    private Task<T> OnUi<T>(Func<T> work) => OnUi(_ui, work);

    /// <summary>Decode + EOL/large-file analysis + rope construction: pure CPU/alloc work, runs on the IO lane.</summary>
    private static Prepared Prepare(byte[] bytes, FileSettings s, EditorConfigProperties ec, Thread? owner, FileEncoding? forced)
    {
        var configured = forced ?? FileEncoding.FromSettingName(ec.Charset) ?? s.Encoding;
        var encoding = forced ?? TextFileCodec.Detect(bytes, configured);
        var text = TextFileCodec.Decode(bytes, encoding);
        var eol = TextFileCodec.DetectEol(text, ec.EolString ?? s.DefaultEol).Eol;

        string? reason = null;
        if (bytes.Length > s.LargeFileThresholdBytes)
            reason = $"Large file ({bytes.Length / (1024 * 1024)} MB): syntax highlighting, folding, bracket matching and language features are turned off.";
        else if (MaxLineLength(text) > s.LongLineChars)
            reason = $"This file has a line longer than {s.LongLineChars:N0} characters: syntax highlighting, folding, bracket matching and language features are turned off.";

        var buffer = new TextDocument(text);
        if (owner is not null) buffer.SetOwnerThread(owner);
        var nl = text.AsSpan().IndexOfAny('\r', '\n');
        var first = nl < 0 ? text : text[..nl];
        if (first.Length > 300) first = first[..300];
        return new Prepared(buffer, encoding, eol, reason is not null, reason, SHA256.HashData(bytes), first);
    }

    private static int MaxLineLength(string text)
    {
        var max = 0;
        var span = text.AsSpan();
        while (span.Length > 0)
        {
            var i = span.IndexOfAny('\r', '\n');
            var len = i < 0 ? span.Length : i;
            if (len > max) max = len;
            if (i < 0) break;
            span = span[(i + 1)..];
        }
        return max;
    }

    private void AttachFile(EditorServices svc, Func<Uri, string?, string> detect, Prepared p, EditorConfigProperties ec, FileStat stat)
    {
        _svc = svc;
        _detectLanguage = detect;
        _encoding = p.Encoding;
        _eol = p.Eol;
        _editorConfig = ec;
        _diskHash = p.Hash;
        _lastKnownStat = stat;
        IsLargeFile = p.Large;
        LargeFileReason = p.LargeReason;
        _isReadOnly = stat.IsReadOnly;
    }

    // ---- save ----

    public Task SaveAsync(CancellationToken ct = default) => SaveAsync(DocumentSaveReason.Manual, ct);

    /// <summary>
    /// Saves for <paramref name="reason"/>. An auto save after a delay skips format on save and leaves the lines that hold a caret alone (trailing
    /// whitespace, final newlines), as VS Code's save participants do; an untitled document is saved (Save As) only when the user asked.
    /// </summary>
    public async Task SaveAsync(DocumentSaveReason reason, CancellationToken ct)
    {
        if (_svc is null) throw new InvalidOperationException("Document is not attached to a file system.");
        if (IsUntitled)
        {
            if (reason != DocumentSaveReason.Manual) return; // an auto save never asks for a name
            // Untitled: ask for a name; a cancelled dialog leaves the document dirty and untouched.
            var target = _svc.Window is null
                ? null
                : await _svc.Window.ShowSaveDialogAsync(new SaveDialogOptions(Title: "Save As", DefaultUri: null), ct).ConfigureAwait(false);
            if (target is null) return;
            await SaveAsAsync(target, ct).ConfigureAwait(false);
            return;
        }
        await SaveToAsync(Uri, reason, ct).ConfigureAwait(false);
    }

    /// <summary>Writes to <paramref name="target"/> and makes it the document's identity (re-registers with the document manager).</summary>
    public async Task SaveAsAsync(Uri target, CancellationToken ct = default)
    {
        if (_svc is null) throw new InvalidOperationException("Document is not attached to a file system.");
        await SaveToAsync(target, DocumentSaveReason.Manual, ct).ConfigureAwait(false);
        if (target == Uri) return;
        await OnUi(() =>
        {
            var old = Uri;
            _registration?.Dispose(); // fires Closed for the old identity (LSP didClose)
            Uri = target;
            _isReadOnly = false;
            if (!_languageExplicit && _detectLanguage is { } detect && !IsLargeFile) ApplyLanguageId(detect(target, GetLineText(0)));
            _registration = _svc.Documents.Register(this); // Opened for the new one
            _uriChanged.Fire((old, target));
            return 0;
        }).ConfigureAwait(false);
        StartWatching();
    }

    private async Task SaveToAsync(Uri target, DocumentSaveReason reason, CancellationToken ct)
    {
        var svc = _svc!;
        await _ioGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var settings = FileSettings.Read(svc.Config, LanguageId);
            var ec = target.IsFile ? await svc.EditorConfig.ResolveAsync(target, ct).ConfigureAwait(false) : EditorConfigProperties.None;
            // VS Code: no format on save for an auto save after a delay (it would reformat the code under the user's hands while they type)
            if (reason != DocumentSaveReason.AfterDelay) await FormatOnSaveAsync(settings, ec, ct).ConfigureAwait(false);
            DocumentSnapshot snap = await ApplySaveParticipantsAsync(settings, ec, reason == DocumentSaveReason.AfterDelay, ct).ConfigureAwait(false);

            var eol = ec.EolString ?? _eol;
            var encoding = FileEncoding.FromSettingName(ec.Charset) is { } charset && target != Uri ? charset : _encoding;
            var bytes = await svc.Io.Run(() => TextFileCodec.Encode(TextFileCodec.NormalizeEol(snap.GetText(), eol), encoding), ct).ConfigureAwait(false);
            var hash = await svc.Io.Run(() => SHA256.HashData(bytes), ct).ConfigureAwait(false);

            lock (_stateGate) _diskHash = hash; // set BEFORE writing: the watcher event for our own write must be recognised as ours
            await WriteAtomicAsync(svc.FileSystem, target, bytes, ct).ConfigureAwait(false);
            var stat = await svc.FileSystem.StatAsync(target, ct).ConfigureAwait(false);

            await OnUi(() =>
            {
                lock (_stateGate) _lastKnownStat = stat;
                var changed = !ReferenceEquals(_encoding, encoding) || _eol != eol;
                _encoding = encoding;
                _eol = eol;
                _editorConfig = ec;
                // Edits typed while the write was in flight stay dirty: only mark the file original if nothing changed since the snapshot.
                if (Version == snap.Version) MarkSaved();
                if (changed) FireOptionsChanged();
                return 0;
            }).ConfigureAwait(false);
            _saved.Fire(this);
            svc.Documents.NotifySaved(this);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <summary>Temp file next to the target, then rename over it: a crash mid-write never leaves a truncated file. Symlinks are written through.</summary>
    internal static async Task WriteAtomicAsync(IFileSystem fs, Uri target, byte[] bytes, CancellationToken ct)
    {
        // lsp-core-8: the app's file systems replace a file safely themselves AND keep its hard links, owner, group, mode and xattrs; a temp file
        // renamed over the target would break hard links and reset the owner (and Platform's rename does not copy the mode: every save dropped +x).
        if (fs.PreservesFileOnWrite(target))
        {
            await fs.WriteFileAsync(target, bytes, createParents: true, ct).ConfigureAwait(false);
            return;
        }
        var existing = await fs.StatAsync(target, ct).ConfigureAwait(false);
        if (existing is not null && (existing.Type & FileType.SymbolicLink) != 0)
        {
            await fs.WriteFileAsync(target, bytes, createParents: true, ct).ConfigureAwait(false);
            return;
        }
        var temp = new Uri(target.AbsoluteUri + $".cscode-{Guid.NewGuid():N}.tmp");
        try
        {
            await fs.WriteFileAsync(temp, bytes, createParents: true, ct).ConfigureAwait(false);
            await fs.RenameAsync(temp, target, overwrite: true, ct).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await fs.DeleteAsync(temp, false, false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // the temp file may not exist; the original exception is the one that matters
            }
            throw;
        }
    }

    // ---- save participants: trim whitespace / final newline (from .editorconfig, else files.*), format on save ----

    private async Task FormatOnSaveAsync(FileSettings settings, EditorConfigProperties ec, CancellationToken ct)
    {
        var svc = _svc!;
        if (!settings.FormatOnSave || IsLargeFile) return;
        var (providers, missing) = CsCode.Editor.Features.FormatterChoice.Select(svc.Features.GetProviders<IDocumentFormattingProvider>(this), CsCode.Editor.Features.FormatterChoice.Configured(svc.Config, LanguageId));
        if (missing is not null)
        {
            svc.LoggerFactory.CreateLogger<EditorDocument>().LogInformation("Format on save skipped for {Uri}: {Reason}", Uri, CsCode.Editor.Features.FormatterChoice.MissingMessage(missing, LanguageId));
            return;
        }
        if (providers.Count == 0) return;
        // the indentation chosen for this document (status bar) first, as the views indent
        var tabSize = _indentation?.TabSize ?? ec.EffectiveIndentSize ?? svc.Config.GetOr("editor.tabSize", LanguageId, 4);
        var insertSpaces = _indentation?.InsertSpaces ?? (ec.IndentStyle is { } style ? style == "space" : svc.Config.GetOr("editor.insertSpaces", LanguageId, true));
        var options = new FormattingOptions(tabSize, insertSpaces);
        var versionBefore = Version;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3)); // a hung formatter must not block saving forever
        try
        {
            // The first formatter that answers with edits, as Format Document does: a server can register formatting and answer nothing
            // (basedpyright does), and without editor.defaultFormatter it may come first.
            IReadOnlyList<TextEdit>? edits = null;
            foreach (var provider in providers)
                if ((edits = await provider.FormatDocumentAsync(this, options, timeout.Token).ConfigureAwait(false)) is { Count: > 0 }) break;
            // Stale result (the user kept typing): VS Code drops it as well; the save still goes ahead.
            if (edits is { Count: > 0 } && Version == versionBefore) ApplyEdits(edits);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            svc.LoggerFactory.CreateLogger<EditorDocument>().LogWarning("Format on save timed out for {Uri}", Uri);
        }
        catch (Exception ex)
        {
            svc.LoggerFactory.CreateLogger<EditorDocument>().LogWarning(ex, "Format on save failed for {Uri}", Uri);
        }
    }

    private async Task<DocumentSnapshot> ApplySaveParticipantsAsync(FileSettings settings, EditorConfigProperties ec, bool guardCarets, CancellationToken ct)
    {
        var trim = ec.TrimTrailingWhitespace ?? settings.TrimTrailingWhitespace;
        var finalNewline = ec.InsertFinalNewline ?? settings.InsertFinalNewline;
        var trimFinal = settings.TrimFinalNewlines;
        var eol = ec.EolString ?? _eol;
        if (!trim && !finalNewline && !trimFinal) return Snapshot();
        for (var attempt = 0; attempt < MaxSaveParticipantRetries; attempt++)
        {
            var snap = Snapshot();
            var guard = guardCarets ? await OnUi(CaretGuard).ConfigureAwait(false) : null;
            var edits = await _svc!.Io.Run(() => SaveEdits.Compute(snap, trim, finalNewline, trimFinal, eol, guard), ct).ConfigureAwait(false);
            if (edits.Count == 0) return snap;
            var applied = await OnUi(() => Version == snap.Version && ApplyEdits(edits)).ConfigureAwait(false);
            if (applied) return Snapshot();
        }
        return Snapshot(); // the user is typing fast: save what is there without participants
    }

    /// <summary>The lines that hold a caret and whether one is at the very end (UI thread): what an auto save after a delay must not edit.</summary>
    private SaveEdits.CaretGuard CaretGuard()
    {
        var lines = new HashSet<int>();
        var atEnd = false;
        foreach (var offset in CaretOffsets?.Invoke() ?? [])
        {
            var clamped = Math.Clamp(offset, 0, _text.TextLength);
            lines.Add(_text.GetLineByOffset(clamped).LineNumber - 1);
            atEnd |= clamped == _text.TextLength;
        }
        return new SaveEdits.CaretGuard(lines, atEnd);
    }

    // ---- reload / external change ----

    /// <summary>Discards edits and re-reads the file (re-detecting encoding/EOL; undo history is cleared).</summary>
    public Task ReloadAsync(CancellationToken ct = default) => ReloadAsync(null, ct);

    /// <summary>"Reopen with Encoding": like <see cref="ReloadAsync(CancellationToken)"/> but decoding with <paramref name="encoding"/>.</summary>
    internal async Task ReloadAsync(FileEncoding? encoding, CancellationToken ct)
    {
        var svc = _svc ?? throw new InvalidOperationException("Document is not attached to a file system.");
        if (IsUntitled) return;
        await _ioGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stat = await svc.FileSystem.StatAsync(Uri, ct).ConfigureAwait(false)
                       ?? throw new FileNotFoundException($"File not found: {Uri.LocalPath}", Uri.LocalPath);
            var bytes = await svc.FileSystem.ReadFileAsync(Uri, ct).ConfigureAwait(false);
            await ApplyDiskContentAsync(bytes, stat, encoding, ct).ConfigureAwait(false);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <summary>Replaces the buffer with the decoded disk content as ONE minimal edit (common prefix/suffix kept so caret, folds and scroll survive).</summary>
    private async Task ApplyDiskContentAsync(byte[] bytes, FileStat stat, FileEncoding? forced, CancellationToken ct)
    {
        var svc = _svc!;
        var settings = FileSettings.Read(svc.Config, LanguageId);
        var ec = await svc.EditorConfig.ResolveAsync(Uri, ct).ConfigureAwait(false);
        for (var attempt = 0; attempt < MaxSaveParticipantRetries; attempt++)
        {
            var snap = Snapshot();
            var plan = await svc.Io.Run(() =>
            {
                var configured = forced ?? FileEncoding.FromSettingName(ec.Charset) ?? settings.Encoding;
                var encoding = forced ?? TextFileCodec.Detect(bytes, configured);
                var text = TextFileCodec.Decode(bytes, encoding);
                var eol = TextFileCodec.DetectEol(text, ec.EolString ?? settings.DefaultEol).Eol;
                var old = snap.GetText();
                var prefix = CommonPrefix(old, text);
                var suffix = CommonSuffix(old, text, prefix);
                return (Encoding: encoding, Eol: eol, Start: prefix, Length: old.Length - prefix - suffix, Middle: text.Substring(prefix, text.Length - prefix - suffix), Hash: SHA256.HashData(bytes));
            }, ct).ConfigureAwait(false);

            var done = await OnUi(() =>
            {
                if (Version != snap.Version) return false; // edited meanwhile: recompute against the new text
                if (plan.Length > 0 || plan.Middle.Length > 0)
                    RunAtomic(() => _text.Replace(plan.Start, plan.Length, plan.Middle));
                ClearUndoHistory(); // a held undo group (macro playback) stays open, with the new text as its start
                _text.UndoStack.MarkAsOriginalFile();
                UpdateDirty(false);
                lock (_stateGate)
                {
                    _diskHash = plan.Hash;
                    _lastKnownStat = stat;
                }
                var changed = !ReferenceEquals(_encoding, plan.Encoding) || _eol != plan.Eol;
                _encoding = plan.Encoding;
                _eol = plan.Eol;
                _editorConfig = ec;
                _isReadOnly = stat.IsReadOnly;
                if (changed) FireOptionsChanged(); // Reopen with Encoding, or the file changed its line endings outside
                return true;
            }).ConfigureAwait(false);
            if (done) return;
        }
    }

    private static int CommonPrefix(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        var i = a.AsSpan(0, n).CommonPrefixLength(b.AsSpan(0, n));
        // never split a surrogate pair or a CR/LF pair
        if (i > 0 && i < n && (char.IsHighSurrogate(a[i - 1]) || a[i - 1] == '\r')) i--;
        return i;
    }

    private static int CommonSuffix(string a, string b, int prefix)
    {
        var max = Math.Min(a.Length, b.Length) - prefix;
        var i = 0;
        while (i < max && a[a.Length - 1 - i] == b[b.Length - 1 - i]) i++;
        if (i > 0 && i < max && (char.IsLowSurrogate(a[a.Length - i]) || a[a.Length - i] == '\n')) i--;
        return i;
    }

    private void StartWatching()
    {
        var svc = _svc;
        if (svc is null || IsUntitled) return;
        if (!Uri.IsFile)
        {
            StartWatchingProvider(svc);
            return;
        }
        _watch?.Dispose();
        var dir = Path.GetDirectoryName(Uri.LocalPath);
        if (string.IsNullOrEmpty(dir)) return;
        var local = Uri.LocalPath;
        _watch = svc.FileWatcher.Watch(new Uri(dir.TrimEnd('/') + "/"), false, null, changes =>
        {
            foreach (var c in changes)
                if (c.Uri.IsFile && string.Equals(c.Uri.LocalPath, local, StringComparison.Ordinal))
                {
                    _ = HandleExternalChangeAsync();
                    return;
                }
        });
    }

    /// <summary>
    /// A document of another file system (a remote <c>ssh:</c> file): watched through the routing watcher like a local one (its provider reports the
    /// file's own URI, built with the same escaping the document was opened with). A scheme without a watcher provider gets a no-op watch.
    /// </summary>
    private void StartWatchingProvider(EditorServices svc)
    {
        _watch?.Dispose();
        var text = Uri.AbsoluteUri;
        var authority = text.IndexOf("://", StringComparison.Ordinal);
        var slash = text.LastIndexOf('/');
        // Only hierarchical URIs with an authority (ssh://host/dir/file): a virtual document (debug:, untitled:) has no directory to watch.
        if (authority < 0 || slash <= authority + 2 || !Uri.TryCreate(text[..(slash + 1)], UriKind.Absolute, out var directory)) return;
        var self = Uri;
        _watch = svc.FileWatcher.Watch(directory, false, null, changes =>
        {
            foreach (var c in changes)
                if (c.Uri == self)
                {
                    _ = HandleExternalChangeAsync();
                    return;
                }
        });
    }

    private async Task HandleExternalChangeAsync()
    {
        var svc = _svc;
        if (svc is null || _disposed) return;
        lock (_stateGate)
        {
            if (_externalRunning)
            {
                _recheckExternal = true; // coalesce: the running pass will look again
                return;
            }
            _externalRunning = true;
        }
        try
        {
            do
            {
                lock (_stateGate) _recheckExternal = false;
                await _ioGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_disposed) return;
                    await CheckDiskAsync(svc).ConfigureAwait(false);
                }
                finally
                {
                    _ioGate.Release();
                }
            }
            while (ConsumeRecheck());
        }
        catch (Exception ex)
        {
            // Runs fire-and-forget from the watcher callback: it must never surface as an unobserved fault.
            svc.LoggerFactory.CreateLogger<EditorDocument>().LogDebug(ex, "External-change check failed for {Uri}", Uri);
        }
        finally
        {
            lock (_stateGate) _externalRunning = false;
        }
    }

    private bool ConsumeRecheck()
    {
        lock (_stateGate)
        {
            var r = _recheckExternal;
            _recheckExternal = false;
            return r;
        }
    }

    private async Task CheckDiskAsync(EditorServices svc)
    {
        var stat = await svc.FileSystem.StatAsync(Uri).ConfigureAwait(false);
        if (stat is null) return; // deleted: the buffer stays as the user's copy
        FileStat? known;
        lock (_stateGate) known = _lastKnownStat;
        if (known is not null && known.Size == stat.Size && known.ModifiedUtc == stat.ModifiedUtc) return;

        var bytes = await svc.FileSystem.ReadFileAsync(Uri).ConfigureAwait(false);
        var hash = await svc.Io.Run(() => SHA256.HashData(bytes)).ConfigureAwait(false);
        byte[]? knownHash;
        lock (_stateGate) knownHash = _diskHash;
        if (knownHash is not null && hash.AsSpan().SequenceEqual(knownHash))
        {
            lock (_stateGate) _lastKnownStat = stat; // touched, content identical (or our own write)
            return;
        }

        if (!IsDirty)
        {
            await ApplyDiskContentAsync(bytes, stat, null, CancellationToken.None).ConfigureAwait(false); // clean: silent reload
            return;
        }

        // Dirty + changed on disk: the user decides.
        string? choice;
        _externalChangePending = true;
        try
        {
            choice = svc.Window is null
                ? null
                : await svc.Window.ShowMessageAsync(
                    MessageSeverity.Warning,
                    $"'{Path.GetFileName(Uri.LocalPath)}' has changed on disk and you have unsaved changes.",
                    [new MessageAction("Reload"), new MessageAction("Keep my changes", IsCloseAffordance: true)]).ConfigureAwait(false);
        }
        finally
        {
            _externalChangePending = false;
        }
        if (choice == "Reload") await ApplyDiskContentAsync(bytes, stat, null, CancellationToken.None).ConfigureAwait(false);
        else
            lock (_stateGate)
            {
                // Keep: remember this disk state so we do not ask again for the same content; the next save overwrites it.
                _diskHash = hash;
                _lastKnownStat = stat;
            }
    }

    private void DisposeFileState()
    {
        _watch?.Dispose();
        _watch = null;
        _registration?.Dispose();
        _registration = null;
        _uriChanged.Clear();
        _optionsChanged.Clear();
    }
}

/// <summary>Pure computation of the edits that "trim trailing whitespace" / "insert final newline" / "trim final newlines" make on save.</summary>
internal static class SaveEdits
{
    /// <summary>What an auto save after a delay leaves alone: the lines that hold a caret, and the end of the text when a caret is there.</summary>
    public sealed record CaretGuard(IReadOnlySet<int> Lines, bool AtEnd);

    public static IReadOnlyList<TextEdit> Compute(DocumentSnapshot snap, bool trimTrailingWhitespace, bool insertFinalNewline, bool trimFinalNewlines, string eol, CaretGuard? guard = null)
    {
        var edits = new List<TextEdit>();
        var lineCount = snap.LineCount;

        if (trimTrailingWhitespace)
        {
            for (var line = 0; line < lineCount; line++)
            {
                if (guard is not null && guard.Lines.Contains(line)) continue; // VS Code: the space just typed before the caret stays
                var text = snap.GetLineText(line);
                var end = text.Length;
                while (end > 0 && text[end - 1] is ' ' or '\t') end--;
                if (end < text.Length) edits.Add(new TextEdit(TextRange.FromLines(line, end, line, text.Length), string.Empty));
            }
        }

        if (trimFinalNewlines && lineCount > 2)
        {
            // "a\n\n\n" → "a\n": keep the last non-empty line and ONE terminator, drop the blank lines after it.
            var lastContent = lineCount - 1;
            while (lastContent >= 0 && snap.LineContentLength(lastContent) == 0) lastContent--;
            // VS Code keeps the blank lines up to the last caret's on an auto save after a delay
            var from = guard is { Lines.Count: > 0 } ? Math.Max(lastContent + 1, guard.Lines.Max() + 1) : lastContent + 1;
            if (lastContent >= 0 && from < lineCount - 1)
                edits.Add(new TextEdit(new TextRange(new TextPosition(from, 0), new TextPosition(lineCount - 1, 0)), string.Empty));
        }

        // (not with a caret at the very end on an auto save after a delay: the newline would push the caret onto a line of its own while the
        // user types; the next save without one there adds it)
        if (insertFinalNewline && snap.Length > 0 && guard is not { AtEnd: true })
        {
            var lastLine = lineCount - 1;
            if (snap.LineContentLength(lastLine) > 0)
            {
                var at = new TextPosition(lastLine, snap.LineContentLength(lastLine));
                // trailing-whitespace trimming may already have shortened this line: insert at the trimmed end then
                var trimmed = edits.FirstOrDefault(e => e.Range.Start.Line == lastLine && e.Range.End.Line == lastLine);
                if (trimmed is not null) at = trimmed.Range.End;
                edits.Add(TextEdit.Insert(at, eol));
            }
        }
        return edits;
    }
}
