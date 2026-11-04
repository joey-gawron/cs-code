using CsCode.Core.Platform;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CsCode.Platform.Tests;

/// <summary>
/// The keys of Toggle Word Wrap, the font zoom commands and Visual Studio's Make Uppercase in both presets, on Linux and macOS (docs/EDITOR.md §7):
/// they resolve in the editor, leave the keys they would shadow alone (Visual Studio's Ctrl+- Navigate Backward, Ctrl+U Cursor Undo, Ctrl+D
/// Duplicate; VS Code's Ctrl+Shift+U Output) and do nothing outside the editor.
/// </summary>
public class EverydayEditorKeysTests : IDisposable
{
    private readonly KeybindingFixture _f = new();
    public void Dispose() => _f.Dispose();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string? Press(Keybindings.KeybindingService svc, string chord)
    {
        KeyResolveResult? last = null;
        foreach (var s in KeyStrokeParser.ParseChord(chord)) last = svc.Resolve(s);
        svc.ResetChord();
        return last is { Kind: KeyResolution.Matched } r ? r.Binding!.Command : null;
    }

    public static TheoryData<string, HostOs> PresetsOnLinuxAndMac =>
        new() { { "visualstudio", HostOs.Linux }, { "visualstudio", HostOs.MacOs }, { "vscode", HostOs.Linux }, { "vscode", HostOs.MacOs } };

    [Theory]
    [MemberData(nameof(PresetsOnLinuxAndMac))]
    public async Task Word_wrap_and_font_zoom_keys_resolve_in_the_editor(string preset, HostOs os)
    {
        using var svc = _f.Create(os);
        await svc.SetPresetAsync(preset, Ct);
        var mac = os == HostOs.MacOs;
        var vs = preset == "visualstudio";
        var cmd = mac ? "cmd" : "ctrl";

        Assert.Null(Press(svc, "alt+z")); // only in the editor
        _f.Context.Set("editorTextFocus", true);
        Assert.Equal("editor.action.toggleWordWrap", Press(svc, "alt+z"));
        Assert.Equal("editor.action.fontZoomIn", Press(svc, cmd + "+="));
        Assert.Equal("editor.action.fontZoomIn", Press(svc, cmd + "+shift+="));
        Assert.Equal("editor.action.fontZoomIn", Press(svc, cmd + "+numpad_add"));
        Assert.Equal("editor.action.fontZoomOut", Press(svc, cmd + "+numpad_subtract"));
        Assert.Equal("editor.action.fontZoomReset", Press(svc, cmd + "+0"));
        Assert.Equal("editor.action.fontZoomReset", Press(svc, cmd + "+numpad0"));
        // Ctrl+- zooms out, except in Visual Studio's preset off the Mac, where it stays Navigate Backward
        Assert.Equal(vs && !mac ? "workbench.action.navigateBack" : "editor.action.fontZoomOut", Press(svc, cmd + "+-"));
        Assert.Equal(mac ? "⌥Z" : "Alt+Z", svc.GetLabel("editor.action.toggleWordWrap"));
        Assert.DoesNotContain(_f.Log.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Theory]
    [MemberData(nameof(PresetsOnLinuxAndMac))]
    public async Task Only_Visual_Studios_preset_binds_Make_Uppercase_and_no_key_it_would_shadow_moves(string preset, HostOs os)
    {
        using var svc = _f.Create(os);
        await svc.SetPresetAsync(preset, Ct);
        _f.Context.Set("editorTextFocus", true);
        var cmd = os == HostOs.MacOs ? "cmd" : "ctrl";
        var vs = preset == "visualstudio";

        Assert.Equal(vs ? "editor.action.transformToUppercase" : "workbench.action.output.toggleOutput", Press(svc, cmd + "+shift+u"));
        Assert.Equal("cursorUndo", Press(svc, cmd + "+u")); // Visual Studio's Make Lowercase key is taken by Cursor Undo
        Assert.Equal(vs ? "editor.action.copyLinesDownAction" : "editor.action.addSelectionToNextFindMatch", Press(svc, cmd + "+d"));
        foreach (var id in new[] { "editor.action.transformToLowercase", "editor.action.transformToTitlecase", "editor.action.transformToSnakecase",
                     "editor.action.transformToCamelcase", "editor.action.transformToKebabcase", "editor.action.transformToPascalcase", "editor.action.duplicateSelection" })
            Assert.Null(svc.GetLabel(id)); // Command Palette only, as in VS Code
        _f.Context.Set("editorReadonly", true);
        if (vs) Assert.Null(Press(svc, cmd + "+shift+u"));
    }
}
