using System.Runtime.CompilerServices;
using CsCode.Core.Lifecycle;

namespace CsCode.Vim;

/// <summary>Which Vim modes a mapping applies to (the letters of :nmap :vmap :xmap :smap :omap :imap :cmap).</summary>
[Flags]
public enum MapModes
{
    None = 0,
    Normal = 1,
    Visual = 2,
    Select = 4,
    OperatorPending = 8,
    Insert = 16,
    CommandLine = 32,
    /// <summary>`:map` = n + v(x+s) + o.</summary>
    All = Normal | Visual | Select | OperatorPending,
    InsertAndCommandLine = Insert | CommandLine,
}

/// <summary>An app command (or Ex command, when it starts with ':') run by a key binding from `vim.*ModeKeyBindings`.</summary>
public sealed record VimBindingCommand(string Command, IReadOnlyList<object?>? Args);

public sealed class Mapping
{
    public required int[] Lhs { get; init; }
    public int[] Rhs { get; init; } = [];
    public MapModes Modes { get; init; }
    public bool NoRemap { get; init; }
    public bool Silent { get; init; }
    public bool NoWait { get; init; }
    public bool Expr { get; init; }
    public string LhsText { get; init; } = "";
    public string RhsText { get; init; } = "";
    public IReadOnlyList<VimBindingCommand>? Commands { get; init; }
    /// <summary>Where it came from ("vimrc", "settings", ":map") so reloading settings replaces only its own.</summary>
    public string Origin { get; init; } = "user";
}

/// <summary>The mapping table shared by a session. Lookup is by exact lhs or by "is a prefix of a longer lhs" (for timeoutlen disambiguation).</summary>
public sealed class MappingTable
{
    private readonly List<Mapping> _items = [];
    public int Version { get; private set; }
    public IReadOnlyList<Mapping> Items => _items;

    public bool Any(MapModes mode)
    {
        foreach (var m in _items) if ((m.Modes & mode) != 0) return true;
        return false;
    }

    public void Add(Mapping m)
    {
        // A new mapping replaces an existing one with the same lhs in the same modes.
        for (var i = 0; i < _items.Count; i++)
        {
            var e = _items[i];
            if ((e.Modes & m.Modes) != 0 && e.Lhs.AsSpan().SequenceEqual(m.Lhs))
            {
                var remaining = e.Modes & ~m.Modes;
                if (remaining == MapModes.None) { _items.RemoveAt(i); i--; }
                else _items[i] = Clone(e, remaining);
            }
        }
        _items.Add(m);
        Version++;
    }

    public bool Remove(int[] lhs, MapModes modes)
    {
        var removed = false;
        for (var i = 0; i < _items.Count; i++)
        {
            var e = _items[i];
            if ((e.Modes & modes) == 0 || !e.Lhs.AsSpan().SequenceEqual(lhs)) continue;
            var remaining = e.Modes & ~modes;
            if (remaining == MapModes.None) { _items.RemoveAt(i); i--; } else _items[i] = Clone(e, remaining);
            removed = true;
        }
        if (removed) Version++;
        return removed;
    }

    public void Clear(MapModes modes)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            var e = _items[i];
            if ((e.Modes & modes) == 0) continue;
            var remaining = e.Modes & ~modes;
            if (remaining == MapModes.None) { _items.RemoveAt(i); i--; } else _items[i] = Clone(e, remaining);
        }
        Version++;
    }

    public void RemoveOrigin(string origin)
    {
        if (_items.RemoveAll(m => m.Origin == origin) > 0) Version++;
    }

    private static Mapping Clone(Mapping m, MapModes modes) => new()
    {
        Lhs = m.Lhs, Rhs = m.Rhs, Modes = modes, NoRemap = m.NoRemap, Silent = m.Silent, NoWait = m.NoWait, Expr = m.Expr,
        LhsText = m.LhsText, RhsText = m.RhsText, Commands = m.Commands, Origin = m.Origin,
    };

    /// <summary>Longest-needed lookup: exact match (if any) and whether any longer mapping starts with <paramref name="keys"/>.</summary>
    public (Mapping? Exact, bool LongerExists) Lookup(MapModes mode, ReadOnlySpan<int> keys)
    {
        Mapping? exact = null;
        var longer = false;
        foreach (var m in _items)
        {
            if ((m.Modes & mode) == 0) continue;
            var lhs = m.Lhs;
            if (lhs.Length < keys.Length) continue;
            if (!lhs.AsSpan(0, keys.Length).SequenceEqual(keys)) continue;
            if (lhs.Length == keys.Length) exact = m; // later definitions win
            else longer = true;
        }
        return (exact, longer);
    }
}

/// <summary>Search/substitute/find memory that survives across buffers.</summary>
public sealed class SearchMemory
{
    public string Pattern = "";
    public bool Forward = true;
    public string Offset = "";           // e.g. "e+1", "s-2", "+1", "b1"
    public string SubPattern = "";       // last pattern used by :s (for ~ and :&)
    public string SubReplacement = "";
    public string SubFlags = "";
    public bool HighlightActive = true;  // :nohlsearch clears until the next search
    public int FindChar;                 // f/F/t/T
    public bool FindForward = true;
    public bool FindTill;
    public string LastExCommand = "";
    public char LastMacroRegister;
    public string LastGlobalCommand = "";
}

/// <summary>
/// State shared by every editor view that uses Vim: options, registers, mappings, search memory and per-buffer state
/// (undo tree, marks). One session per application; engines (one per view) share it so registers and options are global like in Vim.
/// </summary>
public sealed class VimSession : IDisposable
{
    private readonly ConditionalWeakTable<IVimBuffer, BufferState> _buffers = new();
    private readonly DisposableStore _disposables = new();

    public VimOptions Options { get; } = new();
    public RegisterFile Registers { get; } = new();
    public MappingTable Mappings { get; } = new();
    public SearchMemory Search { get; } = new();

    public string Leader { get; set; } = "\\";
    public string LocalLeader { get; set; } = "\\";

    /// <summary>Plugin emulation switches (vim.surround, vim.commentary, ...).</summary>
    public VimPluginFlags Plugins { get; set; } = new();

    /// <summary>Per-mode cursor styles (vim.cursorStylePerMode.*).</summary>
    public Dictionary<VimMode, VimCursorShape> CursorStyles { get; } = new()
    {
        [VimMode.Normal] = VimCursorShape.Block, [VimMode.Insert] = VimCursorShape.Line, [VimMode.Replace] = VimCursorShape.Underline,
        [VimMode.Visual] = VimCursorShape.Block, [VimMode.VisualLine] = VimCursorShape.Block, [VimMode.VisualBlock] = VimCursorShape.Block,
        [VimMode.OperatorPending] = VimCursorShape.Underline, [VimMode.CommandLine] = VimCursorShape.Line, [VimMode.InsertNormal] = VimCursorShape.Block,
    };

    /// <summary>Keys that must NOT be handled by Vim (vim.handleKeys {"&lt;C-c&gt;": false}); they pass through to the editor.</summary>
    public HashSet<int> UnhandledKeys { get; } = [];

    public bool UseSystemClipboard { get; set; }

    /// <summary>
    /// The clipboard register a put without a register name reads (Vim's adjust_clip_reg): <c>+</c> with 'clipboard' unnamedplus or
    /// <see cref="UseSystemClipboard"/>, <c>*</c> with 'clipboard' unnamed alone; null when the unnamed register is Vim's own.
    /// </summary>
    public char? UnnamedClipboardRegister => UseSystemClipboard || Options.UnnamedPlusClip ? '+' : Options.UnnamedClip ? '*' : null;
    public bool PersistRegisters { get; set; } = true;
    public bool StartInInsertMode { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>Hook for hosts that persist registers (the DI composition wires IStateStorage here).</summary>
    public Action<RegisterFile>? RegistersChanged { get; set; }

    /// <summary>Global (A-Z) marks: buffer uri + position.</summary>
    internal Dictionary<char, (Uri? Uri, Core.Text.TextPosition Pos)> GlobalMarks { get; } = new();

    /// <summary>Reads a script file for :source (the host decides how; may be a cached read). Null = :source unavailable.</summary>
    public Func<string, string?>? ReadFile { get; set; }

    internal BufferState StateFor(IVimBuffer buffer) => _buffers.GetValue(buffer, _ => new BufferState());

    public void Dispose() => _disposables.Dispose();
    public void Own(IDisposable d) => _disposables.Add(d);
}

/// <summary>Switches for the built-in plugin emulations (VSCodeVim parity).</summary>
public sealed class VimPluginFlags
{
    public bool Surround { get; set; } = true;
    public bool Commentary { get; set; } = true;
    public bool Sneak { get; set; }
    public bool EasyMotion { get; set; }
    public bool CamelCaseMotion { get; set; }
    public bool ArgumentObjects { get; set; }
    public bool ReplaceWithRegister { get; set; }
    public bool HighlightedYank { get; set; } = true;
    public bool Targets { get; set; }
}
