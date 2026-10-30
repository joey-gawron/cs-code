using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using CsCode.App.Docking;
using CsCode.App.Macros;
using CsCode.App.Notifications;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.View;
using CsCode.Macros;

namespace CsCode.App.Tests.RealApp.Macros;

/// <summary>
/// The REAL application (<see cref="RealAppHost"/>: every module, the real editor, the macro module and its shell integration) with a file open and
/// its editor focused. Input is real key events into the window (a key press, plus text input for a character, as a keyboard produces them), so
/// recording goes through the window's handlers, the Vim adapter, the keyboard pipeline's tap and the editor exactly as for a user. Vim emulation
/// is off unless asked for (it is on by default in the product).
/// </summary>
public sealed class MacroApp : IAsyncDisposable
{
    public const string Quiet = "\"editor.quickSuggestions\": false, \"editor.suggestOnTriggerCharacters\": false";

    private MacroApp(RealAppHost host) => Host = host;

    public RealAppHost Host { get; }
    /// <summary>The window keys and text are raised on: the main window, or a floating window the editor was moved into.</summary>
    public Avalonia.Controls.TopLevel Input { get => _input ?? Host.Window; set => _input = value; }
    private Avalonia.Controls.TopLevel? _input;
    public MacroIntegration Integration => Host.Shell.Macros ?? throw new InvalidOperationException("the composition has no macro integration");
    public MacroService Service => Integration.Service;
    public DocumentWell Well => Host.Shell.Well;
    public EditorTab Tab => Well.ActiveEditor ?? throw new InvalidOperationException("no active editor");
    public CodeEditorControl View => (CodeEditorControl)(Tab.LiveView ?? throw new InvalidOperationException("the active tab has no live view"));
    public string Text => View.Document.GetText().Replace("\r\n", "\n");
    public TextPosition Caret => View.Modal.Controller.Caret;

    /// <summary>Starts the app with <paramref name="text"/> in <paramref name="fileName"/> (opened from the command line), its editor focused.</summary>
    public static async Task<MacroApp> StartAsync(string text, string fileName = "test.txt", bool vim = false, string? settings = null, Action<RealAppHost>? seed = null)
    {
        var host = RealAppHost.Create(h =>
        {
            h.Files.WriteFile(fileName, text);
            var path = h.Home.Combine("config", "settings.json");
            var current = File.ReadAllText(path).TrimEnd();
            var extra = string.Join(",\n", new[] { Quiet, vim ? null : "\"vim.enable\": false", settings }.Where(s => s is not null));
            File.WriteAllText(path, current[..^1].TrimEnd() + ",\n" + extra + "\n}\n");
            seed?.Invoke(h);
        });
        await host.StartAsync([host.Files.Combine(fileName)]);
        var app = new MacroApp(host);
        await app.FocusAsync();
        return app;
    }

    public async Task FocusAsync()
    {
        if (!await RealAppHost.WaitFor(() => Well.ActiveEditor?.LiveView is CodeEditorControl { IsVisible: true }))
            throw new TimeoutException("the editor did not appear");
        View.Modal.Controller.Focus();
        if (!await RealAppHost.WaitFor(() => View.Modal.Controller.IsFocused)) throw new TimeoutException("the editor did not take focus");
        await DevScene.SettleAsync(3);
    }

    public void SetCaret(int line, int column)
    {
        View.Modal.Controller.SetSelection(TextRange.At(line, column), reveal: true);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Types characters as a keyboard does (key press with the character, text input, key release).</summary>
    public void Type(string text)
    {
        foreach (var c in text)
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
            if (key is { } k) Input.KeyPress(k, mods, PhysicalKey.None, c.ToString());
            Input.KeyTextInput(c.ToString());
            if (key is { } k2) Input.KeyRelease(k2, mods, PhysicalKey.None, c.ToString());
            Dispatcher.UIThread.RunJobs();
        }
    }

    public void Press(Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        Input.KeyPress(key, modifiers, PhysicalKey.None, null);
        Input.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Ctrl+M, Ctrl+R: the default key of Record / Stop.</summary>
    public void RecordKey()
    {
        Press(Key.M, RawInputModifiers.Control);
        Press(Key.R, RawInputModifiers.Control);
    }

    public Task SettleAsync(int rounds = 5) => DevScene.SettleAsync(rounds);

    public string? StatusText(string id) => Host.Get<IStatusBarService>().Items.FirstOrDefault(i => i.Id == id)?.Text;

    public IEnumerable<string> Notifications => Host.Get<NotificationCenter>().History.Select(n => n.Message);

    public static RawInputModifiers Ctrl => RawInputModifiers.Control;

    public ValueTask DisposeAsync() => Host.DisposeAsync();
}
