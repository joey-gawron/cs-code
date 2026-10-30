using System.Text.Json;
using CsCode.Core.Platform;

namespace CsCode.Vim;

/// <summary>The <c>vim.*</c> setting keys (VSCodeVim-compatible names) and their registered defaults.</summary>
public static class VimSettings
{
    /// <summary>The master switch (default on). <see cref="Enabled"/> is the older spelling and still honoured: Vim is on only when neither is false.</summary>
    public const string Enable = "vim.enable";
    public const string Enabled = "vim.enabled";
    public const string UseSystemClipboard = "vim.useSystemClipboard";
    public const string Leader = "vim.leader";
    public const string HlSearch = "vim.hlsearch";
    public const string IncSearch = "vim.incsearch";
    public const string IgnoreCase = "vim.ignorecase";
    public const string SmartCase = "vim.smartcase";
    public const string WrapScan = "vim.wrapscan";
    public const string StartInInsertMode = "vim.startInInsertMode";
    public const string Timeout = "vim.timeout";
    public const string HandleKeys = "vim.handleKeys";
    public const string PersistRegisters = "vim.persistRegisters";
    public const string MaxRegisterChars = "vim.maxRegisterChars";
    public const string CursorStylePerMode = "vim.cursorStylePerMode";
    public const string AutoSwitchInputMethod = "vim.autoSwitchInputMethod";
    public const string VimrcEnable = "vim.vimrc.enable";
    public const string VimrcPath = "vim.vimrc.path";

    public const string Surround = "vim.surround";
    public const string Commentary = "vim.commentary";
    public const string Sneak = "vim.sneak";
    public const string EasyMotion = "vim.easymotion";
    public const string CamelCaseMotion = "vim.camelCaseMotion";
    public const string ArgumentObjects = "vim.argumentObjects";
    public const string ReplaceWithRegister = "vim.replaceWithRegister";
    public const string HighlightedYank = "vim.highlightedyank";
    public const string Targets = "vim.targets";

    /// <summary>The ten key-binding settings: <c>vim.{mode}ModeKeyBindings</c> and <c>...NonRecursive</c>.</summary>
    public static readonly IReadOnlyList<(string Key, MapModes Modes, bool NonRecursive)> KeyBindingSettings =
    [
        ("vim.normalModeKeyBindings", MapModes.Normal, false),
        ("vim.normalModeKeyBindingsNonRecursive", MapModes.Normal, true),
        ("vim.insertModeKeyBindings", MapModes.Insert, false),
        ("vim.insertModeKeyBindingsNonRecursive", MapModes.Insert, true),
        ("vim.visualModeKeyBindings", MapModes.Visual | MapModes.Select, false),
        ("vim.visualModeKeyBindingsNonRecursive", MapModes.Visual | MapModes.Select, true),
        ("vim.operatorPendingModeKeyBindings", MapModes.OperatorPending, false),
        ("vim.operatorPendingModeKeyBindingsNonRecursive", MapModes.OperatorPending, true),
        ("vim.commandLineModeKeyBindings", MapModes.CommandLine, false),
        ("vim.commandLineModeKeyBindingsNonRecursive", MapModes.CommandLine, true),
    ];

    /// <summary>Mode names of <c>vim.cursorStylePerMode.{name}</c> and the modes they style.</summary>
    public static readonly IReadOnlyList<(string Name, VimMode[] Modes, string Default)> CursorStyleSettings =
    [
        ("normal", [VimMode.Normal, VimMode.InsertNormal], "block"),
        ("insert", [VimMode.Insert], "line"),
        ("replace", [VimMode.Replace], "underline"),
        ("visual", [VimMode.Visual], "block"),
        ("visualline", [VimMode.VisualLine], "block"),
        ("visualblock", [VimMode.VisualBlock], "block"),
        ("operatorpending", [VimMode.OperatorPending], "underline"),
        ("commandline", [VimMode.CommandLine], "line"),
    ];

    private static JsonElement J(object? v) => JsonSerializer.SerializeToElement(v);

    /// <summary>The properties registered with <see cref="IConfigurationService.RegisterProperties"/>.</summary>
    public static IReadOnlyList<ConfigurationProperty> Properties { get; } = BuildProperties();

    private static IReadOnlyList<ConfigurationProperty> BuildProperties()
    {
        var l = new List<ConfigurationProperty>
        {
            new(Enable, J(true), "Vim emulation in editors (on by default). The command \"Toggle Vim Mode\" switches it for the session."),
            new(Enabled, J(true), "Older name of vim.enable; Vim is off when either is false."),
            new(UseSystemClipboard, J(false), "Use the system clipboard for the unnamed register (yank, delete and put)."),
            new(Leader, J("\\"), "The key that <leader> expands to in key bindings."),
            new(HlSearch, J(false), "Highlight all matches of the last search."),
            new(IncSearch, J(true), "Show the next match while typing a search pattern."),
            new(IgnoreCase, J(true), "Ignore case in search patterns."),
            new(SmartCase, J(true), "Override ignorecase when the search pattern contains an uppercase letter."),
            new(WrapScan, J(true), "Searches wrap around the end of the file."),
            new(StartInInsertMode, J(false), "Start in Insert mode instead of Normal mode."),
            new(Timeout, J(1000), "Milliseconds to wait for the next key of an ambiguous key binding (timeoutlen)."),
            new(HandleKeys, J(new Dictionary<string, bool>()), "Keys to hand to the editor instead of Vim, e.g. {\"<C-c>\": false}."),
            new(PersistRegisters, J(true), "Persist the named registers a-z (and so recorded macros) across sessions."),
            new(MaxRegisterChars, J(8_000_000), "Largest text a register may hold, in characters."),
            new(AutoSwitchInputMethod, J(new Dictionary<string, object>()), "Accepted for VSCodeVim compatibility; ignored."),
            new(VimrcEnable, J(true), "Load the vimrc file at startup."),
            // Machine scope (lsp-core-1): a workspace must not choose the file whose mappings run on every key.
            new(VimrcPath, J(""), "Path of the vimrc file (\"~/.vimrc\" to use your Vim configuration). Empty: 'vimrc' in the CsCode user data directory; ~/.vimrc is never read unless named here. User settings only.") { Scope = ConfigurationScope.Machine },
            new(Surround, J(true), "Enable the vim-surround emulation (ys, ds, cs, S)."),
            new(Commentary, J(true), "Enable the vim-commentary emulation (gc, gcc)."),
            new(Sneak, J(false), "Enable the vim-sneak emulation (s, S, z)."),
            new(EasyMotion, J(false), "Enable the easymotion emulation."),
            new(CamelCaseMotion, J(false), "Enable camelCaseMotion (<leader>w, <leader>b, <leader>e)."),
            new(ArgumentObjects, J(false), "Enable the argument text objects (ia, aa)."),
            new(ReplaceWithRegister, J(false), "Enable ReplaceWithRegister (gr)."),
            new(HighlightedYank, J(true), "Flash the yanked text."),
            new(Targets, J(false), "Enable the targets.vim text objects."),
        };
        // Machine scope (lsp-core-1): a binding's "commands" run any command with any arguments on a plain key press — from a cloned repository's
        // .vscode/settings.json, "j" could send a shell line to the terminal (workbench.action.terminal.sendSequence). User settings only.
        foreach (var (key, _, nonRec) in KeyBindingSettings)
            l.Add(new ConfigurationProperty(key, J(Array.Empty<object>()), (nonRec
                ? "Key bindings that do not recurse: [{\"before\": [\"j\", \"j\"], \"after\": [\"<Esc>\"], \"commands\": [{\"command\": \"...\", \"args\": []}]}]."
                : "Key bindings that expand recursively: [{\"before\": [\"j\", \"j\"], \"after\": [\"<Esc>\"], \"commands\": [{\"command\": \"...\", \"args\": []}]}].") + " User settings only.")
            { Scope = ConfigurationScope.Machine });
        foreach (var (name, _, def) in CursorStyleSettings)
            l.Add(new ConfigurationProperty($"{CursorStylePerMode}.{name}", J(def), $"Cursor shape in {name} mode: block, line, underline, block-outline, line-thin or underline-thin."));
        return l;
    }

    public static bool TryParseCursorShape(string? text, out VimCursorShape shape)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "block": shape = VimCursorShape.Block; return true;
            case "line": shape = VimCursorShape.Line; return true;
            case "underline": shape = VimCursorShape.Underline; return true;
            case "block-outline": shape = VimCursorShape.BlockOutline; return true;
            case "line-thin": shape = VimCursorShape.LineThin; return true;
            case "underline-thin": shape = VimCursorShape.UnderlineThin; return true;
        }
        shape = default;
        return false;
    }
}
