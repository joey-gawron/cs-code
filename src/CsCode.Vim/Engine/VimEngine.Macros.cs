using System.Text;

namespace CsCode.Vim;

// Vim macros: q{reg} records the TYPED keys (before mappings are applied) as register text in Vim's own key encoding; @{reg} pushes that text
// back as keys. Errors flush the stuffed keys, which aborts the rest of the macro AND any enclosing count, exactly like Vim.
public sealed partial class VimEngine
{
    private char _recReg;
    private readonly StringBuilder _recBuf = new();
    private int _recLastCmdLen;
    private bool _macroAborted;
    private int _nest;

    /// <summary>Maximum number of keys one top-level command may execute through macros/mappings (runaway guard).</summary>
    public long MacroKeyBudget { get; set; } = 20_000_000;

    /// <summary>Maximum wall-clock time of macro execution started by one typed key (runaway guard).</summary>
    public TimeSpan MacroTimeBudget { get; set; } = TimeSpan.FromSeconds(10);

    private long _macroKeys;
    private long _macroStartTicks;

    private void InitMacroPersistence()
    {
        // registers are persisted by the session (see VimSession.RegistersChanged); nothing to do per engine
    }

    private void RecordTyped(int rawKey)
    {
        _macroKeys = 0;
        _macroAborted = false;
        _macroStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_recReg == 0) return;
        var before = _recBuf.Length;
        Keys.Encode(_recBuf, rawKey & ~NoRemapFlag);
        _recLastCmdLen = _recBuf.Length - before;
    }

    /// <summary>Called for each stuffed key: enforces the runaway guard.</summary>
    private bool CheckMacroBudget()
    {
        if (++_macroKeys > MacroKeyBudget)
        {
            Error("E-macro: macro aborted (key budget exceeded)");
            return false;
        }
        if ((_macroKeys & 1023) == 0 && System.Diagnostics.Stopwatch.GetElapsedTime(_macroStartTicks) > MacroTimeBudget)
        {
            Error("E-macro: macro aborted (time budget exceeded)");
            return false;
        }
        return true;
    }

    private void RecordCommand(int reg)
    {
        if (_recReg != 0)
        {
            // stop recording; the `q` that stopped it was recorded last
            var text = _recBuf.ToString();
            var cut = Math.Min(_recLastCmdLen, text.Length);
            text = text[..^cut];
            // when stopped via Ctrl-O q from Insert mode, drop the Ctrl-O as well
            if (_restartEdit && text.EndsWith('\u000f')) text = text[..^1];
            var name = _recReg;
            _recReg = '\0';
            _recBuf.Clear();
            StoreMacro(name, text);
            _recordingChanged.Fire(null);
            Host.OnRecordingChanged(null);
            return;
        }
        if (!(reg is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '"')) { Beep(); return; }
        _recReg = (char)reg;
        _recBuf.Clear();
        _recLastCmdLen = 0;
        _recordingChanged.Fire((char)reg);
        Host.OnRecordingChanged((char)reg);
    }

    private void StoreMacro(char name, string text)
    {
        var target = _s.Registers.ForWrite(name, out var append);
        if (target is null) return;
        var lines = text.Split('\n');
        RegisterFile.Store(target, lines, VimRegisterType.Character, 0, append);
        NotifyNamedRegisterChange();
    }

    private void NotifyNamedRegisterChange() => _s.RegistersChanged?.Invoke(_s.Registers);

    /// <summary>The register text as the key stream Vim would execute: lines joined by NL, a linewise register ends with NL.</summary>
    private static string MacroTextOf(Register r)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < r.Lines.Length; i++)
        {
            sb.Append(r.Lines[i]);
            if (r.Type == VimRegisterType.Line || i < r.Lines.Length - 1) sb.Append('\n');
        }
        return sb.ToString();
    }

    private void ExecuteRegisterMacro(int reg, int count)
    {
        if (reg == '@')
        {
            if (_s.Search.LastMacroRegister == '\0') { Error("E748: No previously used register"); return; }
            reg = _s.Search.LastMacroRegister;
        }
        if (reg == ':')
        {
            if (_s.Registers.LastCommandLine.Length == 0) { Error("E30: No previous command line"); return; }
            _s.Search.LastMacroRegister = ':';
            for (var i = 0; i < count && !_macroAborted; i++) ExecuteExLine(_s.Registers.LastCommandLine);
            return;
        }
        if (reg is '%' or '#' || !RegisterFile.IsValidName(reg) || reg == '_') { Error($"E354: Invalid register name: '{char.ConvertFromUtf32(reg)}'"); return; }
        Register? r;
        if (reg == '.') r = _s.Registers.ForRead('.');
        else r = _s.Registers.ForRead(reg == '"' ? 0 : reg, () => Host.ReadClipboard());
        _s.Search.LastMacroRegister = (char)reg;
        if (r is null || r.IsEmpty) { Beep(); return; }
        _macroAborted = false;
        StuffText(MacroTextOf(r), count);
    }
}
