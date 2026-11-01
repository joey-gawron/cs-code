using System.Text.Json;
using CsCode.App.Docking;
using CsCode.Core.Text;
using CsCode.Sessions.Ipc;

namespace CsCode.App.Background;

/// <summary>What <c>inspect</c> reports about one tab (integration tests of real processes).</summary>
public sealed record InspectedTab(string Uri, string Title, bool Dirty, bool Active, bool Preview, bool Pinned, bool HasView, int CaretLine, int CaretColumn,
    IReadOnlyList<int> Folded, string? Text);

/// <summary>What <c>inspect</c> reports about the process (served only with <c>--headless-test</c>).</summary>
public sealed record InspectedState(string? Session, bool Unnamed, string State, bool Visible, string Title, IReadOnlyList<string> Folders, string? Active,
    IReadOnlyList<InspectedTab> Tabs, int LiveViews, long WorkingSet, long ManagedHeap, int Trims, long LastTrimWorkingSet, int Autosaves, int AutosaveFailures,
    string? LastAutosaveError, IReadOnlyList<string>? WarmedUp = null, bool? BackgroundPriority = null);

public sealed partial class SessionWindowController
{
    /// <summary><c>inspect</c>: the window's state, built on the UI thread; serialised (and, with <c>force</c>, a full GC) on the pool.</summary>
    private async Task<IpcMessage> InspectAsync(IpcMessage request)
    {
        var withText = request.Text == "text";
        var state = await _ui.InvokeAsync(() =>
        {
            var well = _shell.Well;
            var tabs = well.EditorTabs.Select(t =>
            {
                var view = t.Controller?.GetViewState() ?? t.SavedViewState;
                return new InspectedTab(t.Uri.ToString(), t.Title, t.IsDirty, ReferenceEquals(well.ActiveEditor, t), t.IsPreview, t.IsPinned, t.LiveView is not null,
                    view?.Caret.Line ?? -1, view?.Caret.Character ?? -1, view?.FoldedStartLines ?? [], withText ? t.Doc.GetText() : null);
            }).ToArray();
            return new InspectedState(_host.SessionName, _host.IsUnnamed, _host.State, _window.IsVisible, _window.Title ?? "", _workspace?.Folders.Select(f => f.ToString()).ToArray() ?? [],
                well.ActiveEditor?.Uri.ToString(), tabs, well.Tabs.Count(t => t.LiveView is not null), 0, 0, Volatile.Read(ref _trims),
                Interlocked.Read(ref _lastTrimWorkingSet), _autosave.SaveCount, _autosave.FailureCount, _autosave.LastError?.Message, [.. _warmedUp]);
        }).ConfigureAwait(false);
        var heap = GC.GetTotalMemory(forceFullCollection: request.Force == true);
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        state = state with { ManagedHeap = heap, WorkingSet = self.WorkingSet64, BackgroundPriority = Sessions.ProcessPriority.IsBackground() };
        return IpcMessage.Reply() with { Data = JsonSerializer.SerializeToElement(state) };
    }

    /// <summary>The test-only edits: typing, caret moves, folds, closing a tab, closing the window (through the real close path).</summary>
    private async Task<IpcMessage> TestActionAsync(IpcMessage request)
    {
        var well = _shell.Well;
        if (request.Type == IpcVerbs.TestCloseWindow)
        {
            _window.Close();
            return IpcMessage.Reply();
        }
        EditorTab? tab = request.Uri is { } u && Uri.TryCreate(u, UriKind.Absolute, out var uri) ? well.Find(uri) : well.ActiveEditor;
        if (tab is null) return IpcMessage.Failure("no such tab: " + (request.Uri ?? "(active)"));
        switch (request.Type)
        {
            case IpcVerbs.TestEdit:
                var at = tab.Doc.Validate(new TextPosition(request.Line ?? 0, request.Column ?? 0));
                tab.Doc.ApplyEdits([new TextEdit(new TextRange(at, at), request.Text ?? "")]);
                return IpcMessage.Reply();
            case IpcVerbs.TestCaret:
                well.Activate(tab);
                tab.EnsureHost();
                tab.Controller?.SetSelection(TextRange.At(request.Line ?? 0, request.Column ?? 0), reveal: true);
                return tab.Controller is null ? IpcMessage.Failure("the tab has no editor view") : IpcMessage.Reply();
            case IpcVerbs.TestFold:
                well.Activate(tab);
                tab.EnsureHost();
                if (tab.Controller is not { } controller) return IpcMessage.Failure("the tab has no editor view");
                var state = controller.GetViewState();
                controller.RestoreViewState(state with { FoldedStartLines = [.. state.FoldedStartLines, request.Line ?? 0] });
                return IpcMessage.Reply();
            case IpcVerbs.TestCloseTab:
                await well.CloseAsync(tab, prompt: false).ConfigureAwait(true);
                return IpcMessage.Reply();
        }
        return IpcMessage.Failure("unknown test verb " + request.Type);
    }
}
