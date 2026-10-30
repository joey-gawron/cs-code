using Avalonia.Headless.XUnit;
using CommunityToolkit.Mvvm.Input;
using CsCode.App.Docking;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Debugging;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Panels.Search;
using CsCode.Panels.Search.Views;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>
/// Who owns each command id in the REAL composition (<see cref="RealAppHost"/>). The command table is a stack (the newest registration runs), so
/// a duplicate registration is an ownership decision; docs/SHELL.md ("Command ownership") lists the intended ones.
/// </summary>
public class RealAppCommandOwnershipTests
{
    private sealed class RecordingCommands(ICommandService inner) : ICommandService
    {
        /// <summary>Every registration in order, with the module (assembly) whose code the handler is.</summary>
        public List<(string Id, string Module)> Registrations { get; } = new();

        public IDisposable Register(string id, Func<CommandInvocation, ValueTask<object?>> handler, CommandMetadata? metadata = null)
        {
            lock (Registrations) Registrations.Add((id, handler.Method.DeclaringType?.Assembly.GetName().Name ?? "?"));
            return inner.Register(id, handler, metadata);
        }

        public bool Has(string id) => inner.Has(id);
        public IReadOnlyList<CommandMetadata> All => inner.All;
        public ValueTask<object?> ExecuteAsync(string id, params object?[] args) => inner.ExecuteAsync(id, args);
        public IEventSource<string> Registered => inner.Registered;
    }

    private static string Module(Type type) => type.Assembly.GetName().Name!;

    /// <summary>Wraps the real (Platform) command service so every registration of every module is recorded.</summary>
    private static void RecordCommands(IServiceCollection services, Action<RecordingCommands> created)
    {
        var real = services.Last(d => d.ServiceType == typeof(ICommandService));
        services.Remove(real);
        services.AddSingleton<ICommandService>(sp =>
        {
            var inner = real.ImplementationFactory is { } f ? (ICommandService)f(sp)
                : real.ImplementationInstance as ICommandService ?? (ICommandService)ActivatorUtilities.CreateInstance(sp, real.ImplementationType!);
            var recording = new RecordingCommands(inner);
            created(recording);
            return recording;
        });
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Every_command_id_has_one_owner_and_the_documented_shadowing_is_the_only_duplication()
    {
        RecordingCommands? recorded = null;
        await using var host = await RealAppHost.Create(h => h.Files.WriteFile("a.txt", "one\ntwo\nthree\nfour\n"))
            .StartAsync([], s => RecordCommands(s, r => recorded = r));
        await host.Get<INavigationService>().OpenAsync(new Uri(host.Files.Combine("a.txt")));
        await DevScene.SettleAsync(20);

        var duplicated = recorded!.Registrations.GroupBy(r => r.Id).Where(g => g.Count() > 1).Select(g => g.Key).OrderBy(x => x, StringComparer.Ordinal);
        // Intended: the editor registers standalone fallbacks that the shell (Go to Line, Cut/Copy/Paste: they follow keyboard focus) and Dap
        // (Toggle Breakpoint) shadow. The Settings commands belong to the Settings panel alone (the shell's openSettings used to shadow it, so the
        // Settings editor could never open).
        string[] clipboard = ["editor.action.clipboardCopyAction", "editor.action.clipboardCutAction", "editor.action.clipboardPasteAction"];
        Assert.Equal([.. clipboard, KnownCommands.DebugToggleBreakpoint, "workbench.action.gotoLine"], duplicated);

        // Who registered them, in order (the last one runs). The behaviour checks below cannot tell the two Toggle Breakpoint handlers apart
        // (both toggle at the caret), so the owner is asserted by the module whose handler it is.
        string[] RegisteredBy(string id) => recorded.Registrations.Where(r => r.Id == id).Select(r => r.Module).ToArray();
        var editor = Module(typeof(CsCode.Editor.View.EditorViewFactory));
        Assert.Equal([editor, Module(typeof(CsCode.Dap.Service.DebugService))], RegisteredBy(KnownCommands.DebugToggleBreakpoint));
        Assert.Equal([editor, Module(typeof(ShellController))], RegisteredBy("workbench.action.gotoLine"));
        foreach (var id in clipboard) Assert.Equal([editor, Module(typeof(ShellController))], RegisteredBy(id));
        Assert.Equal([Module(typeof(SearchPanelsHost))], RegisteredBy(KnownCommands.OpenSettings));

        // The effective handlers are the owners' ones.
        var palette = await host.OpenPaletteAsync("workbench.action.gotoLine");
        Assert.Equal(":", host.Get<QuickInput.QuickInputViewModel>().Text); // the shell's palette mode, not the editor's input box
        await host.ClosePaletteAsync(palette);
        await host.RunAsync("workbench.action.gotoLine", "3:2"); // the editor's argument form still works
        Assert.Equal(new TextPosition(2, 1), host.Get<INavigationService>().ActiveCaret);

        await host.RunAsync(KnownCommands.DebugToggleBreakpoint); // Dap's: at the active caret (line 3)
        Assert.Equal(2, Assert.Single(host.Get<IBreakpointManager>().SourceBreakpoints).Line);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Open_Settings_opens_the_Settings_editor_as_a_document_tab_and_the_JSON_command_opens_the_file()
    {
        await using var host = await RealAppHost.Create().StartAsync();
        var well = host.Shell.Well;

        await host.RunAsync(KnownCommands.OpenSettings, "editor.fontSize");
        var tab = Assert.IsAssignableFrom<ShellDocument>(well.Active);
        Assert.Equal("view:settings", tab.Id);
        Assert.Equal("Settings", tab.Title);
        Assert.IsType<SettingsView>(tab.LiveView);
        Assert.Equal("editor.fontSize", host.Get<SearchPanelsHost>().LiveSettings?.SearchText);

        // A second Open Settings (here: Tools > Options...) re-activates the same tab.
        await host.RunAsync(KnownCommands.NewUntitledFile);
        Assert.NotSame(tab, well.Active);
        host.Shell.Menus.Refresh();
        var options = host.Shell.Menus.Roots.Single(r => r.Header == "T_ools").Children.Single(c => c.Header == "_Options...");
        Assert.True(options.IsEnabled);
        await ((IAsyncRelayCommand)options.Command!).ExecuteAsync(null);
        await DevScene.SettleAsync(10);
        Assert.Single(well.Tabs, t => t.Id == "view:settings");
        Assert.Same(tab, well.Active);

        await host.RunAsync("workbench.action.openSettingsJson");
        Assert.True(await RealAppHost.WaitFor(() => well.ActiveEditor is not null));
        Assert.Equal(Path.GetFullPath(host.Get<IConfigurationService>().UserSettingsPath), well.ActiveEditor!.Uri.LocalPath);
    }
}
