using System.Globalization;

namespace CsCode.Vim;

public enum VimOptionKind { Bool, Number, String }

/// <summary>One `:set`-able option. <see cref="Get"/>/<see cref="Set"/> exchange the value as object (bool, int or string).</summary>
public sealed record VimOptionDef(string Name, string Abbreviation, VimOptionKind Kind, Func<VimOptions, object> Get, Action<VimOptions, object> Set, object Default);

/// <summary>
/// Vim options that the engine honours. Values live in plain fields so the typing path reads them without a dictionary lookup.
/// Options are session-wide (also for :setlocal) — per-buffer option scopes are a documented simplification.
/// </summary>
public sealed class VimOptions
{
    // ---- searching
    public bool IgnoreCase, SmartCase, HlSearch, IncSearch = true, WrapScan = true, Magic = true, GDefault;
    // ---- layout / display
    public bool Number, RelativeNumber, CursorLine, Wrap = true, List, FoldEnable = true;
    public int ScrollOff, SideScrollOff, Scroll;
    // ---- editing
    public bool ExpandTab, AutoIndent, SmartIndent, SmartTab, ShiftRound, JoinSpaces, StartOfLine = true, TildeOp, Hidden = true;
    public int TabStop = 8, ShiftWidth = 8, SoftTabStop, TextWidth, UndoLevels = 1000, Report = 2;
    public string IsKeyword = KeywordSet.Default, MatchPairs = "(:),{:},[:]", NrFormats = "bin,hex", Backspace = "indent,eol,start",
        WhichWrap = "b,s", Selection = "inclusive", VirtualEdit = "", FormatOptions = "tcq", Clipboard = "", CpOptions = "aABceFs", Comments = "";
    // ---- timing
    public bool Timeout = true, TTimeout = true;
    public int TimeoutLen = 1000, TTimeoutLen = 100;
    // ---- misc
    public int History = 50, MaxRegisterChars = 8_000_000;

    public KeywordSet Keywords { get; } = new();

    /// <summary>Effective shiftwidth (0 means 'tabstop').</summary>
    public int Sw => ShiftWidth > 0 ? ShiftWidth : TabStop;

    /// <summary>Effective softtabstop (negative means shiftwidth).</summary>
    public int Sts => SoftTabStop < 0 ? Sw : SoftTabStop;

    public bool VirtualOneMore => VirtualEdit.Contains("onemore", StringComparison.Ordinal) || VirtualEdit.Contains("all", StringComparison.Ordinal);
    public bool VirtualBlock => VirtualEdit.Contains("block", StringComparison.Ordinal) || VirtualEdit.Contains("all", StringComparison.Ordinal);
    public bool SelectionExclusive => Selection == "exclusive";
    public bool UnnamedClip => Clipboard.Contains("unnamed", StringComparison.Ordinal) && !Clipboard.Contains("unnamedplus", StringComparison.Ordinal);
    public bool UnnamedPlusClip => Clipboard.Contains("unnamedplus", StringComparison.Ordinal);

    public bool NrBin => Has(NrFormats, "bin");
    public bool NrOctal => Has(NrFormats, "octal");
    public bool NrHex => Has(NrFormats, "hex");
    public bool NrAlpha => Has(NrFormats, "alpha");
    public bool NrUnsigned => Has(NrFormats, "unsigned");
    public bool NrBlank => Has(NrFormats, "blank");

    /// <summary>True if the comma-separated <see cref="CpOptions"/>-like flag list (whichwrap, backspace, formatoptions...) contains <paramref name="item"/>.</summary>
    public static bool Has(string list, string item) => list.Split(',').Contains(item);

    public bool WhichWrapHas(char c) => WhichWrap.Split(',').Contains(c.ToString()) || WhichWrap.Contains("all");
    public bool FoHas(char c) => FormatOptions.Contains(c);
    public bool CpoHas(char c) => CpOptions.Contains(c);

    public static readonly IReadOnlyList<VimOptionDef> Definitions = BuildDefinitions();
    private static readonly Dictionary<string, VimOptionDef> ByName = BuildIndex();

    public static VimOptionDef? Find(string nameOrAbbreviation) => ByName.GetValueOrDefault(nameOrAbbreviation);

    private static Dictionary<string, VimOptionDef> BuildIndex()
    {
        var d = new Dictionary<string, VimOptionDef>(StringComparer.Ordinal);
        foreach (var o in Definitions)
        {
            d[o.Name] = o;
            if (o.Abbreviation.Length > 0) d[o.Abbreviation] = o;
        }
        return d;
    }

    private static IReadOnlyList<VimOptionDef> BuildDefinitions()
    {
        var l = new List<VimOptionDef>();
        void B(string n, string a, Func<VimOptions, bool> g, Action<VimOptions, bool> s) =>
            l.Add(new VimOptionDef(n, a, VimOptionKind.Bool, o => g(o), (o, v) => s(o, (bool)v), g(new VimOptions())));
        void N(string n, string a, Func<VimOptions, int> g, Action<VimOptions, int> s) =>
            l.Add(new VimOptionDef(n, a, VimOptionKind.Number, o => g(o), (o, v) => s(o, (int)v), g(new VimOptions())));
        void S(string n, string a, Func<VimOptions, string> g, Action<VimOptions, string> s) =>
            l.Add(new VimOptionDef(n, a, VimOptionKind.String, o => g(o), (o, v) => s(o, (string)v), g(new VimOptions())));

        B("ignorecase", "ic", o => o.IgnoreCase, (o, v) => o.IgnoreCase = v);
        B("smartcase", "scs", o => o.SmartCase, (o, v) => o.SmartCase = v);
        B("hlsearch", "hls", o => o.HlSearch, (o, v) => o.HlSearch = v);
        B("incsearch", "is", o => o.IncSearch, (o, v) => o.IncSearch = v);
        B("wrapscan", "ws", o => o.WrapScan, (o, v) => o.WrapScan = v);
        B("magic", "", o => o.Magic, (o, v) => o.Magic = v);
        B("gdefault", "gd", o => o.GDefault, (o, v) => o.GDefault = v);
        B("number", "nu", o => o.Number, (o, v) => o.Number = v);
        B("relativenumber", "rnu", o => o.RelativeNumber, (o, v) => o.RelativeNumber = v);
        B("cursorline", "cul", o => o.CursorLine, (o, v) => o.CursorLine = v);
        B("wrap", "", o => o.Wrap, (o, v) => o.Wrap = v);
        B("list", "", o => o.List, (o, v) => o.List = v);
        B("foldenable", "fen", o => o.FoldEnable, (o, v) => o.FoldEnable = v);
        B("expandtab", "et", o => o.ExpandTab, (o, v) => o.ExpandTab = v);
        B("autoindent", "ai", o => o.AutoIndent, (o, v) => o.AutoIndent = v);
        B("smartindent", "si", o => o.SmartIndent, (o, v) => o.SmartIndent = v);
        B("smarttab", "sta", o => o.SmartTab, (o, v) => o.SmartTab = v);
        B("shiftround", "sr", o => o.ShiftRound, (o, v) => o.ShiftRound = v);
        B("joinspaces", "js", o => o.JoinSpaces, (o, v) => o.JoinSpaces = v);
        B("startofline", "sol", o => o.StartOfLine, (o, v) => o.StartOfLine = v);
        B("tildeop", "top", o => o.TildeOp, (o, v) => o.TildeOp = v);
        B("hidden", "hid", o => o.Hidden, (o, v) => o.Hidden = v);
        B("timeout", "to", o => o.Timeout, (o, v) => o.Timeout = v);
        B("ttimeout", "", o => o.TTimeout, (o, v) => o.TTimeout = v);
        N("scrolloff", "so", o => o.ScrollOff, (o, v) => o.ScrollOff = v);
        N("sidescrolloff", "siso", o => o.SideScrollOff, (o, v) => o.SideScrollOff = v);
        N("scroll", "scr", o => o.Scroll, (o, v) => o.Scroll = v);
        N("tabstop", "ts", o => o.TabStop, (o, v) => o.TabStop = Math.Max(1, v));
        N("shiftwidth", "sw", o => o.ShiftWidth, (o, v) => o.ShiftWidth = Math.Max(0, v));
        N("softtabstop", "sts", o => o.SoftTabStop, (o, v) => o.SoftTabStop = v);
        N("textwidth", "tw", o => o.TextWidth, (o, v) => o.TextWidth = Math.Max(0, v));
        N("undolevels", "ul", o => o.UndoLevels, (o, v) => o.UndoLevels = v);
        N("report", "", o => o.Report, (o, v) => o.Report = v);
        N("timeoutlen", "tm", o => o.TimeoutLen, (o, v) => o.TimeoutLen = Math.Max(0, v));
        N("ttimeoutlen", "ttm", o => o.TTimeoutLen, (o, v) => o.TTimeoutLen = v);
        N("history", "hi", o => o.History, (o, v) => o.History = v);
        S("iskeyword", "isk", o => o.IsKeyword, (o, v) => { o.IsKeyword = v; o.Keywords.Parse(v); });
        S("matchpairs", "mps", o => o.MatchPairs, (o, v) => o.MatchPairs = v);
        S("nrformats", "nf", o => o.NrFormats, (o, v) => o.NrFormats = v);
        S("backspace", "bs", o => o.Backspace, (o, v) => o.Backspace = v);
        S("whichwrap", "ww", o => o.WhichWrap, (o, v) => o.WhichWrap = v);
        S("selection", "sel", o => o.Selection, (o, v) => o.Selection = v);
        S("virtualedit", "ve", o => o.VirtualEdit, (o, v) => o.VirtualEdit = v);
        S("formatoptions", "fo", o => o.FormatOptions, (o, v) => o.FormatOptions = v);
        S("clipboard", "cb", o => o.Clipboard, (o, v) => o.Clipboard = v);
        S("cpoptions", "cpo", o => o.CpOptions, (o, v) => o.CpOptions = v);
        S("comments", "com", o => o.Comments, (o, v) => o.Comments = v);
        return l;
    }

    public static string Format(VimOptionDef def, object value) => value switch
    {
        bool b => (b ? "  " : "no") + def.Name,
        int i => $"  {def.Name}={i.ToString(CultureInfo.InvariantCulture)}",
        _ => $"  {def.Name}={value}",
    };

    /// <summary>Copies every option from <paramref name="other"/> (used when a session is configured from settings).</summary>
    public void CopyFrom(VimOptions other)
    {
        foreach (var d in Definitions) d.Set(this, d.Get(other));
    }
}
