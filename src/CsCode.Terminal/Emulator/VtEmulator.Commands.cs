using System.Text;

namespace CsCode.Terminal.Emulator;

/// <summary>
/// A command the shell reported through shell integration (OSC 633/133): its command line, where its output starts and ends (absolute rows
/// and columns of the primary screen, as when it finished; a later width change re-flows the rows and can move them) and its exit code.
/// </summary>
/// <param name="CommandLine">The line as the shell reported it (OSC 633 E with the terminal's nonce), else as read from the screen between the prompt's end and the output.</param>
/// <param name="ExitCode">Null when the shell did not say.</param>
public sealed record TerminalCommand(string? CommandLine, int? ExitCode, long PromptLine, long OutputStartLine, int OutputStartColumn, long OutputEndLine, int OutputEndColumn);

public sealed partial class VtEmulator
{
    /// <summary>The longest command line kept (a hostile E sequence cannot make the terminal hold more).</summary>
    public const int MaxCommandLineLength = 8 * 1024;

    // positions of the command being run (absolute primary-screen rows), from the A/B/C marks of the shell integration
    private long _promptLine = -1;
    private (long Line, int Column)? _inputStart;
    private (long Line, int Column)? _outputStart;
    private string? _reportedCommandLine;
    private string? _commandLine;

    /// <summary>
    /// The nonce CsCode gave the shell (CSCODE_NONCE): a command line reported with it (OSC 633 E) is the shell's own; any other E (a program
    /// printing one) is ignored and the line is read from the screen instead.
    /// </summary>
    public string? ShellIntegrationNonce { get; set; }

    /// <summary>The shell reported at least one shell-integration mark (VS Code's "shell integration activated").</summary>
    public bool HasShellIntegration { get; private set; }

    /// <summary>The last command that finished (OSC 633/133 D), or null.</summary>
    public TerminalCommand? LastCommand { get; private set; }

    /// <summary>Shell integration: the last command finished (after <see cref="LastCommand"/> was set; worker thread).</summary>
    public Action<TerminalCommand>? CommandRecorded { get; set; }

    private (long Line, int Column) AbsoluteCursor => (ScreenTop + _screen.CursorY, _screen.PendingWrap ? Columns : _screen.CursorX);

    private void OnPromptStart()
    {
        if (IsAlternateScreen) return;
        _promptLine = ScreenTop + _screen.CursorY;
        _inputStart = null;
        _outputStart = null;
        _reportedCommandLine = null;
        _commandLine = null;
    }

    private void OnCommandStart()
    {
        if (!IsAlternateScreen) _inputStart = AbsoluteCursor;
    }

    private void OnCommandLine(string data)
    {
        // "E;<escaped command line>;<nonce>": the line escapes ';', so the nonce is what follows the last one
        var rest = data.Length > 2 ? data[2..] : "";
        var semi = rest.LastIndexOf(';');
        var line = semi < 0 ? rest : rest[..semi];
        var nonce = semi < 0 ? null : rest[(semi + 1)..];
        if (ShellIntegrationNonce is not { Length: > 0 } expected || nonce != expected) return;
        var text = UnescapeShellIntegrationValue(line);
        _reportedCommandLine = text.Length > MaxCommandLineLength ? text[..MaxCommandLineLength] : text;
    }

    private void OnCommandExecuted()
    {
        if (IsAlternateScreen) return;
        _outputStart = AbsoluteCursor;
        _commandLine = _reportedCommandLine ?? CommandLineFromScreen();
    }

    private void OnCommandFinished(int? exitCode)
    {
        if (IsAlternateScreen || _outputStart is not { } start) return;
        var (endLine, endColumn) = AbsoluteCursor;
        LastCommand = new TerminalCommand(_commandLine, exitCode, _promptLine, start.Line, start.Column, endLine, endColumn);
        _outputStart = null;
        _inputStart = null;
        CommandRecorded?.Invoke(LastCommand);
    }

    /// <summary>Without a reported line: the text typed after the prompt (B) up to where the output starts (C), soft wraps joined.</summary>
    private string? CommandLineFromScreen()
    {
        if (_inputStart is not { } from || _outputStart is not { } to) return null;
        var sb = new StringBuilder();
        AppendRange(sb, from.Line, from.Column, to.Line, to.Column, MaxCommandLineLength);
        var text = sb.ToString().Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// The output of <paramref name="command"/> (default: <see cref="LastCommand"/>) as text: rows joined with '\n' (soft-wrapped rows without
    /// one), trailing blanks trimmed, at most <paramref name="maxChars"/> characters (the end is kept). Rows that fell out of the scrollback
    /// are gone. Worker only.
    /// </summary>
    public string? CommandOutput(TerminalCommand? command = null, int maxChars = 1024 * 1024, int maxRows = int.MaxValue)
    {
        command ??= LastCommand;
        if (command is null || IsAlternateScreen) return null;
        var sb = new StringBuilder();
        var (startLine, startColumn) = command.OutputEndLine - command.OutputStartLine >= maxRows
            ? (command.OutputEndLine - maxRows + 1, 0) // only the tail is wanted: never read a huge output whole
            : (command.OutputStartLine, command.OutputStartColumn);
        AppendRange(sb, startLine, startColumn, command.OutputEndLine, command.OutputEndColumn, int.MaxValue);
        var text = sb.ToString().TrimEnd('\n', ' ', '\r');
        return text.Length > maxChars ? text[^maxChars..] : text;
    }

    /// <summary>Text from (line, column) up to, not including, (endLine, endColumn) of the primary screen and its scrollback.</summary>
    private void AppendRange(StringBuilder sb, long line, int column, long endLine, int endColumn, int maxChars)
    {
        var first = Math.Max(line, ScrollbackTop);
        var last = Math.Min(endLine, ScreenTop + Rows - 1);
        var lineStart = sb.Length;
        for (var l = first; l <= last && sb.Length < maxChars; l++)
        {
            if (GetRow(l) is not { } row) continue;
            if (l > first && !row.IsWrapped)
            {
                // a hard line ends (a soft-wrapped row continues on the next one: its blanks are part of the text)
                TrimTrailingBlanks(sb, lineStart);
                sb.Append('\n');
                lineStart = sb.Length;
            }
            var start = l == line ? column : 0;
            var end = l == endLine ? endColumn : int.MaxValue;
            if (end > start) row.AppendText(sb, start, end);
        }
        TrimTrailingBlanks(sb, lineStart);
    }

    private static void TrimTrailingBlanks(StringBuilder sb, int from)
    {
        var length = sb.Length;
        while (length > from && sb[length - 1] == ' ') length--;
        sb.Length = length;
    }

    /// <summary>OSC 633 values escape '\' as "\\" and any byte as "\xHH" (';' and control characters).</summary>
    public static string UnescapeShellIntegrationValue(string value)
    {
        if (value.IndexOf('\\', StringComparison.Ordinal) < 0) return value;
        var bytes = new List<byte>(value.Length);
        var utf8 = new byte[4];
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\\' && i + 1 < value.Length)
            {
                if (value[i + 1] == '\\') { bytes.Add((byte)'\\'); i++; continue; }
                if (value[i + 1] == 'x' && i + 3 < value.Length && byte.TryParse(value.AsSpan(i + 2, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
                {
                    bytes.Add(b);
                    i += 3;
                    continue;
                }
            }
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                var n = Encoding.UTF8.GetBytes(value.AsSpan(i, 2), utf8);
                for (var k = 0; k < n; k++) bytes.Add(utf8[k]);
                i++;
                continue;
            }
            var count = Encoding.UTF8.GetBytes(value.AsSpan(i, 1), utf8);
            for (var k = 0; k < count; k++) bytes.Add(utf8[k]);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}
