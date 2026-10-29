using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;

namespace CsCode.Vim;

/// <summary>
/// What the Editor/Shell adapter talks to: the shared session, the on/off switch and the engine factory. Resolving it from DI is what
/// activates the Vim module (registers the <c>vim.*</c> settings and the <c>vim.*</c> commands, loads the vimrc and the saved registers).
/// </summary>
public interface IVimService
{
    VimSession Session { get; }

    /// <summary>True while Vim emulation is on (setting <c>vim.enabled</c>, or the last <c>vim.toggle</c>).</summary>
    bool Enabled { get; }

    IEventSource<bool> EnabledChanged { get; }

    /// <summary>Switches emulation on or off for every engine of the session (not persisted; the setting decides at startup).</summary>
    void SetEnabled(bool enabled);

    /// <summary>Creates the engine for one editor view. The caller owns it and must dispose it when the view closes.</summary>
    VimEngine CreateEngine(IVimHost host);

    /// <summary>Completes when the vimrc has been read and executed (also when there is none).</summary>
    Task VimrcLoaded { get; }

    /// <summary>Errors of the last vimrc load; empty when it loaded cleanly or does not exist.</summary>
    IReadOnlyList<string> VimrcErrors { get; }

    Task ReloadVimrcAsync(CancellationToken ct = default);
}

public sealed class VimService : IVimService, IDisposable
{
    public const string ToggleCommand = "vim.toggle";
    public const string EnableCommand = "vim.enable";
    public const string DisableCommand = "vim.disable";
    public const string CheatSheetCommand = "vim.showCheatSheet";
    public const string CheatSheetChannel = "Vim Cheat Sheet";

    private readonly VimConfiguration _configuration;
    private readonly IOutputService? _output;
    private readonly DisposableStore _disposables = new();

    public VimService(VimSession session, IConfigurationService config, ICommandService commands, IStateStorage? state = null, IAppPaths? paths = null, IOutputService? output = null)
    {
        Session = session;
        _output = output;
        _configuration = _disposables.Add(new VimConfiguration(session, config, state, paths));
        _disposables.Add(commands.Register(ToggleCommand, _ => Run(() => SetEnabled(!Enabled)), new CommandMetadata(ToggleCommand, "Toggle Vim Emulation", "Vim")));
        _disposables.Add(commands.Register(EnableCommand, _ => Run(() => SetEnabled(true)), new CommandMetadata(EnableCommand, "Enable Vim Emulation", "Vim")));
        _disposables.Add(commands.Register(DisableCommand, _ => Run(() => SetEnabled(false)), new CommandMetadata(DisableCommand, "Disable Vim Emulation", "Vim")));
        _disposables.Add(commands.Register(CheatSheetCommand, _ => Run(ShowCheatSheet), new CommandMetadata(CheatSheetCommand, "Show Vim Cheat Sheet", "Vim")));
        VimrcLoaded = _configuration.ReloadVimrcAsync();
    }

    public VimSession Session { get; }
    public bool Enabled => _configuration.Enabled;
    public IEventSource<bool> EnabledChanged => _configuration.EnabledChanged;
    public Task VimrcLoaded { get; private set; }
    public IReadOnlyList<string> VimrcErrors => _configuration.VimrcErrors;

    public void SetEnabled(bool enabled) => _configuration.SetEnabled(enabled);

    public VimEngine CreateEngine(IVimHost host) => new(host, Session);

    public Task ReloadVimrcAsync(CancellationToken ct = default) => VimrcLoaded = _configuration.ReloadVimrcAsync(ct);

    private void ShowCheatSheet()
    {
        if (_output is null) return;
        var channel = _output.GetOrCreate(CheatSheetChannel);
        channel.Clear();
        channel.AppendLine(VimCheatSheet.Text);
    }

    private static ValueTask<object?> Run(Action action)
    {
        action();
        return ValueTask.FromResult<object?>(null);
    }

    public void Dispose() => _disposables.Dispose();
}

/// <summary>The text of <c>vim.showCheatSheet</c>.</summary>
public static class VimCheatSheet
{
    public const string Text = """
        Vim cheat sheet (CsCode.Vim)

        Modes     Esc normal | i a I A o O insert | R replace | v V Ctrl-V visual | : command line | Ctrl-O one Normal command from Insert
        Motions   h j k l  w W b B e E ge gE  0 ^ $ g_ |  f F t T ; ,  % ( ) { } [[ ]]  H M L gg G {n}G  ' ` marks  / ? n N * #  gj gk g0 g$
        Operators d c y > < = g~ gu gU g? gq gw zf  (doubled: dd cc yy >> << == g~~ guu gUU g?? gqq)
        Edits     x X D C s S Y p P gp gP J gJ ~ r R .  u Ctrl-R U  Ctrl-A Ctrl-X  & g&
        Objects   iw aw iW aW is as ip ap i( a( i[ a[ i{ a{ i< a< i" a" i' a' i` a` it at
        Registers "{a-z} "{A-Z} append  "0 yank  "1-"9 deletes  "- small delete  "_ black hole  "+ "* clipboard  ". ": "/ "% read only
        Macros    q{a-z} record, q stop, @{a-z} run, @@ repeat, {n}@a, @: last command, :{range}normal @a
        Marks     m{a-zA-Z}  '{mark} `{mark}  '' ``  '[ ']  '< '>  g; g,  Ctrl-O Ctrl-I
        Search    /pat ?pat n N * # g* g#  :noh   :%s/pat/rep/gc   :g/pat/cmd   :v/pat/cmd
        Visual    o O gv  I A c $ r J > <  (block)  aw ip (extend)  : '<,'> range
        Surround  ys{motion}{char} yss ds{char} cs{old}{new} S{char} (visual)
        Comment   gc{motion} gcc gC (block) and gc in Visual mode
        Ex        :w :q :wq :x :e :bn :bp :sp :vsp :set :map :nnoremap :let :normal :undo :redo :registers :marks :jumps :changes
        Settings  vim.useSystemClipboard vim.leader vim.hlsearch vim.ignorecase vim.smartcase vim.handleKeys vim.normalModeKeyBindings ...
        """;
}
