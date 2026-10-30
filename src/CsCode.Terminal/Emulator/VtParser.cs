namespace CsCode.Terminal.Emulator;

/// <summary>Parameters of a CSI (or DCS) sequence, reused by the parser between sequences (do not keep a reference).</summary>
public sealed class VtParams
{
    public const int Max = 32;
    /// <summary>Parameter values are clamped to this (xterm's limit is smaller; nothing needs more).</summary>
    public const int MaxValue = 65535;

    private readonly int[] _values = new int[Max];
    private readonly bool[] _isSub = new bool[Max];
    private readonly bool[] _given = new bool[Max];

    /// <summary>Number of parameters (sub-parameters included).</summary>
    public int Count { get; private set; }

    /// <summary>The value of parameter <paramref name="index"/>, or <paramref name="fallback"/> when it is missing or empty (0 counts as given).</summary>
    public int Get(int index, int fallback = 0) => index < Count && _given[index] ? _values[index] : fallback;

    /// <summary>Like <see cref="Get"/>, but 0 also means "default" (cursor movement counts: CUU 0 moves one line).</summary>
    public int GetNonZero(int index, int fallback = 1) => Get(index, fallback) is var v && v == 0 ? fallback : v;

    public bool IsGiven(int index) => index < Count && _given[index];

    /// <summary>True when parameter <paramref name="index"/> was separated from the previous one by ':' (a sub-parameter).</summary>
    public bool IsSub(int index) => index < Count && _isSub[index];

    /// <summary>Raw value (0 when missing).</summary>
    public int this[int index] => index < Count ? _values[index] : 0;

    internal void Reset()
    {
        Count = 0;
        _overflow = false;
        _digits = false;
    }

    private bool _overflow;
    private bool _digits;

    internal void Digit(int d)
    {
        if (Count == 0) Begin(false);
        if (_overflow) return;
        var i = Count - 1;
        var v = _values[i] * 10 + d;
        _values[i] = v > MaxValue ? MaxValue : v;
        _given[i] = true;
        _digits = true;
    }

    internal void Separator(bool sub)
    {
        if (Count == 0) Begin(false);
        if (Count >= Max) { _overflow = true; return; }
        Begin(sub);
    }

    private void Begin(bool sub)
    {
        _values[Count] = 0;
        _given[Count] = false;
        _isSub[Count] = sub;
        Count++;
        _digits = false;
    }

    /// <summary>Whether any parameter text was seen (distinguishes "CSI m" from "CSI 0 m" where it matters).</summary>
    internal bool HasDigits => _digits || Count > 1;

    public override string ToString()
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < Count; i++)
        {
            if (i > 0) sb.Append(_isSub[i] ? ':' : ';');
            if (_given[i]) sb.Append(_values[i]);
        }
        return sb.ToString();
    }
}

/// <summary>What the parser calls for each recognised element of the stream.</summary>
public interface IVtHandler
{
    /// <summary>A printable character (a Unicode scalar value, never a control).</summary>
    void Print(int codePoint);

    /// <summary>A run of printable ASCII (0x20-0x7E): the fast path for ordinary text.</summary>
    void PrintAscii(ReadOnlySpan<byte> text);

    /// <summary>A C0 control (0x00-0x1F) or C1 control (0x80-0x9F) that is not a sequence introducer.</summary>
    void Execute(int control);

    /// <summary>An escape sequence: up to two intermediates packed low byte first, and the final character.</summary>
    void EscDispatch(int intermediates, char final);

    /// <summary>A control sequence: private marker ('?', '>', '=', '&lt;' or 0), parameters, intermediates and final character.</summary>
    void CsiDispatch(char prefix, VtParams parameters, int intermediates, char final);

    /// <summary>An operating system command's payload (the bytes between "ESC ]" and BEL/ST), already bounded.</summary>
    void OscDispatch(ReadOnlySpan<byte> payload);

    /// <summary>A device control string (DCS): prefix, parameters, intermediates, final and its (bounded) data.</summary>
    void DcsDispatch(char prefix, VtParams parameters, int intermediates, char final, ReadOnlySpan<byte> data);
}

/// <summary>
/// The VT500-series parser after Paul Williams' DEC ANSI state machine (vt100.net/emu/dec_ansi_parser), as xterm.js implements it:
/// UTF-8 is decoded first (invalid bytes become U+FFFD, maximal subpart replacement), and decoded C1 code points (U+0080-U+009F) act as 8-bit
/// controls. CSI parameters accept ':' sub-parameters (SGR 4:3, 38:2::r:g:b). Everything is bounded: 32 parameters, 2 intermediates, OSC
/// payloads up to <see cref="MaxOscBytes"/> and DCS data up to <see cref="MaxDcsBytes"/> (longer strings are consumed and dropped), so a
/// hostile program cannot make the parser allocate without limit. The parser never throws on input.
/// </summary>
public sealed class VtParser(IVtHandler handler)
{
    public const int MaxOscBytes = 1 << 20;
    public const int MaxDcsBytes = 64 * 1024;
    private const int MaxIntermediates = 2;

    private enum State : byte
    {
        Ground, Escape, EscapeIntermediate, CsiEntry, CsiParam, CsiIntermediate, CsiIgnore,
        DcsEntry, DcsParam, DcsIntermediate, DcsPassthrough, DcsIgnore, OscString, SosPmApcString,
    }

    private readonly IVtHandler _h = handler;
    private readonly VtParams _params = new();
    private State _state;
    private char _prefix;
    private int _intermediates;
    private int _intermediateCount;
    private bool _intermediateOverflow;
    private char _dcsFinal;

    // UTF-8 decoder
    private int _utf8Cp;
    private int _utf8Needed;
    private int _utf8Lower = 0x80, _utf8Upper = 0xBF;

    // OSC / DCS string buffer (grows up to its bound, shrinks back after a large string)
    private byte[] _string = new byte[256];
    private int _stringLength;
    private bool _stringOverflow;
    private bool _stringEsc; // an ESC was seen inside a string: '\' completes ST, anything else aborts the string
    private bool _stringC2;  // a 0xC2 byte was seen inside a string: 0x9C next makes it the UTF-8 encoded ST

    /// <summary>Bytes in the string buffer (tests: proves the bound).</summary>
    internal int StringCapacity => _string.Length;

    public void Feed(ReadOnlySpan<byte> data)
    {
        var i = 0;
        while (i < data.Length)
        {
            var b = data[i];
            if (_state is State.OscString or State.DcsPassthrough or State.DcsIgnore or State.SosPmApcString)
            {
                i++;
                StringByte(b);
                continue;
            }
            if (_utf8Needed == 0)
            {
                if (b < 0x80)
                {
                    if (_state == State.Ground && b >= 0x20 && b < 0x7F)
                    {
                        var start = i;
                        while (i < data.Length && data[i] >= 0x20 && data[i] < 0x7F) i++;
                        _h.PrintAscii(data[start..i]);
                        continue;
                    }
                    i++;
                    Advance(b);
                    continue;
                }
                i++;
                StartUtf8(b);
                continue;
            }
            // inside a multi-byte sequence
            if (b < _utf8Lower || b > _utf8Upper)
            {
                // invalid continuation: the partial sequence becomes U+FFFD and this byte is processed afresh
                _utf8Needed = 0;
                Advance(0xFFFD);
                continue;
            }
            i++;
            _utf8Lower = 0x80;
            _utf8Upper = 0xBF;
            _utf8Cp = (_utf8Cp << 6) | (b & 0x3F);
            if (--_utf8Needed == 0) Advance(_utf8Cp);
        }
    }

    private void StartUtf8(byte b)
    {
        switch (b)
        {
            case >= 0xC2 and <= 0xDF: _utf8Needed = 1; _utf8Cp = b & 0x1F; break;
            case >= 0xE0 and <= 0xEF:
                _utf8Needed = 2;
                _utf8Cp = b & 0x0F;
                if (b == 0xE0) _utf8Lower = 0xA0; // overlong
                if (b == 0xED) _utf8Upper = 0x9F; // surrogates
                break;
            case >= 0xF0 and <= 0xF4:
                _utf8Needed = 3;
                _utf8Cp = b & 0x07;
                if (b == 0xF0) _utf8Lower = 0x90;
                if (b == 0xF4) _utf8Upper = 0x8F;
                break;
            default: Advance(0xFFFD); break; // stray continuation byte, C0/C1 lead, F5-FF
        }
    }

    /// <summary>Resets to the ground state (RIS, or a host that wants to discard a half-received sequence).</summary>
    public void Reset()
    {
        _state = State.Ground;
        _utf8Needed = 0;
        _utf8Lower = 0x80;
        _utf8Upper = 0xBF;
        ClearString();
    }

    private void Advance(int c)
    {
        // "anywhere" transitions
        switch (c)
        {
            case 0x18 or 0x1A: _state = State.Ground; return; // CAN, SUB abort a sequence
            case 0x1B: Enter(State.Escape); return;
            case 0x90: Enter(State.DcsEntry); return;
            case 0x9B: Enter(State.CsiEntry); return;
            case 0x9D: EnterString(State.OscString); return;
            case 0x98 or 0x9E or 0x9F: EnterString(State.SosPmApcString); return;
            case 0x9C: _state = State.Ground; return;
            case >= 0x80 and <= 0x9F: _state = State.Ground; _h.Execute(c); return;
        }

        switch (_state)
        {
            case State.Ground:
                if (c < 0x20) _h.Execute(c);
                else if (c != 0x7F) _h.Print(c);
                return;

            case State.Escape:
                if (c < 0x20) { _h.Execute(c); return; }
                if (c <= 0x2F) { Collect(c); _state = State.EscapeIntermediate; return; }
                switch (c)
                {
                    case '[': Enter(State.CsiEntry); return;
                    case ']': EnterString(State.OscString); return;
                    case 'P': Enter(State.DcsEntry); return;
                    case 'X' or '^' or '_': EnterString(State.SosPmApcString); return;
                    case 0x7F: return;
                }
                if (c <= 0x7E) EscDispatch((char)c);
                else _state = State.Ground; // a non-ASCII character cannot end an escape sequence
                return;

            case State.EscapeIntermediate:
                if (c < 0x20) { _h.Execute(c); return; }
                if (c <= 0x2F) { Collect(c); return; }
                if (c <= 0x7E) EscDispatch((char)c);
                else if (c != 0x7F) _state = State.Ground;
                return;

            case State.CsiEntry:
                if (c < 0x20) { _h.Execute(c); return; }
                if (c <= 0x2F) { Collect(c); _state = State.CsiIntermediate; return; }
                if (c <= 0x3B) { Param(c); _state = State.CsiParam; return; }
                if (c <= 0x3F) { _prefix = (char)c; _state = State.CsiParam; return; }
                if (c <= 0x7E) { CsiDispatch((char)c); return; }
                if (c != 0x7F) _state = State.CsiIgnore;
                return;

            case State.CsiParam:
                if (c < 0x20) { _h.Execute(c); return; }
                if (c is >= 0x30 and <= 0x3B) { Param(c); return; }
                if (c <= 0x2F) { Collect(c); _state = State.CsiIntermediate; return; }
                if (c <= 0x3F) { _state = State.CsiIgnore; return; }
                if (c <= 0x7E) { CsiDispatch((char)c); return; }
                if (c != 0x7F) _state = State.CsiIgnore;
                return;

            case State.CsiIntermediate:
                if (c < 0x20) { _h.Execute(c); return; }
                if (c <= 0x2F) { Collect(c); return; }
                if (c <= 0x3F) { _state = State.CsiIgnore; return; }
                if (c <= 0x7E) { CsiDispatch((char)c); return; }
                if (c != 0x7F) _state = State.CsiIgnore;
                return;

            case State.CsiIgnore:
                if (c < 0x20) { _h.Execute(c); return; }
                if (c is >= 0x40 and <= 0x7E) _state = State.Ground;
                return;

            case State.DcsEntry:
                if (c < 0x20) return;
                if (c <= 0x2F) { Collect(c); _state = State.DcsIntermediate; return; }
                if (c is >= 0x30 and <= 0x39 or 0x3B) { Param(c); _state = State.DcsParam; return; }
                if (c == 0x3A) { EnterString(State.DcsIgnore); return; }
                if (c <= 0x3F) { _prefix = (char)c; _state = State.DcsParam; return; }
                if (c <= 0x7E) { Hook((char)c); return; }
                if (c != 0x7F) EnterString(State.DcsIgnore);
                return;

            case State.DcsParam:
                if (c < 0x20) return;
                if (c is >= 0x30 and <= 0x39 or 0x3B) { Param(c); return; }
                if (c is 0x3A or (>= 0x3C and <= 0x3F)) { EnterString(State.DcsIgnore); return; }
                if (c <= 0x2F) { Collect(c); _state = State.DcsIntermediate; return; }
                if (c <= 0x7E) { Hook((char)c); return; }
                if (c != 0x7F) EnterString(State.DcsIgnore);
                return;

            case State.DcsIntermediate:
                if (c < 0x20) return;
                if (c <= 0x2F) { Collect(c); return; }
                if (c <= 0x3F) { EnterString(State.DcsIgnore); return; }
                if (c <= 0x7E) { Hook((char)c); return; }
                if (c != 0x7F) EnterString(State.DcsIgnore);
                return;
        }
    }

    private void Enter(State state)
    {
        _state = state;
        _prefix = '\0';
        _intermediates = 0;
        _intermediateCount = 0;
        _intermediateOverflow = false;
        _params.Reset();
    }

    private void EnterString(State state)
    {
        if (state is State.OscString or State.SosPmApcString)
        {
            _prefix = '\0';
            _intermediates = 0;
            _intermediateCount = 0;
            _params.Reset();
        }
        _state = state;
        ClearString();
    }

    private void Collect(int c)
    {
        if (_intermediateCount >= MaxIntermediates) { _intermediateOverflow = true; return; }
        _intermediates |= c << (8 * _intermediateCount);
        _intermediateCount++;
    }

    private void Param(int c)
    {
        if (c == ';') _params.Separator(sub: false);
        else if (c == ':') _params.Separator(sub: true);
        else _params.Digit(c - '0');
    }

    private void EscDispatch(char final)
    {
        _state = State.Ground;
        if (_intermediateOverflow) return;
        _h.EscDispatch(_intermediates, final);
    }

    private void CsiDispatch(char final)
    {
        _state = State.Ground;
        if (_intermediateOverflow) return;
        _h.CsiDispatch(_prefix, _params, _intermediates, final);
    }

    private void Hook(char final)
    {
        _dcsFinal = final;
        _state = State.DcsPassthrough;
        ClearString();
    }

    // ------------------------------------------------------------------ strings (raw bytes)

    private void StringByte(byte b)
    {
        if (_stringC2)
        {
            // C2 9C is U+009C (ST) in UTF-8: no printable character is encoded that way, so it ends the string as the C1 control does.
            _stringC2 = false;
            if (b == 0x9C) { EndString(); return; }
            AppendIfKept(0xC2);
        }
        if (b == 0xC2 && !_stringEsc) { _stringC2 = true; return; }
        if (_stringEsc)
        {
            _stringEsc = false;
            if (b == (byte)'\\') { EndString(); return; }
            // ESC followed by anything else aborts the string and starts an escape sequence with this byte.
            AbortString();
            Enter(State.Escape);
            Feed([b]);
            return;
        }
        switch (b)
        {
            case 0x1B: _stringEsc = true; return;
            case 0x18 or 0x1A: AbortString(); _state = State.Ground; return;
            case 0x07 when _state == State.OscString: EndString(); return; // xterm: BEL ends an OSC
        }
        if (b < 0x20 && _state == State.OscString) return; // other C0 inside an OSC are ignored
        AppendIfKept(b);
    }

    private void AppendIfKept(byte b)
    {
        if (_state is State.SosPmApcString or State.DcsIgnore) return;
        Append(b);
    }

    private void Append(byte b)
    {
        if (_stringOverflow) return;
        var limit = _state == State.OscString ? MaxOscBytes : MaxDcsBytes;
        if (_stringLength >= limit) { _stringOverflow = true; return; }
        if (_stringLength == _string.Length) Array.Resize(ref _string, Math.Min(limit, _string.Length * 2));
        _string[_stringLength++] = b;
    }

    private void EndString()
    {
        var state = _state;
        _state = State.Ground;
        if (!_stringOverflow)
        {
            if (state == State.OscString) _h.OscDispatch(_string.AsSpan(0, _stringLength));
            else if (state == State.DcsPassthrough && !_intermediateOverflow) _h.DcsDispatch(_prefix, _params, _intermediates, _dcsFinal, _string.AsSpan(0, _stringLength));
        }
        ClearString();
    }

    private void AbortString() => ClearString();

    private void ClearString()
    {
        _stringLength = 0;
        _stringOverflow = false;
        _stringEsc = false;
        _stringC2 = false;
        if (_string.Length > 64 * 1024) _string = new byte[256]; // a big OSC 52 or a hostile string: do not keep the buffer
    }
}
