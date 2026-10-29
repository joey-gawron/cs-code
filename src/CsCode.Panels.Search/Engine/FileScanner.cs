using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;
using CsCode.Core.Text;

namespace CsCode.Panels.Search.Engine;

internal enum ScanStatus { Ok, Binary, TooLarge, Unreadable, TimedOut, Cancelled }

/// <summary>One unit of worker input: a file on disk, or text that already lives in memory (an open document / preloaded bytes).</summary>
internal readonly record struct WorkItem(Uri Uri, string? Text = null, int? Version = null, byte[]? Bytes = null, int LineOffset = 0, int FirstLineColumnOffset = 0);

internal readonly record struct ScanResult(ScanStatus Status, List<SearchMatch>? Matches = null, string? Encoding = null, string? LineEnding = null);

/// <summary>
/// Turns one file into matches without reading it whole: 64 KB chunks are decoded incrementally, split into lines in a reusable buffer
/// and matched line by line (vectorised literal search / allocation-free regex enumeration). One instance per worker thread; its buffers
/// are rented once and returned by <see cref="Dispose"/>. Cancellation and the result cap are checked per line.
/// <para>Decisions: BOM decides UTF-8/UTF-16; a NUL byte in the first 8 KB means binary (skipped); no BOM means strict UTF-8 and, if the
/// bytes are not valid UTF-8, the whole file is rescanned as Latin-1; lines are terminated by \n, \r\n or \r.</para>
/// </summary>
internal sealed class FileScanner : IDisposable
{
    private const int SniffBytes = 8192;
    private const int LeadContext = 25; // characters of context kept before a match that starts late in its line (keeps it visible in a narrow pane)

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly SearchLimits _limits;
    private readonly TextMatcher _matcher;
    private readonly int _previewMax;
    private byte[] _bytes;
    private char[] _chars;
    private char[] _carry = ArrayPool<char>.Shared.Rent(1024);
    private char[]? _whole;
    private int _wholeLen;

    // per-file state
    private SearchSession _session = null!;
    private List<SearchMatch>? _matches;
    private int _reserved;
    private int _carryLen;
    private int _lineNo;
    private int _lineStartNo;      // first line number of this scan (selection scans start mid-document)
    private int _firstLineCol;     // column offset of the first line (selection scans start mid-line)
    private int _splitCol;         // column offset inside an over-long line that was split
    private bool _skipLf;
    private int _crlf, _lf, _cr;

    public FileScanner(SearchLimits limits, TextMatcher matcher)
    {
        _limits = limits;
        _matcher = matcher;
        _previewMax = Math.Clamp(limits.MaxPreviewChars, 40, 480);
        _bytes = ArrayPool<byte>.Shared.Rent(Math.Max(4096, limits.ReadChunkBytes));
        _chars = ArrayPool<char>.Shared.Rent(Math.Max(4096, limits.ReadChunkBytes) + 8);
    }

    public ScanResult Scan(in WorkItem item, SearchSession session)
    {
        _session = session;
        ResetFile(item.LineOffset, item.FirstLineColumnOffset);
        try
        {
            if (item.Text is { } text) return ScanText(text);
            if (item.Bytes is { } bytes)
            {
                if (bytes.Length > _limits.MaxFileSizeBytes) return new ScanResult(ScanStatus.TooLarge);
                using var ms = new MemoryStream(bytes, writable: false);
                return ScanStream(ms);
            }
            return ScanPath(item.Uri.LocalPath);
        }
        catch (RegexMatchTimeoutException)
        {
            Abandon();
            return new ScanResult(ScanStatus.TimedOut);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException or NotSupportedException)
        {
            Abandon();
            return new ScanResult(ScanStatus.Unreadable);
        }
    }

    // ----------------------------------------------------------------------------------------------------- sources

    private ScanResult ScanPath(string path)
    {
        // This runs on a dedicated search worker thread (never the UI thread, never the shared pool), which is what the banned-API rule
        // for sync file I/O is about; FileStream is used directly because IFileSystem has no streaming read.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        if (fs.CanSeek && fs.Length > _limits.MaxFileSizeBytes) return new ScanResult(ScanStatus.TooLarge);
        return ScanStream(fs);
    }

    private ScanResult ScanText(string text)
    {
        if (text.Length > _limits.MaxFileSizeBytes) return new ScanResult(ScanStatus.TooLarge);
        if (_matcher.RequiresWholeText) ScanWhole(TextMatcher.NormaliseBreaks(text).AsSpan());
        else
        {
            FeedLines(text.AsSpan());
            FinishLines();
        }
        return Finish(null);
    }

    private ScanResult ScanStream(Stream stream)
    {
        var first = ReadChunk(stream, 0);
        if (first == 0) return new ScanResult(ScanStatus.Ok);

        // BOM or no BOM.
        Encoding encoding;
        string name;
        var skip = 0;
        var bom = false;
        if (first >= 3 && _bytes[0] == 0xEF && _bytes[1] == 0xBB && _bytes[2] == 0xBF) { encoding = StrictUtf8; name = "utf-8-bom"; skip = 3; bom = true; }
        else if (first >= 2 && _bytes[0] == 0xFF && _bytes[1] == 0xFE) { encoding = Encoding.Unicode; name = "utf-16le"; skip = 2; bom = true; }
        else if (first >= 2 && _bytes[0] == 0xFE && _bytes[1] == 0xFF) { encoding = Encoding.BigEndianUnicode; name = "utf-16be"; skip = 2; bom = true; }
        else
        {
            if (_bytes.AsSpan(0, Math.Min(first, SniffBytes)).IndexOf((byte)0) >= 0) return new ScanResult(ScanStatus.Binary);
            encoding = StrictUtf8;
            name = "utf-8";
        }

        var status = Decode(stream, encoding, first, skip);
        if (status == DecodeStatus.InvalidBytes && !bom && stream.CanSeek)
        {
            // Not UTF-8: rescan the whole file as Latin-1 (every byte sequence is valid there). Nothing was emitted yet.
            Abandon();
            ResetFile(_lineStartNo, _firstLineCol);
            stream.Position = 0;
            first = ReadChunk(stream, 0);
            status = Decode(stream, Encoding.Latin1, first, 0);
            name = "latin1";
        }
        return status switch
        {
            DecodeStatus.Ok => Finish(name),
            DecodeStatus.Cancelled => new ScanResult(ScanStatus.Cancelled, _matches, name, DominantEol()),
            _ => Unreadable(),
        };
    }

    private ScanResult Unreadable()
    {
        Abandon();
        return new ScanResult(ScanStatus.Unreadable);
    }

    private enum DecodeStatus { Ok, InvalidBytes, Cancelled }

    private DecodeStatus Decode(Stream stream, Encoding encoding, int firstLength, int skip)
    {
        var decoder = encoding.GetDecoder();
        var wholeText = _matcher.RequiresWholeText;
        EnsureChars(encoding.GetMaxCharCount(_bytes.Length));
        var length = firstLength;
        var offset = skip;
        while (true)
        {
            var eof = length == 0;
            int n;
            try { n = decoder.GetChars(_bytes.AsSpan(offset, length - offset), _chars, eof); }
            catch (DecoderFallbackException) { return DecodeStatus.InvalidBytes; }

            if (n > 0)
            {
                if (wholeText) AppendWhole(_chars.AsSpan(0, n));
                else if (!FeedLines(_chars.AsSpan(0, n))) return _session.Token.IsCancellationRequested ? DecodeStatus.Cancelled : DecodeStatus.Ok;
            }
            if (eof) break;
            if (_session.Token.IsCancellationRequested) return DecodeStatus.Cancelled;
            length = ReadChunk(stream, 0);
            offset = 0;
        }
        if (wholeText)
        {
            var text = TextMatcher.NormaliseBreaks(new string(_whole!, 0, _wholeLen));
            CountBreaks(_whole.AsSpan(0, _wholeLen));
            ScanWhole(text.AsSpan());
        }
        else FinishLines();
        return _session.Token.IsCancellationRequested ? DecodeStatus.Cancelled : DecodeStatus.Ok;
    }

    private int ReadChunk(Stream stream, int offset)
    {
        var total = 0;
        var want = Math.Min(_limits.ReadChunkBytes, _bytes.Length) - offset;
        while (total < want)
        {
            var r = stream.Read(_bytes, offset + total, want - total);
            if (r == 0) break;
            total += r;
        }
        return offset + total;
    }

    // ----------------------------------------------------------------------------------------------- line mode

    /// <summary>Splits decoded text into lines (carrying a partial last line over to the next call) and matches each. False = stop (cancelled or capped).</summary>
    private bool FeedLines(ReadOnlySpan<char> data)
    {
        var pos = 0;
        if (_skipLf)
        {
            _skipLf = false;
            if (data.Length > 0 && data[0] == '\n') { pos = 1; _crlf++; }
            else _cr++;
        }
        while (pos < data.Length)
        {
            var rest = data[pos..];
            var idx = rest.IndexOfAny('\n', '\r');
            if (idx < 0)
            {
                if (!AppendCarry(rest)) return false;
                break;
            }
            ReadOnlySpan<char> line;
            if (_carryLen > 0)
            {
                if (!AppendCarry(rest[..idx])) return false;
                line = _carry.AsSpan(0, _carryLen);
            }
            else line = rest[..idx];

            var term = rest[idx];
            if (!MatchLine(line)) return false;
            _carryLen = 0;
            _splitCol = 0;
            _lineNo++;
            pos += idx + 1;
            if (term == '\r')
            {
                if (pos < data.Length)
                {
                    if (data[pos] == '\n') { pos++; _crlf++; }
                    else _cr++;
                }
                else _skipLf = true; // the \n of a \r\n may open the next chunk
            }
            else _lf++;
        }
        return !_session.Token.IsCancellationRequested;
    }

    private void FinishLines()
    {
        if (_skipLf) { _skipLf = false; _cr++; }
        if (_carryLen > 0)
        {
            MatchLine(_carry.AsSpan(0, _carryLen));
            _carryLen = 0;
        }
    }

    /// <summary>Appends a partial line; an over-long line is processed in pieces (matches straddling a split are missed).</summary>
    private bool AppendCarry(ReadOnlySpan<char> part)
    {
        while (_carryLen + part.Length > _limits.MaxLineChars)
        {
            var take = _limits.MaxLineChars - _carryLen;
            Grow(take);
            part[..take].CopyTo(_carry.AsSpan(_carryLen));
            _carryLen += take;
            part = part[take..];
            if (!MatchLine(_carry.AsSpan(0, _carryLen))) return false;
            _splitCol += _carryLen;
            _carryLen = 0;
        }
        Grow(part.Length);
        part.CopyTo(_carry.AsSpan(_carryLen));
        _carryLen += part.Length;
        return true;
    }

    private void Grow(int extra)
    {
        if (_carryLen + extra <= _carry.Length) return;
        var bigger = ArrayPool<char>.Shared.Rent(Math.Max(_carry.Length * 2, _carryLen + extra));
        _carry.AsSpan(0, _carryLen).CopyTo(bigger);
        ArrayPool<char>.Shared.Return(_carry);
        _carry = bigger;
    }

    private bool MatchLine(ReadOnlySpan<char> line)
    {
        if (_session.Token.IsCancellationRequested) return false;
        var from = 0;
        var colBase = _splitCol + (_lineNo == _lineStartNo ? _firstLineCol : 0);
        int idx;
        while ((idx = _matcher.Find(line, from, out var len)) >= 0)
        {
            if (!_session.TryReserve()) return false;
            _reserved++;
            var range = new TextRange(new TextPosition(_lineNo, colBase + idx), new TextPosition(_lineNo, colBase + idx + len));
            (_matches ??= new List<SearchMatch>()).Add(MakeMatch(line, idx, Math.Min(len, line.Length - idx), range, line.Slice(idx, len)));
            from = idx + len;
        }
        return true;
    }

    // ------------------------------------------------------------------------------------------ whole-text mode

    private void AppendWhole(ReadOnlySpan<char> part)
    {
        _whole ??= ArrayPool<char>.Shared.Rent(Math.Max(part.Length * 2, 64 * 1024));
        if (_wholeLen + part.Length > _whole.Length)
        {
            var bigger = ArrayPool<char>.Shared.Rent(Math.Max(_whole.Length * 2, _wholeLen + part.Length));
            _whole.AsSpan(0, _wholeLen).CopyTo(bigger);
            ArrayPool<char>.Shared.Return(_whole);
            _whole = bigger;
        }
        part.CopyTo(_whole.AsSpan(_wholeLen));
        _wholeLen += part.Length;
    }

    private void CountBreaks(ReadOnlySpan<char> text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') _lf++;
            else if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') { _crlf++; i++; }
                else _cr++;
            }
        }
    }

    /// <summary>Matches over the whole EOL-normalised text; line/column are tracked incrementally so the pass stays linear.</summary>
    private void ScanWhole(ReadOnlySpan<char> text)
    {
        var from = 0;
        var line = _lineStartNo;
        var lineStart = 0;
        var scanned = 0; // text[..scanned] is already folded into (line, lineStart)
        int idx;
        while ((idx = _matcher.Find(text, from, out var len)) >= 0)
        {
            if (_session.Token.IsCancellationRequested) return;
            if (!_session.TryReserve()) return;
            _reserved++;

            Advance(text, ref scanned, idx, ref line, ref lineStart);
            var startCol = idx - lineStart + (line == _lineStartNo ? _firstLineCol : 0);
            var lineEndIdx = text[idx..].IndexOf('\n');
            var lineEnd = lineEndIdx < 0 ? text.Length : idx + lineEndIdx;
            var lineSpan = text[lineStart..lineEnd];

            var endLine = line;
            var endCol = startCol + len;
            var inside = text.Slice(idx, len);
            var lastBreak = inside.LastIndexOf('\n');
            if (lastBreak >= 0)
            {
                endLine += inside.Count('\n');
                endCol = len - lastBreak - 1;
            }
            var range = new TextRange(new TextPosition(line, startCol), new TextPosition(endLine, endCol));
            var inLine = Math.Min(len, lineSpan.Length - (idx - lineStart));
            (_matches ??= new List<SearchMatch>()).Add(MakeMatch(lineSpan, idx - lineStart, inLine, range, inside));
            from = idx + len;
        }
    }

    private static void Advance(ReadOnlySpan<char> text, ref int scanned, int upTo, ref int line, ref int lineStart)
    {
        while (scanned < upTo)
        {
            var nl = text[scanned..upTo].IndexOf('\n');
            if (nl < 0) { scanned = upTo; return; }
            scanned += nl + 1;
            line++;
            lineStart = scanned;
        }
    }

    // ------------------------------------------------------------------------------------------------ results

    private SearchMatch MakeMatch(ReadOnlySpan<char> line, int idx, int lenInLine, TextRange range, ReadOnlySpan<char> matchedText)
    {
        var matchEnd = idx + lenInLine;
        var lead = 0;
        while (lead < idx && char.IsWhiteSpace(line[lead])) lead++; // never trim into the match
        int start = lead, end = line.Length;
        bool clipLeft = false, clipRight = false;
        if (line.Length > _limits.LongLineThreshold || end - start > _previewMax || idx - lead > LeadContext)
        {
            start = Math.Max(lead, idx - LeadContext);
            clipLeft = start > lead;
            end = Math.Min(line.Length, start + _previewMax);
            clipRight = end < line.Length;
        }
        else
        {
            while (end > matchEnd && end > start && char.IsWhiteSpace(line[end - 1])) end--;
        }
        var clippedEnd = Math.Min(matchEnd, end);

        Span<char> buf = stackalloc char[_previewMax + 2];
        var n = 0;
        if (clipLeft) buf[n++] = '…';
        for (var i = start; i < end; i++) buf[n++] = line[i] < ' ' ? ' ' : line[i]; // tabs/controls become spaces (offsets unchanged)
        if (clipRight) buf[n++] = '…';

        var previewStart = (clipLeft ? 1 : 0) + (idx - start);
        var previewLen = Math.Max(0, clippedEnd - idx);
        var matched = matchedText.Length > 256 ? new string(matchedText[..256]) : new string(matchedText);
        return new SearchMatch(range, new string(buf[..n]), previewStart, previewLen, matched);
    }

    private ScanResult Finish(string? encoding)
    {
        if (_session.Token.IsCancellationRequested && _matches is null) return new ScanResult(ScanStatus.Cancelled, null, encoding, DominantEol());
        return new ScanResult(ScanStatus.Ok, _matches, encoding, DominantEol());
    }

    private string? DominantEol()
    {
        if (_crlf == 0 && _lf == 0 && _cr == 0) return null;
        if (_crlf >= _lf && _crlf >= _cr) return "\r\n";
        return _lf >= _cr ? "\n" : "\r";
    }

    /// <summary>Gives back the global-cap reservations of matches that are being thrown away.</summary>
    private void Abandon()
    {
        if (_reserved > 0) _session.Release(_reserved);
        _reserved = 0;
        _matches = null;
    }

    private void ResetFile(int lineOffset, int firstLineColumnOffset)
    {
        _matches = null;
        _reserved = 0;
        _carryLen = 0;
        _lineNo = lineOffset;
        _lineStartNo = lineOffset;
        _firstLineCol = firstLineColumnOffset;
        _splitCol = 0;
        _skipLf = false;
        _crlf = _lf = _cr = 0;
        _wholeLen = 0;
    }

    private void EnsureChars(int needed)
    {
        if (_chars.Length >= needed) return;
        ArrayPool<char>.Shared.Return(_chars);
        _chars = ArrayPool<char>.Shared.Rent(needed);
    }

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_bytes);
        ArrayPool<char>.Shared.Return(_chars);
        ArrayPool<char>.Shared.Return(_carry);
        if (_whole is not null) ArrayPool<char>.Shared.Return(_whole);
        _bytes = [];
        _chars = [];
        _carry = [];
        _whole = null;
    }
}
