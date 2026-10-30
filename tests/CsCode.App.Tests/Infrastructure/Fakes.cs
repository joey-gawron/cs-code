using System.Text.Json;
using Avalonia.Controls;
using CsCode.App.Notifications;
using CsCode.App.Services;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Core.Theming;
using CsCode.Editor;

namespace CsCode.App.Tests.Infrastructure;

/// <summary>Editor document double: a real <see cref="InMemoryTextDocument"/> plus the file-ish surface the shell touches.</summary>
public sealed class FakeEditorDocument : IEditorDocument
{
    private readonly InMemoryTextDocument _inner;
    private readonly Emitter<IEditorDocument> _saved = new(), _dirty = new();
    private bool _isDirty;

    public FakeEditorDocument(Uri uri, string text = "", string language = "plaintext") => _inner = new InMemoryTextDocument(uri, language, text);

    public Uri Uri => _inner.Uri;
    public string LanguageId => _inner.LanguageId;
    public int Version => _inner.Version;
    public bool IsDirty => _isDirty;
    public int Length => _inner.Length;
    public int LineCount => _inner.LineCount;
    public bool IsReadOnly { get; set; }
    public bool IsUntitled => Uri.Scheme == "untitled";
    public string EncodingName => "utf-8";
    public string EndOfLine => "\n";
    public int SaveCount { get; private set; }
    public Uri? SavedAs { get; private set; }
    public Exception? SaveFailure { get; set; }
    public string GetText() => _inner.GetText();
    public string GetText(TextRange range) => _inner.GetText(range);
    public string GetLineText(int line) => _inner.GetLineText(line);
    public int OffsetAt(TextPosition position) => _inner.OffsetAt(position);
    public TextPosition PositionAt(int offset) => _inner.PositionAt(offset);
    public TextPosition Validate(TextPosition position) => _inner.Validate(position);
    public IEventSource<TextDocumentChangedEventArgs> Changed => _inner.Changed;
    public IEventSource<ITextDocument> LanguageIdChanged => _inner.LanguageIdChanged;
    public IEventSource<IEditorDocument> Saved => _saved;
    public IEventSource<IEditorDocument> DirtyChanged => _dirty;
    public void SetLanguageId(string languageId) => _inner.SetLanguageId(languageId);

    public bool ApplyEdits(IReadOnlyList<TextEdit> edits)
    {
        var ok = _inner.ApplyEdits(edits);
        if (ok) SetDirty(true);
        return ok;
    }

    public void SetDirty(bool value)
    {
        if (_isDirty == value) return;
        _isDirty = value;
        _dirty.Fire(this);
    }

    public Task SaveAsync(CancellationToken ct = default)
    {
        if (SaveFailure is not null) throw SaveFailure;
        SaveCount++;
        SetDirty(false);
        _saved.Fire(this);
        return Task.CompletedTask;
    }

    public Task SaveAsAsync(Uri target, CancellationToken ct = default)
    {
        SavedAs = target;
        return SaveAsync(ct);
    }

    public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Editor factory double. Views are plain TextBoxes (only valid inside a headless Avalonia test).</summary>
public sealed class FakeEditorViewFactory(IDocumentManager? documents = null) : IEditorViewFactory
{
    private readonly Dictionary<Uri, FakeEditorDocument> _docs = new();
    // Weak: a real editor must not keep views alive either; the shell drops its own references when a tab closes.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, FakeController> _controllers = new();
    private readonly Dictionary<Uri, IDisposable> _registrations = new();
    private int _untitled;

    public int ViewsCreated { get; private set; }
    public List<Uri> Closed { get; } = new();
    public Func<Uri, string> TextFor { get; set; } = _ => "";
    public Func<FakeEditorDocument, FakeController>? ControllerFactory { get; set; }

    public ValueTask<IEditorDocument> OpenDocumentAsync(Uri uri, CancellationToken ct = default)
    {
        if (!_docs.TryGetValue(uri, out var d))
        {
            _docs[uri] = d = new FakeEditorDocument(uri, TextFor(uri));
            if (documents is not null) _registrations[uri] = documents.Register(d); // disposed in CloseDocumentAsync, like the real editor
        }
        return ValueTask.FromResult<IEditorDocument>(d);
    }

    public IEditorDocument CreateUntitled(string? languageId = null)
    {
        var d = new FakeEditorDocument(new Uri($"untitled:Untitled-{++_untitled}"), "", languageId ?? "plaintext");
        _docs[d.Uri] = d;
        return d;
    }

    public Control CreateView(IEditorDocument document)
    {
        ViewsCreated++;
        // A plain Border: a real TextBox starts focus-dependent timers (caret blink) whose lifetime is not ours to control in tests.
        var view = new Border { Child = new TextBlock { Text = document.GetText() } };
        _controllers.Add(view, ControllerFactory?.Invoke((FakeEditorDocument)document) ?? new FakeController(document));
        return view;
    }

    public IEditorViewController? GetController(Control view) => _controllers.TryGetValue(view, out var c) ? c : null;

    public ValueTask CloseDocumentAsync(IEditorDocument document)
    {
        Closed.Add(document.Uri);
        _docs.Remove(document.Uri);
        if (_registrations.Remove(document.Uri, out var reg)) reg.Dispose();
        return ValueTask.CompletedTask;
    }

    // Weak as well: remembering destroyed views strongly would itself retain them (leak tests run through this fake).
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, object> _disposedViews = new();

    public int DisposedViewCount { get; private set; }

    /// <summary>Whether the shell handed <paramref name="view"/> back through <see cref="DisposeView"/>.</summary>
    public bool WasDisposed(Control view) => _disposedViews.TryGetValue(view, out _);

    public void DisposeView(Control view)
    {
        _disposedViews.AddOrUpdate(view, this);
        DisposedViewCount++;
        _controllers.Remove(view);
    }

    public FakeEditorDocument Doc(Uri uri) => _docs[uri];
    public int OpenCount => _docs.Count;
}

public sealed class FakeController(IEditorDocument document) : IEditorViewController
{
    private readonly Emitter<TextPosition> _caret = new();
    private readonly Emitter<IReadOnlyList<TextRange>> _sel = new();
    private TextRange? _selection;
    public IEditorDocument Document { get; } = document;
    public TextPosition Caret { get; private set; }
    public IReadOnlyList<TextRange> Selections => _selection is { } s ? [s] : [];
    public List<TextRange> Reveals { get; } = new();
    public void SetSelection(TextRange range, bool reveal = true)
    {
        _selection = range;
        Caret = range.End;
        if (reveal) Reveals.Add(range);
        _caret.Fire(Caret);
    }
    public void Reveal(TextRange range, bool center = true) => Reveals.Add(range);
    public void Focus() { }
    public bool CanUndo => false;
    public bool CanRedo => false;
    public void Undo() { }
    public void Redo() { }
    public void ShowFind(bool replace) { }
    public void FormatDocument() { }
    public void SetDebugStopLine(int? line) { }
    public IEventSource<TextPosition> CaretChanged => _caret;
    public IEventSource<IReadOnlyList<TextRange>> SelectionChanged => _sel;
    /// <summary>What a real view would add to its state: scroll offsets and folded regions.</summary>
    public (double Vertical, double Horizontal, IReadOnlyList<int> Folds) Layout { get; set; } = (0, 0, []);
    /// <summary>Every state this controller was asked to restore.</summary>
    public List<EditorViewState> Restored { get; } = new();
    public EditorViewState GetViewState() => (_selection is { } s
            ? EditorViewState.Initial with { Caret = Caret, SelectionStart = s.Start, SelectionEnd = s.End }
            : EditorViewState.Initial with { Caret = Caret, SelectionStart = Caret, SelectionEnd = Caret })
        with { VerticalOffset = Layout.Vertical, HorizontalOffset = Layout.Horizontal, FoldedStartLines = Layout.Folds };
    public void RestoreViewState(EditorViewState state)
    {
        Restored.Add(state);
        Layout = (state.VerticalOffset, state.HorizontalOffset, state.FoldedStartLines);
        SetSelection(state.SelectionStart == state.SelectionEnd ? new TextRange(state.Caret, state.Caret) : new TextRange(state.SelectionStart, state.SelectionEnd), reveal: false);
    }
    public ValueTask<IReadOnlyList<string>> GetBreadcrumbsAsync(CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<string>>([]);
    public bool IsFocused => false;
}

public sealed class FakeModalDialogs : IModalDialogs
{
    public Queue<string?> Answers { get; } = new();
    public List<(string Title, string Message, IReadOnlyList<string> Buttons)> Shown { get; } = new();

    public Task<string?> ShowAsync(string title, string message, IReadOnlyList<string> buttons, int defaultIndex = 0)
    {
        Shown.Add((title, message, buttons));
        return Task.FromResult(Answers.Count > 0 ? Answers.Dequeue() : null);
    }
}

public sealed class FakeShellHost : IShellHost
{
    public Queue<IReadOnlyList<Uri>?> OpenAnswers { get; } = new();
    public string? Clipboard { get; private set; }
    public List<Uri> Launched { get; } = new();
    public ValueTask<IReadOnlyList<Uri>?> PickOpenAsync(OpenDialogOptions options, CancellationToken ct) => ValueTask.FromResult(OpenAnswers.Count > 0 ? OpenAnswers.Dequeue() : null);
    public ValueTask<Uri?> PickSaveAsync(SaveDialogOptions options, CancellationToken ct) => ValueTask.FromResult<Uri?>(null);
    public ValueTask CopyTextAsync(string text) { Clipboard = text; return ValueTask.CompletedTask; }
    public ValueTask<bool> LaunchAsync(Uri uri) { Launched.Add(uri); return ValueTask.FromResult(true); }
}

/// <summary>IFileSystem over the real disk (tests run against temp directories). Trash is not simulated: deletes are permanent.</summary>
public sealed class DiskFileSystem : IFileSystem
{
    public ValueTask<FileStat?> StatAsync(Uri uri, CancellationToken ct = default)
    {
        var p = uri.LocalPath;
        if (Directory.Exists(p)) return ValueTask.FromResult<FileStat?>(new FileStat(FileType.Directory, 0, Directory.GetLastWriteTimeUtc(p)));
        if (File.Exists(p)) { var i = new FileInfo(p); return ValueTask.FromResult<FileStat?>(new FileStat(FileType.File, i.Length, i.LastWriteTimeUtc)); }
        return ValueTask.FromResult<FileStat?>(null);
    }

    public ValueTask<IReadOnlyList<DirectoryEntry>> ReadDirectoryAsync(Uri uri, CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<DirectoryEntry>>(new DirectoryInfo(uri.LocalPath).EnumerateFileSystemInfos()
            .Select(i => new DirectoryEntry(i.Name, i is DirectoryInfo ? FileType.Directory : FileType.File)).ToArray());

    public async ValueTask<byte[]> ReadFileAsync(Uri uri, CancellationToken ct = default) => await File.ReadAllBytesAsync(uri.LocalPath, ct);

    public async ValueTask WriteFileAsync(Uri uri, ReadOnlyMemory<byte> content, bool createParents = true, CancellationToken ct = default)
    {
        if (createParents) Directory.CreateDirectory(Path.GetDirectoryName(uri.LocalPath)!);
        await File.WriteAllBytesAsync(uri.LocalPath, content.ToArray(), ct);
    }

    public ValueTask CreateDirectoryAsync(Uri uri, CancellationToken ct = default) { Directory.CreateDirectory(uri.LocalPath); return ValueTask.CompletedTask; }

    public ValueTask DeleteAsync(Uri uri, bool recursive = false, bool useTrash = true, CancellationToken ct = default)
    {
        if (Directory.Exists(uri.LocalPath)) Directory.Delete(uri.LocalPath, recursive);
        else File.Delete(uri.LocalPath);
        return ValueTask.CompletedTask;
    }

    public ValueTask RenameAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default)
    {
        if (Directory.Exists(source.LocalPath)) Directory.Move(source.LocalPath, target.LocalPath);
        else File.Move(source.LocalPath, target.LocalPath, overwrite);
        return ValueTask.CompletedTask;
    }

    public ValueTask CopyAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default)
    {
        if (Directory.Exists(source.LocalPath)) CopyDir(source.LocalPath, target.LocalPath);
        else File.Copy(source.LocalPath, target.LocalPath, overwrite);
        return ValueTask.CompletedTask;
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }
}

/// <summary>Watcher double: tests call <see cref="Raise"/> to deliver a batch of changes to everything watching.</summary>
public sealed class FakeFileWatcher : IFileWatcher
{
    private readonly List<(Uri Root, IReadOnlyList<string>? Excludes, Action<IReadOnlyList<FileChange>> Handler, FileWatchWants? Wants)> _watches = new();
    public int ActiveWatches { get { lock (_watches) return _watches.Count; } }
    public IReadOnlyList<Uri> ActiveRoots { get { lock (_watches) return _watches.Select(w => w.Root).ToArray(); } }
    /// <summary>The exclude globs each active watch passed (null = files.watcherExclude), in the order of <see cref="ActiveRoots"/>.</summary>
    public IReadOnlyList<IReadOnlyList<string>?> ActiveExcludes { get { lock (_watches) return _watches.Select(w => w.Excludes).ToArray(); } }
    /// <summary>The <see cref="FileWatchWants"/> each active watch was given (live: read its current paths), in the order of <see cref="ActiveRoots"/>.</summary>
    public IReadOnlyList<FileWatchWants?> ActiveWants { get { lock (_watches) return _watches.Select(w => w.Wants).ToArray(); } }

    public IDisposable Watch(Uri root, bool recursive, IReadOnlyList<string>? excludeGlobs, Action<IReadOnlyList<FileChange>> onChanges) =>
        Watch(root, recursive, excludeGlobs, onChanges, null);

    public IDisposable Watch(Uri root, bool recursive, IReadOnlyList<string>? excludeGlobs, Action<IReadOnlyList<FileChange>> onChanges, FileWatchWants? wants)
    {
        var entry = (root, excludeGlobs, onChanges, wants);
        lock (_watches) _watches.Add(entry);
        return Disposable.Create(() => { lock (_watches) _watches.Remove(entry); });
    }

    public void Raise(params FileChange[] changes)
    {
        (Uri Root, IReadOnlyList<string>? Excludes, Action<IReadOnlyList<FileChange>> Handler, FileWatchWants? Wants)[] snapshot;
        lock (_watches) snapshot = _watches.ToArray();
        foreach (var w in snapshot) w.Handler(changes);
    }
}

public sealed class FakeWorkspaceService : IWorkspaceService
{
    private readonly List<Uri> _folders = new();
    private readonly Emitter<WorkspaceFoldersChangedEventArgs> _changed = new();

    public FakeWorkspaceService(IFileSystem? fs = null, IFileWatcher? watcher = null)
    {
        FileSystem = fs ?? new DiskFileSystem();
        Watcher = watcher ?? new FakeFileWatcher();
    }

    public IReadOnlyList<Uri> Folders => _folders;
    public string? Name => _folders.Count > 0 ? Path.GetFileName(_folders[0].LocalPath.TrimEnd('/')) : null;
    public IFileSystem FileSystem { get; }
    public IFileWatcher Watcher { get; }
    public List<Uri> Recents { get; } = new();
    public IReadOnlyList<Uri> RecentFolders => Recents;
    public IEventSource<WorkspaceFoldersChangedEventArgs> FoldersChanged => _changed;
    /// <summary>When set, FindFilesAsync streams these instead of walking the disk (200k-file palette tests).</summary>
    public IReadOnlyList<Uri>? SyntheticFiles { get; set; }

    public ValueTask OpenFolderAsync(Uri folder, CancellationToken ct = default)
    {
        _folders.Add(folder);
        _changed.Fire(new WorkspaceFoldersChangedEventArgs([folder], []));
        return ValueTask.CompletedTask;
    }

    public ValueTask CloseFolderAsync(CancellationToken ct = default)
    {
        var removed = _folders.ToArray();
        _folders.Clear();
        _changed.Fire(new WorkspaceFoldersChangedEventArgs([], removed));
        return ValueTask.CompletedTask;
    }

    /// <summary>When set, the next FindFilesAsync throws it (an unreadable folder, an I/O error) and clears it.</summary>
    public Exception? FindFilesFailure { get; set; }

    public async IAsyncEnumerable<Uri> FindFilesAsync(string? includeGlob, int maxResults, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (FindFilesFailure is { } failure)
        {
            FindFilesFailure = null;
            throw failure;
        }
        var n = 0;
        if (SyntheticFiles is not null)
        {
            foreach (var u in SyntheticFiles)
            {
                if (n++ >= maxResults) yield break;
                ct.ThrowIfCancellationRequested();
                yield return u;
            }
            yield break;
        }
        foreach (var f in _folders)
            foreach (var file in Directory.EnumerateFiles(f.LocalPath, "*", SearchOption.AllDirectories))
            {
                if (n++ >= maxResults) yield break;
                ct.ThrowIfCancellationRequested();
                yield return new Uri(file);
            }
        await Task.CompletedTask;
    }
}

public sealed class InMemoryStateStorage : IStateStorage
{
    private readonly Dictionary<(string, StorageScope), string> _data = new();
    public T? Get<T>(string key, StorageScope scope = StorageScope.Application) =>
        _data.TryGetValue((key, scope), out var json) ? JsonSerializer.Deserialize<T>(json, CoreJson.Options) : default;
    public void Set<T>(string key, T? value, StorageScope scope = StorageScope.Application) => _data[(key, scope)] = JsonSerializer.Serialize(value, CoreJson.Options);
    public void Remove(string key, StorageScope scope = StorageScope.Application) => _data.Remove((key, scope));
    public Task FlushAsync() => Task.CompletedTask;
    public int Count => _data.Count;
}

/// <summary>Context keys with a real when-clause evaluator (!, &amp;&amp;, ||, ==, !=, parentheses), standing in for Platform's.</summary>
public sealed class SimpleContextKeys : IContextKeyService
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
    private readonly Emitter<IReadOnlyCollection<string>> _changed = new();
    public IEventSource<IReadOnlyCollection<string>> Changed => _changed;

    public void Set(string key, object? value)
    {
        lock (_values)
        {
            if (_values.TryGetValue(key, out var cur) && Equals(cur, value)) return;
            _values[key] = value;
        }
        _changed.Fire([key]);
    }

    public object? Get(string key) { lock (_values) return _values.GetValueOrDefault(key); }
    public IContextKeyService CreateScope() => this;

    public bool Evaluate(string? whenClause)
    {
        if (string.IsNullOrWhiteSpace(whenClause)) return true;
        var tokens = Tokenize(whenClause);
        var pos = 0;
        var result = ParseOr(tokens, ref pos);
        return result;
    }

    private static List<string> Tokenize(string s)
    {
        var tokens = new List<string>();
        for (var i = 0; i < s.Length;)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c is '(' or ')' or '!' && !(c == '!' && i + 1 < s.Length && s[i + 1] == '=')) { tokens.Add(c.ToString()); i++; continue; }
            if (s.AsSpan(i).StartsWith("&&") || s.AsSpan(i).StartsWith("||") || s.AsSpan(i).StartsWith("==") || s.AsSpan(i).StartsWith("!=")) { tokens.Add(s.Substring(i, 2)); i += 2; continue; }
            var j = i;
            while (j < s.Length && !char.IsWhiteSpace(s[j]) && s[j] is not ('(' or ')') && !(s[j] == '!' && j + 1 < s.Length && s[j + 1] == '=') && !s.AsSpan(j).StartsWith("&&") && !s.AsSpan(j).StartsWith("||") && !s.AsSpan(j).StartsWith("==")) j++;
            if (j == i) j = i + 1;
            tokens.Add(s[i..j]);
            i = j;
        }
        return tokens;
    }

    private bool ParseOr(List<string> t, ref int p)
    {
        var v = ParseAnd(t, ref p);
        while (p < t.Count && t[p] == "||") { p++; var r = ParseAnd(t, ref p); v = v || r; }
        return v;
    }

    private bool ParseAnd(List<string> t, ref int p)
    {
        var v = ParseUnary(t, ref p);
        while (p < t.Count && t[p] == "&&") { p++; var r = ParseUnary(t, ref p); v = v && r; }
        return v;
    }

    private bool ParseUnary(List<string> t, ref int p)
    {
        if (p < t.Count && t[p] == "!") { p++; return !ParseUnary(t, ref p); }
        if (p < t.Count && t[p] == "(") { p++; var v = ParseOr(t, ref p); if (p < t.Count && t[p] == ")") p++; return v; }
        var key = t[p++];
        if (p < t.Count && t[p] is "==" or "!=")
        {
            var op = t[p++];
            var rhs = t[p++].Trim('\'', '"');
            var lhs = Convert.ToString(Get(key), System.Globalization.CultureInfo.InvariantCulture) ?? "";
            return op == "==" ? lhs == rhs : lhs != rhs;
        }
        return Truthy(Get(key));
    }

    private static bool Truthy(object? v) => v switch { null => false, bool b => b, string s => s.Length > 0, int i => i != 0, long l => l != 0, _ => true };
}

/// <summary>Keybinding service double with a real chord state machine and when-gating; labels formatted like the Platform's.</summary>
public sealed class FakeKeybindingService(IContextKeyService keys) : IKeybindingService
{
    private readonly List<KeyBinding> _bindings = new();
    private readonly List<KeyStroke> _pending = new();
    private readonly Emitter<object?> _changed = new();

    public IReadOnlyList<KeyBinding> All => _bindings;
    public IReadOnlyList<KeyStroke> PendingChord => _pending;
    public string ActivePreset => "visualstudio";
    public IEventSource<object?> Changed => _changed;

    public void Add(string command, string chord, string? when = null, JsonElement? args = null) =>
        _bindings.Add(new KeyBinding(command, KeyStrokeParser.ParseChord(chord), when, args, KeyBindingSource.Default));

    public KeyResolveResult Resolve(KeyStroke stroke)
    {
        _pending.Add(stroke);
        var candidates = _bindings.Where(b => !b.IsNegative && keys.Evaluate(b.When) && Matches(b.Chord)).ToList();
        var exact = candidates.LastOrDefault(b => b.Chord.Count == _pending.Count);
        if (exact is not null) { _pending.Clear(); return new KeyResolveResult(KeyResolution.Matched, exact); }
        if (candidates.Count > 0) return new KeyResolveResult(KeyResolution.Pending);
        _pending.Clear();
        return new KeyResolveResult(KeyResolution.None);
    }

    private bool Matches(IReadOnlyList<KeyStroke> chord)
    {
        if (chord.Count < _pending.Count) return false;
        for (var i = 0; i < _pending.Count; i++) if (chord[i] != _pending[i]) return false;
        return true;
    }

    public void ResetChord() => _pending.Clear();
    public IReadOnlyList<KeyBinding> GetBindingsFor(string commandId) => _bindings.Where(b => b.Command == commandId).ToArray();

    public string? GetLabel(string commandId) => GetBindingsFor(commandId).LastOrDefault() is { } b
        ? string.Join(", ", b.Chord.Select(Format))
        : null;

    private static string Format(KeyStroke s)
    {
        var parts = new List<string>();
        if (s.Ctrl) parts.Add("Ctrl");
        if (s.Shift) parts.Add("Shift");
        if (s.Alt) parts.Add("Alt");
        if (s.Meta) parts.Add("Win");
        parts.Add(s.Key.Length == 1 ? s.Key.ToUpperInvariant() : char.ToUpperInvariant(s.Key[0]) + s.Key[1..]);
        return string.Join('+', parts);
    }

    public ValueTask SetPresetAsync(string preset, CancellationToken ct = default) => ValueTask.CompletedTask;
    public void RaiseChanged() => _changed.Fire(null);
}

/// <summary>Colour theme service double implementing a small subset of the documented VS Code fallback chain.</summary>
public sealed class FakeColorThemeService : IColorThemeService
{
    private static readonly Dictionary<string, string> Fallbacks = new()
    {
        ["tab.inactiveBackground"] = "editorGroupHeader.tabsBackground",
        ["tab.unfocusedActiveBackground"] = "tab.activeBackground",
        ["button.secondaryHoverBackground"] = "button.secondaryBackground",
        ["list.focusBackground"] = "list.hoverBackground",
        ["editorGroupHeader.noTabsBackground"] = "editorGroupHeader.tabsBackground",
        ["quickInputList.focusBackground"] = "list.activeSelectionBackground",
        ["panelTitle.activeBorder"] = "focusBorder",
        ["foreground"] = "editor.foreground",
    };

    private readonly List<ColorTheme> _themes = new();
    private readonly Emitter<ColorTheme> _changed = new();
    private ColorTheme? _current;

    public ColorTheme Current => _current ?? throw new InvalidOperationException("No theme registered");
    public IReadOnlyList<ColorTheme> Available => _themes;
    public IEventSource<ColorTheme> Changed => _changed;
    public int GetColorCalls { get; private set; }

    public IDisposable Register(ColorTheme theme)
    {
        _themes.Add(theme);
        _current ??= theme;
        return Disposable.Create(() => _themes.Remove(theme));
    }

    public ValueTask SetCurrentAsync(string themeId, CancellationToken ct = default)
    {
        _current = _themes.First(t => t.Id == themeId);
        _changed.Fire(_current);
        return ValueTask.CompletedTask;
    }

    public Rgba GetColor(string key)
    {
        GetColorCalls++;
        for (var depth = 0; depth < 6; depth++)
        {
            if (Current.TryGetColor(key, out var c)) return c;
            if (!Fallbacks.TryGetValue(key, out var next)) break;
            key = next;
        }
        return new Rgba(0, 0, 0, 0);
    }
}
