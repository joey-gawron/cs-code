using System.Text;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using CsCode.App.Docking;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.App.Vim;
using CsCode.Core.Text;
using CsCode.Editor.View;
using CsCode.Vim;

namespace CsCode.App.Tests.RealApp.Vim;

/// <summary>
/// The REAL application (<see cref="RealAppHost"/>: every module, the real editor, the real Vim module and adapter) with a file open and its editor
/// focused; <see cref="Keys"/> types Vim notation as real key events into the window (key presses for special and Ctrl keys, key press + text
/// input for characters, as a keyboard produces them), so everything goes through the window's handlers, the adapter's key policy and the
/// engine exactly as for a user.
/// </summary>
public sealed class VimApp : IAsyncDisposable
{
    private VimApp(RealAppHost host) => Host = host;

    public RealAppHost Host { get; }
    public VimIntegration Vim => Host.Shell.Vim ?? throw new InvalidOperationException("the composition has no Vim adapter");
    public DocumentWell Well => Host.Shell.Well;
    public EditorTab Tab => Well.ActiveEditor ?? throw new InvalidOperationException("no active editor");
    public CodeEditorControl View => (CodeEditorControl)(Tab.LiveView ?? throw new InvalidOperationException("the active tab has no live view"));
    public VimEngine Engine => Vim.EngineOf(View) ?? throw new InvalidOperationException("the view has no engine");
    public string Text => View.Document.GetText().Replace("\r\n", "\n");
    public TextPosition Caret => View.Modal.Controller.Caret;
    public string? ModeText => Vim.StatusText(VimIntegration.ModeItemId);
    public string? CommandLineText => Vim.StatusText(VimIntegration.CommandLineItemId);
    public string? ShowCommandText => Vim.StatusText(VimIntegration.ShowCommandItemId);

    /// <summary>
    /// Starts the app with <paramref name="text"/> in <paramref name="fileName"/>, opened from the command line, its editor focused;
    /// <paramref name="configure"/> may replace services (it runs after the host's own test registrations).
    /// </summary>
    public static async Task<VimApp> StartAsync(string text, string fileName = "test.txt", string? settings = null, Action<RealAppHost>? seed = null,
        Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? configure = null)
    {
        var host = RealAppHost.Create(h =>
        {
            h.Files.WriteFile(fileName, text);
            if (settings is not null)
            {
                var path = h.Home.Combine("config/settings.json");
                var current = File.ReadAllText(path).TrimEnd();
                File.WriteAllText(path, current[..^1].TrimEnd() + ",\n" + settings + "\n}\n");
            }
            seed?.Invoke(h);
        });
        await host.StartAsync([host.Files.Combine(fileName)], configure);
        var app = new VimApp(host);
        await app.FocusAsync();
        return app;
    }

    /// <summary>Waits for the active editor's view and engine and gives it keyboard focus.</summary>
    public async Task FocusAsync(int settleRounds = 3)
    {
        if (!await RealAppHost.WaitFor(() => Well.ActiveEditor?.LiveView is CodeEditorControl c && Vim.EngineOf(c) is not null && c.IsVisible))
            throw new TimeoutException("the editor (with its Vim engine) did not appear");
        View.Modal.Controller.Focus();
        if (!await RealAppHost.WaitFor(() => View.Modal.Controller.IsFocused)) throw new TimeoutException("the editor did not take focus");
        await DevScene.SettleAsync(settleRounds);
    }

    /// <summary>Opens another file in a new tab and focuses its editor.</summary>
    public async Task<EditorTab> OpenAsync(string fileName, string text, int settleRounds = 3)
    {
        Host.Files.WriteFile(fileName, text);
        var tab = await Well.OpenAsync(new Uri(Host.Files.Combine(fileName)));
        await FocusAsync(settleRounds);
        return tab;
    }

    /// <summary>Puts the caret at <paramref name="line"/>:<paramref name="col"/> (0-based) as a mouse click would.</summary>
    public void SetCaret(int line, int col)
    {
        View.Modal.Controller.SetSelection(TextRange.At(line, col), reveal: true);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Types <paramref name="notation"/> (Vim key notation: <c>dw</c>, <c>&lt;Esc&gt;</c>, <c>&lt;C-v&gt;</c>, <c>&lt;CR&gt;</c>, <c>&lt;lt&gt;</c>...).</summary>
    public void Keys(string notation)
    {
        foreach (var token in Tokenize(notation))
        {
            if (token.Length == 1) Char(token[0]);
            else Special(token);
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Types <paramref name="notation"/> and lets posted work (status bar, sliced engine work) run.</summary>
    public async Task KeysAsync(string notation, int settleRounds = 3)
    {
        Keys(notation);
        await DevScene.SettleAsync(settleRounds);
    }

    /// <summary>
    /// One character as a keyboard sends it (key press, text input, key release) without running posted jobs or rendering afterwards: the time
    /// this takes is what the keystroke costs the UI thread.
    /// </summary>
    public void TypeCharOnly(char c) => Char(c);

    /// <summary>
    /// Raises a key press (and, for a character, its text input) directly on the focused element: the event goes through every handler of
    /// the window and the editor exactly as from a keyboard, and nothing else runs (the headless helpers also run queued jobs and render).
    /// Returns the milliseconds the handlers took.
    /// </summary>
    public double RaiseKey(Key key, KeyModifiers modifiers = KeyModifiers.None, string? text = null)
    {
        var target = Host.Window.FocusManager?.GetFocusedElement() as Avalonia.Interactivity.Interactive ?? throw new InvalidOperationException("nothing has focus");
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = target });
        if (text is not null) target.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = text, Source = target });
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key, KeyModifiers = modifiers, Source = target });
        return System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    /// <summary>An IME (or any text-input-only) commit: text input without key presses.</summary>
    public void Commit(string text)
    {
        Host.Window.KeyTextInput(text);
        Dispatcher.UIThread.RunJobs();
    }

    public void Press(Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        Host.Window.KeyPress(key, modifiers, PhysicalKey.None, null);
        Host.Window.KeyRelease(key, modifiers, PhysicalKey.None, null);
    }

    private void Char(char c)
    {
        Key? key = c switch
        {
            >= 'a' and <= 'z' => Key.A + (c - 'a'),
            >= 'A' and <= 'Z' => Key.A + (c - 'A'),
            >= '0' and <= '9' => Key.D0 + (c - '0'),
            ' ' => Key.Space,
            _ => null,
        };
        var mods = c is >= 'A' and <= 'Z' ? RawInputModifiers.Shift : RawInputModifiers.None;
        if (key is { } k) Host.Window.KeyPress(k, mods, PhysicalKey.None, c.ToString());
        Host.Window.KeyTextInput(c.ToString());
        if (key is { } k2) Host.Window.KeyRelease(k2, mods, PhysicalKey.None, c.ToString());
    }

    private void Special(string token)
    {
        var inner = token[1..^1];
        var mods = RawInputModifiers.None;
        while (inner.Length > 2 && inner[1] == '-')
        {
            mods |= char.ToUpperInvariant(inner[0]) switch
            {
                'C' => RawInputModifiers.Control,
                'S' => RawInputModifiers.Shift,
                'A' or 'M' => RawInputModifiers.Alt,
                'D' => RawInputModifiers.Meta, // Vim's Command key (macOS)
                _ => RawInputModifiers.None,
            };
            inner = inner[2..];
        }
        if (inner.Equals("lt", StringComparison.OrdinalIgnoreCase) && mods == RawInputModifiers.None) { Char('<'); return; }
        if (inner.Equals("space", StringComparison.OrdinalIgnoreCase) && mods == RawInputModifiers.None) { Char(' '); return; }
        if (inner.Equals("bar", StringComparison.OrdinalIgnoreCase)) { Char('|'); return; }
        if (inner.Equals("bslash", StringComparison.OrdinalIgnoreCase)) { Char('\\'); return; }
        var key = inner.ToLowerInvariant() switch
        {
            "esc" => Key.Escape, "cr" or "enter" or "return" => Key.Enter, "bs" => Key.Back, "tab" => Key.Tab, "del" => Key.Delete,
            "up" => Key.Up, "down" => Key.Down, "left" => Key.Left, "right" => Key.Right, "home" => Key.Home, "end" => Key.End,
            "pageup" => Key.PageUp, "pagedown" => Key.PageDown, "insert" => Key.Insert, "space" => Key.Space,
            "[" => Key.OemOpenBrackets, "]" => Key.OemCloseBrackets,
            var f when f.Length >= 2 && f[0] == 'f' && int.TryParse(f[1..], out var n) => Key.F1 + (n - 1),
            var s when s.Length == 1 && char.IsAsciiLetter(s[0]) => Key.A + (char.ToLowerInvariant(s[0]) - 'a'),
            var d when d.Length == 1 && char.IsAsciiDigit(d[0]) => Key.D0 + (d[0] - '0'),
            _ => throw new ArgumentException($"no key for {token}"),
        };
        Host.Window.KeyPress(key, mods, PhysicalKey.None, null);
        Host.Window.KeyRelease(key, mods, PhysicalKey.None, null);
    }

    /// <summary>Splits Vim notation into single characters and <c>&lt;...&gt;</c> tokens.</summary>
    public static IEnumerable<string> Tokenize(string notation)
    {
        for (var i = 0; i < notation.Length; i++)
        {
            if (notation[i] == '<')
            {
                var close = notation.IndexOf('>', i + 1);
                if (close > i + 1 && notation[(i + 1)..close] is var inner && !inner.Contains('<') && !inner.Contains(' '))
                {
                    yield return notation[i..(close + 1)];
                    i = close;
                    continue;
                }
            }
            yield return notation[i].ToString();
        }
    }

    /// <summary>Text with a '‸' at the caret, for assertion messages.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        var lines = Text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) sb.Append("\\n");
            var l = lines[i];
            sb.Append(i == Caret.Line ? l.Insert(Math.Min(Caret.Character, l.Length), "‸") : l);
        }
        return $"[{sb}] mode={Engine.ModeCode} status={ModeText}";
    }

    public ValueTask DisposeAsync() => Host.DisposeAsync();
}
