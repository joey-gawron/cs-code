using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.SpellCheck.Engine;
using CsCode.SpellCheck.Settings;
using CsCode.SpellCheck.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCode.SpellCheck;

/// <summary>
/// What the spell checker adds to the workbench: the quick-fix provider, the commands (cspell's ids where cspell has the command), the
/// status bar item for the active file, and the settings writes behind "Add to dictionary". Started by the shell
/// (<c>ShellController</c>); every service besides the checker itself is optional.
/// </summary>
public sealed class SpellCheckHost : IDisposable
{
    public const string ToggleEnableSpellChecker = "cSpell.toggleEnableSpellChecker";
    public const string EnableForWorkspace = "cSpell.enableForWorkspace";
    public const string DisableForWorkspace = "cSpell.disableForWorkspace";
    public const string EnableForGlobal = "cSpell.enableForGlobal";
    public const string DisableForGlobal = "cSpell.disableForGlobal";
    public const string AddWordToUserDictionary = "cSpell.addWordToUserDictionary";
    public const string AddWordToWorkspaceDictionary = "cSpell.addWordToWorkspaceDictionary";
    public const string GoToNextSpellingIssue = "cSpell.goToNextSpellingIssue";
    public const string GoToPreviousSpellingIssue = "cSpell.goToPreviousSpellingIssue";
    public const string SuggestSpellingCorrections = "cSpell.suggestSpellingCorrections";
    /// <summary>CsCode's own (cspell has no such command): forbid a word (<c>cSpell.flagWords</c> in the user settings); Vim's <c>zw</c>.</summary>
    public const string FlagWord = "cscode.spellCheck.flagWord";

    public const string StatusItemId = "cscode.spellCheck";
    private const string Category = "Spell";

    private readonly SpellCheckService _service;
    private readonly IConfigurationService _config;
    private readonly ICommandService? _commands;
    private readonly ILanguageFeatureRegistry? _features;
    private readonly IStatusBarService? _status;
    private readonly IWindowService? _window;
    private readonly IWorkspaceService? _workspace;
    private readonly Func<INavigationService?> _navigation;
    private readonly ILogger _log;
    private readonly DisposableStore _subscriptions = new();
    private readonly object _statusGate = new();
    private IDisposable? _statusItem;
    private string? _statusText;
    private bool _started;

    public SpellCheckHost(SpellCheckService service, IConfigurationService config, ICommandService? commands = null, ILanguageFeatureRegistry? features = null,
        IStatusBarService? status = null, IWindowService? window = null, IWorkspaceService? workspace = null, ILanguageService? languages = null,
        Func<INavigationService?>? navigation = null, ILoggerFactory? loggerFactory = null)
    {
        _service = service;
        _config = config;
        _commands = commands;
        _features = features;
        _status = status;
        _window = window;
        _workspace = workspace;
        _navigation = navigation ?? (() => null);
        _log = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<SpellCheckHost>();
        CodeActions = new SpellCodeActionProvider(service, languages, () => _workspace);
    }

    public SpellCheckService Service => _service;
    public SpellCodeActionProvider CodeActions { get; }

    /// <summary>Starts the checker, registers the quick fixes, the commands and the status bar item. Idempotent.</summary>
    public IDisposable Start()
    {
        if (_started) return Disposable.Empty;
        _started = true;
        _subscriptions.Add(_service.Start());
        if (_features is not null) _subscriptions.Add(_features.Register<ICodeActionProvider>(DocumentSelector.All, CodeActions, SpellCheckService.Owner));
        if (_commands is not null) RegisterCommands(_commands);
        if (_status is not null)
        {
            _subscriptions.Add(_service.ResultsChanged.Subscribe(uri =>
            {
                if (_navigation()?.ActiveDocument?.Uri == uri) UpdateStatus();
            }));
            _subscriptions.Add(_service.SettingsChanged.Subscribe(_ => UpdateStatus()));
            if (_navigation() is { } nav) _subscriptions.Add(nav.ActiveDocumentChanged.Subscribe(_ => UpdateStatus()));
            UpdateStatus();
        }
        return Disposable.Create(Dispose);
    }

    private void RegisterCommands(ICommandService commands)
    {
        void Add(string id, string title, Func<CommandInvocation, Task> run)
        {
            if (commands.Has(id)) return;
            _subscriptions.Add(commands.Register(id, async invocation =>
            {
                try
                {
                    await run(invocation).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    _log.LogWarning(ex, "{Command} failed", id);
                    if (_window is not null) await _window.ShowMessageAsync(MessageSeverity.Error, $"{title}: {ex.Message}").ConfigureAwait(false);
                }
                return null;
            }, new CommandMetadata(id, title, Category)));
        }
        Add(ToggleEnableSpellChecker, "Toggle Spell Checking", _ => SetEnabledAsync(!_service.Settings.Enabled, _workspace?.Folders.Count > 0 ? ConfigurationTarget.Workspace : ConfigurationTarget.User));
        Add(EnableForWorkspace, "Enable Spell Checking for Workspace", _ => SetEnabledAsync(true, ConfigurationTarget.Workspace));
        Add(DisableForWorkspace, "Disable Spell Checking for Workspace", _ => SetEnabledAsync(false, ConfigurationTarget.Workspace));
        Add(EnableForGlobal, "Enable Spell Checking (User)", _ => SetEnabledAsync(true, ConfigurationTarget.User));
        Add(DisableForGlobal, "Disable Spell Checking (User)", _ => SetEnabledAsync(false, ConfigurationTarget.User));
        Add(AddWordToUserDictionary, "Add Word to User Dictionary", i => AddWordAsync(i, workspace: false));
        Add(AddWordToWorkspaceDictionary, "Add Word to Workspace Dictionary", i => AddWordAsync(i, workspace: true));
        Add(GoToNextSpellingIssue, "Go to Next Spelling Issue", i => GoToIssueAsync(Count(i), forward: true));
        Add(GoToPreviousSpellingIssue, "Go to Previous Spelling Issue", i => GoToIssueAsync(Count(i), forward: false));
        Add(SuggestSpellingCorrections, "Spelling Suggestions...", _ => SuggestAtCaretAsync());
        Add(FlagWord, "Mark Word as Misspelled (Flag Word)", FlagWordAsync);
    }

    private static int Count(CommandInvocation invocation)
    {
        try { return Math.Max(1, invocation.Arg<int?>(0) ?? 1); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or System.Text.Json.JsonException) { return 1; }
    }

    // ---- enable / disable ----

    private async Task SetEnabledAsync(bool enabled, ConfigurationTarget target)
    {
        if (target == ConfigurationTarget.Workspace && !(_workspace?.Folders.Count > 0)) target = ConfigurationTarget.User;
        await _config.UpdateAsync(SpellSettingsLoader.Enabled, enabled, target).ConfigureAwait(false);
        await _service.ReloadSettingsAndWaitAsync().ConfigureAwait(false);
    }

    // ---- dictionaries ----

    /// <summary>
    /// Adds the word (the command's argument, else the word at the active caret) to the user or the workspace dictionary. Every open document is
    /// checked again at once with the word accepted; the settings write lands meanwhile.
    /// </summary>
    public async Task<DictionaryWrite?> AddWordAsync(CommandInvocation invocation, bool workspace)
    {
        var word = WordArgument(invocation) ?? WordAtCaret();
        if (string.IsNullOrWhiteSpace(word)) return null;
        _service.AcceptNow([word]);
        var write = workspace
            ? await DictionaryWriter.AddToWorkspaceAsync(_config, _workspace, _service.Settings, [word]).ConfigureAwait(false)
            : await DictionaryWriter.AddToUserAsync(_config, SpellSettingsLoader.Words, [word]).ConfigureAwait(false);
        _window?.SetStatusBarMessage($"Added \"{word}\" to the {(workspace ? "workspace" : "user")} dictionary ({write.Description})", TimeSpan.FromSeconds(4));
        return write;
    }

    private async Task FlagWordAsync(CommandInvocation invocation)
    {
        var word = WordArgument(invocation) ?? WordAtCaret();
        if (string.IsNullOrWhiteSpace(word)) return;
        _service.FlagNow(word);
        await DictionaryWriter.RemoveFromUserAsync(_config, SpellSettingsLoader.Words, [word]).ConfigureAwait(false);
        await DictionaryWriter.AddToUserAsync(_config, SpellSettingsLoader.FlagWords, [word]).ConfigureAwait(false);
        _window?.SetStatusBarMessage($"\"{word}\" is now reported as a forbidden word (cSpell.flagWords)", TimeSpan.FromSeconds(4));
    }

    private static string? WordArgument(CommandInvocation invocation)
    {
        try { return invocation.Arg<string>(0); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or System.Text.Json.JsonException) { return null; }
    }

    /// <summary>The misspelt word under the active caret, else the word (identifier part) under it.</summary>
    internal string? WordAtCaret()
    {
        if (_navigation() is not { ActiveDocument: { } doc, ActiveCaret: { } caret }) return null;
        if (IssueAt(doc, caret) is { } issue) return issue.Word;
        if (caret.Line >= doc.LineCount) return null;
        var line = doc.GetLineText(caret.Line);
        var words = new List<WordSpan>();
        WordSplitter.Split(line, 0, line.Length, words);
        foreach (var w in words)
            if (caret.Character >= w.Start && caret.Character <= w.Start + w.Length) return line.Substring(w.Start, w.Length);
        return null;
    }

    private SpellIssue? IssueAt(ITextDocument doc, TextPosition caret)
    {
        if (_service.ResultFor(doc.Uri) is not { } result || result.Version != doc.Version) return null;
        foreach (var issue in result.Issues)
            if (issue.Line == caret.Line && caret.Character >= issue.Start && caret.Character <= issue.Start + issue.Length) return issue;
        return null;
    }

    // ---- navigation ----

    /// <summary>Moves the caret to the <paramref name="count"/>-th next (or previous) spelling issue of the active document, wrapping around.</summary>
    public async Task<bool> GoToIssueAsync(int count, bool forward)
    {
        if (_navigation() is not { ActiveDocument: { } doc } nav) return false;
        if (_service.ResultFor(doc.Uri) is not { Issues.Count: > 0 } result || result.Version != doc.Version)
        {
            _window?.SetStatusBarMessage("No spelling issues in this file", TimeSpan.FromSeconds(3));
            return false;
        }
        var issues = result.Issues.OrderBy(i => i.Line).ThenBy(i => i.Start).ToArray();
        var caret = nav.ActiveCaret ?? TextPosition.Zero;
        // Vim's ]s / [s: the next word starting after the caret, the previous one starting before it (from inside a word: that word's start)
        var index = forward
            ? Array.FindIndex(issues, i => new TextPosition(i.Line, i.Start) > caret)
            : Array.FindLastIndex(issues, i => new TextPosition(i.Line, i.Start) < caret);
        if (index < 0) index = forward ? 0 : issues.Length - 1;
        index = forward ? (index + count - 1) % issues.Length : ((index - (count - 1)) % issues.Length + issues.Length) % issues.Length;
        var target = issues[index];
        await nav.OpenAsync(doc.Uri, new TextRange(new TextPosition(target.Line, target.Start), new TextPosition(target.Line, target.Start + target.Length))).ConfigureAwait(false);
        return true;
    }

    // ---- suggestions at the caret (z=) ----

    private sealed record Choice(string? Replacement, string? CommandId);

    /// <summary>A quick pick of the suggestions for the word at the caret (and the dictionary actions); the choice is applied.</summary>
    public async Task SuggestAtCaretAsync()
    {
        if (_navigation() is not { ActiveDocument: { } doc, ActiveCaret: { } caret } nav || _window is null) return;
        var version = doc.Version;
        TextRange range;
        string word;
        if (IssueAt(doc, caret) is { } issue)
        {
            word = issue.Word;
            range = new TextRange(new TextPosition(issue.Line, issue.Start), new TextPosition(issue.Line, issue.Start + issue.Length));
        }
        else if (caret.Line < doc.LineCount)
        {
            var line = doc.GetLineText(caret.Line);
            var words = new List<WordSpan>();
            WordSplitter.Split(line, 0, line.Length, words);
            var hit = words.FirstOrDefault(w => caret.Character >= w.Start && caret.Character <= w.Start + w.Length);
            if (hit.Length == 0) return;
            word = line.Substring(hit.Start, hit.Length);
            range = new TextRange(new TextPosition(caret.Line, hit.Start), new TextPosition(caret.Line, hit.Start + hit.Length));
        }
        else return;
        var suggestions = await _service.SuggestAsync(word).ConfigureAwait(false);
        var items = suggestions.Select((s, i) => new QuickPickItem(s, i == 0 ? "best match" : null, Tag: new Choice(s, null))).ToList();
        items.Add(new QuickPickItem($"Add \"{word}\" to user dictionary", Tag: new Choice(null, AddWordToUserDictionary)));
        if (_workspace?.Folders.Count > 0) items.Add(new QuickPickItem($"Add \"{word}\" to workspace dictionary", Tag: new Choice(null, AddWordToWorkspaceDictionary)));
        var picked = await _window.ShowQuickPickAsync(items, new QuickPickOptions(Title: $"Spelling suggestions for \"{word}\"", Placeholder: "Select a replacement")).ConfigureAwait(false);
        if (picked?.Tag is not Choice choice) return;
        if (choice.CommandId is { } command)
        {
            await AddWordAsync(new CommandInvocation(command, [word]), command == AddWordToWorkspaceDictionary).ConfigureAwait(false);
            return;
        }
        if (doc.Version != version) return; // the text moved on while the list was open
        await nav.ApplyWorkspaceEditAsync(new WorkspaceEdit(new Dictionary<Uri, IReadOnlyList<TextEdit>> { [doc.Uri] = [new TextEdit(range, choice.Replacement!)] }),
            "Spelling correction").ConfigureAwait(false);
    }

    // ---- status bar ----

    private void UpdateStatus()
    {
        if (_status is null) return;
        var doc = _navigation()?.ActiveDocument;
        string? text = null, tooltip = null;
        if (doc is not null)
        {
            var settings = _service.Settings;
            var result = _service.ResultFor(doc.Uri);
            var state = !settings.Enabled ? SpellDocumentState.Disabled : result?.State ?? SpellDocumentState.Checked;
            var languages = string.Join(", ", settings.Languages);
            (text, tooltip) = state switch
            {
                SpellDocumentState.Disabled => ("$(circle-slash) Spell", "Spell checking is off (cSpell.enabled). Click to turn it on."),
                SpellDocumentState.LanguageNotEnabled => ("$(circle-slash) Spell", $"Spell checking is not enabled for {doc.LanguageId} (cSpell.enabledLanguageIds, cSpell.enabledFileTypes)."),
                SpellDocumentState.IgnoredPath => ("$(circle-slash) Spell", "This file matches cSpell.ignorePaths: it is not checked."),
                SpellDocumentState.LargeFile => ("$(circle-slash) Spell", "Large file: not checked."),
                SpellDocumentState.NoTokens => ("$(check) Spell", "Waiting for the editor's syntax tokens to tell comments and strings from code."),
                _ when result is { Issues.Count: > 0 } r => ($"$(book) Spell: {r.Issues.Count}{(r.Truncated ? "+" : "")}",
                    $"{r.Issues.Count}{(r.Truncated ? "+" : "")} unknown word{(r.Issues.Count == 1 ? "" : "s")} in this file ({languages}). Click to turn spell checking off."),
                _ => ("$(check) Spell", $"Spell checking is on ({languages}): {(settings.CheckIdentifiers ? "comments, strings, identifiers" : "comments and strings")} in code, all text in prose. Click to turn it off."),
            };
            if (settings.Problems.Count > 0) tooltip += "\n" + string.Join("\n", settings.Problems);
        }
        lock (_statusGate)
        {
            var key = text + "\u0001" + tooltip;
            if (key == _statusText) return;
            _statusText = key;
            if (text is null)
            {
                _statusItem?.Dispose();
                _statusItem = null;
                return;
            }
            _statusItem = _status.Set(new StatusBarItemModel(StatusItemId, text, tooltip, ToggleEnableSpellChecker, StatusBarAlignment.Right, 58));
        }
    }

    public void Dispose()
    {
        _subscriptions.Dispose();
        lock (_statusGate)
        {
            _statusItem?.Dispose();
            _statusItem = null;
        }
    }
}
