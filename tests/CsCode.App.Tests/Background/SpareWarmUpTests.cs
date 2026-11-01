using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using CsCode.App.Background;
using CsCode.App.Startup;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Text;
using CsCode.Editor;
using CsCode.Editor.View;
using CsCode.Sessions;
using CsCode.Sessions.Ipc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests.Background;

/// <summary>
/// The warm spare's off-screen warm-up (docs/SESSIONS.md, "The warm spare"), in the test process: a REAL spare (<see cref="BackgroundHost"/> with
/// <c>--spare</c>, the real app and modules, headless) renders its window and a throwaway editor before any claim, leaves nothing of the editor
/// behind, and a claim that arrives in the middle of it takes the editor out before the session is restored.
/// </summary>
public class SpareWarmUpTests
{
    private sealed class Spare : IAsyncDisposable
    {
        private readonly BackgroundHost? _previous;

        private Spare(RealAppHome env, RuntimeDirectory runtime, BackgroundHost host, RealAppRun run, BackgroundHost? previous)
        {
            Env = env;
            Runtime = runtime;
            Host = host;
            Run = run;
            _previous = previous;
        }

        public RealAppHome Env { get; }
        public RuntimeDirectory Runtime { get; }
        public BackgroundHost Host { get; }
        public RealAppRun Run { get; }
        public SessionWindowController Controller => Host.Controller!;
        public EditorViewFactory Editors => (EditorViewFactory)Run.Services.GetRequiredService<IEditorViewFactory>();
        public OverlayLayer Overlay => OverlayLayer.GetOverlayLayer(Run.Session.Window!)!;

        public static async Task<Spare> StartAsync()
        {
            if (OperatingSystem.IsWindows()) Assert.Skip("Unix sockets");
            var env = new RealAppHome();
            env.WriteUserSettings("""{ "workbench.startupEditor": "none", "cscode.background.showTrayIcon": false }""");
            var runtime = RuntimeDirectory.Resolve(name => name == "CSCODE_HOME" ? env.Home.Path : null);
            var store = new SessionStore(env.Home.Combine("data"));
            var host = BackgroundHost.Start(CommandLineOptions.Parse(["--host", "--spare", "--headless-test"]), new CrashHandler(env.CrashLogs.Path), runtime, store, out _);
            Assert.NotNull(host);
            var previous = App.Background;
            App.Background = host;
            var run = new RealAppRun(env);
            var spare = new Spare(env, runtime, host, run, previous);
            Assert.True(await DevHost.WaitFor(() => host.State == ProcessStates.Spare && host.Controller is not null, 60_000), "not a spare: " + host.State);
            return spare;
        }

        public Task<IpcMessage> RequestAsync(IpcMessage request) =>
            Task.Run(() => IpcClient.Request(Runtime.EndpointOf(Environment.ProcessId), request, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30)));

        public async ValueTask DisposeAsync()
        {
            try
            {
                await RequestAsync(new IpcMessage { Type = IpcVerbs.Quit, Force = true });
                await Run.Session.Ended.WaitAsync(TimeSpan.FromSeconds(30));
                await Run.DisposeAsync();
            }
            finally
            {
                App.Background = _previous;
                Host.Dispose();
                Env.Dispose();
            }
        }
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task A_warm_spare_renders_its_window_and_a_coloured_throwaway_editor_and_keeps_nothing_of_the_editor()
    {
        var opened = new List<ITextDocument>();
        await using var s = await Spare.StartAsync();
        using var sub = s.Run.Services.GetRequiredService<IDocumentManager>().Opened.Subscribe(e => opened.Add(e.Document));
        await s.Controller.SpareWarm.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(["window", "editor", "colours"], s.Controller.WarmedUp);
        // Nothing of the throwaway editor is left: no view, no tokenisation session, nothing in the overlay layer, and no document was ever opened
        // (no language server, tab or session heard of it). The spare stays a spare: hidden, unclaimed.
        Assert.Empty(s.Editors.Views);
        Assert.Equal(0, s.Run.Services.GetRequiredService<CsCode.Editor.TextMate.TokenizationManager>().SessionCount);
        Assert.Empty(s.Overlay.Children);
        Assert.Empty(opened);
        Assert.False(s.Run.Session.Window!.IsVisible);
        Assert.Equal(ProcessStates.Spare, s.Host.State);

        // A claim then restores and shows the session as usual.
        var claim = await s.RequestAsync(new IpcMessage { Type = IpcVerbs.Claim, Session = "W" });
        Assert.True(claim.Ok == true, claim.Error);
        Assert.True(await DevHost.WaitFor(() => s.Host.State == ProcessStates.Shown, 30_000), "not shown: " + s.Host.State);
        Assert.Empty(s.Overlay.Children);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task A_claim_in_the_middle_of_the_editor_warm_up_takes_the_throwaway_editor_out_before_the_session_is_restored()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SessionWindowController.EditorWarmUpHoldForTests = async () =>
        {
            reached.TrySetResult();
            await release.Task;
        };
        try
        {
            await using var s = await Spare.StartAsync();
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(60));
            var warm = Assert.Single(s.Editors.Views);
            Assert.Equal(EditorWarmUp.Scheme, warm.Document.Uri.Scheme);
            Assert.Contains(warm, s.Overlay.Children);

            // The claim (a hidden restore, as the launcher sends it) runs while the warm-up waits.
            var claim = await s.RequestAsync(new IpcMessage { Type = IpcVerbs.Claim, Session = "M", Hidden = true });
            Assert.True(claim.Ok == true, claim.Error);
            Assert.Empty(s.Overlay.Children);
            Assert.DoesNotContain(s.Editors.Views, v => v.Document.Uri.Scheme == EditorWarmUp.Scheme);
            Assert.Null(warm.Content); // disposed: its editor released

            // The warm-up, let go, does nothing more to the claimed window.
            release.TrySetResult();
            await s.Controller.SpareWarm.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(["window", "editor"], s.Controller.WarmedUp);
            Assert.Empty(s.Overlay.Children);
            Assert.Empty(s.Editors.Views);
            Assert.Equal(ProcessStates.Hidden, s.Host.State);
        }
        finally
        {
            SessionWindowController.EditorWarmUpHoldForTests = null;
            release.TrySetResult();
        }
    }
}
