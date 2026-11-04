using CsCode.Core.Platform;
using CsCode.Platform.Keybindings;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CsCode.Platform.Tests;

/// <summary>
/// The bookmark keys of both presets (docs/EDITOR.md §7g): Visual Studio's Ctrl+K chords where that keymap has them free, the Bookmarks
/// extension's Ctrl+Alt keys in the VS Code keymap, with Cmd on a Mac; and the keys that stay what they were because they were taken.
/// </summary>
public sealed class BookmarkKeysTests : IDisposable
{
    private readonly KeybindingFixture _f = new();
    public void Dispose() => _f.Dispose();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string? Run(IKeybindingService svc, string chord)
    {
        KeyResolveResult? last = null;
        foreach (var s in KeyStrokeParser.ParseChord(chord)) last = svc.Resolve(s);
        svc.ResetChord();
        return last!.Kind == KeyResolution.Matched ? last.Binding!.Command : null;
    }

    public static TheoryData<string, HostOs, string, string?> Keys => new()
    {
        // Visual Studio: Ctrl+K Ctrl+N / Ctrl+K Ctrl+P / Ctrl+K Ctrl+W; Ctrl+K Ctrl+K and Ctrl+K Ctrl+L are Indent / Outdent Lines in this keymap
        { "visualstudio", HostOs.Linux, "ctrl+k ctrl+n", "bookmarks.jumpToNext" },
        { "visualstudio", HostOs.Linux, "ctrl+k ctrl+p", "bookmarks.jumpToPrevious" },
        { "visualstudio", HostOs.Linux, "ctrl+k ctrl+w", "bookmarks.showWindow" },
        { "visualstudio", HostOs.Linux, "ctrl+k ctrl+k", "editor.action.indentLines" },
        { "visualstudio", HostOs.Linux, "ctrl+k ctrl+l", "editor.action.outdentLines" },
        { "visualstudio", HostOs.MacOs, "cmd+k cmd+k", "bookmarks.toggle" }, // free on a Mac: Indent is Cmd+] there
        { "visualstudio", HostOs.MacOs, "cmd+k cmd+n", "bookmarks.jumpToNext" },
        { "visualstudio", HostOs.MacOs, "cmd+k cmd+p", "bookmarks.jumpToPrevious" },
        { "visualstudio", HostOs.MacOs, "cmd+k cmd+w", "bookmarks.showWindow" },
        { "visualstudio", HostOs.MacOs, "cmd+k cmd+l", "editor.toggleFold" },
        { "visualstudio", HostOs.MacOs, "cmd+]", "editor.action.indentLines" },
        // VS Code: the Bookmarks extension's ⌥⌘K / Ctrl+Alt+K, ⌥⌘L / Ctrl+Alt+L, ⌥⌘J / Ctrl+Alt+J
        { "vscode", HostOs.Linux, "ctrl+alt+k", "bookmarks.toggle" },
        { "vscode", HostOs.Linux, "ctrl+alt+l", "bookmarks.jumpToNext" },
        { "vscode", HostOs.Linux, "ctrl+alt+j", "bookmarks.jumpToPrevious" },
        { "vscode", HostOs.MacOs, "alt+cmd+k", "bookmarks.toggle" },
        { "vscode", HostOs.MacOs, "alt+cmd+l", "bookmarks.jumpToNext" },
        { "vscode", HostOs.MacOs, "alt+cmd+j", "bookmarks.jumpToPrevious" },
    };

    [Theory]
    [MemberData(nameof(Keys))]
    public async Task Each_preset_binds_the_bookmark_keys_it_has_free_in_the_editor(string preset, HostOs os, string chord, string? command)
    {
        using var svc = _f.Create(os);
        await svc.SetPresetAsync(preset, Ct);
        _f.Context.Set("editorTextFocus", true);
        Assert.Equal(command, Run(svc, chord));
        _f.Context.Set("editorTextFocus", false);
        if (command?.StartsWith("bookmarks.", StringComparison.Ordinal) == true) Assert.NotEqual(command, Run(svc, chord)); // only in an editor
        Assert.DoesNotContain(_f.Log.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task Menus_show_no_key_for_a_skipped_binding()
    {
        using var linux = _f.Create(HostOs.Linux);
        await linux.SetPresetAsync("visualstudio", Ct);
        Assert.Null(linux.GetLabel("bookmarks.toggle"));
        Assert.Null(linux.GetLabel("bookmarks.clearFromAllFiles"));
        Assert.Equal("Ctrl+K, Ctrl+N", linux.GetLabel("bookmarks.jumpToNext"));
        using var mac = _f.Create(HostOs.MacOs);
        await mac.SetPresetAsync("visualstudio", Ct);
        Assert.NotNull(mac.GetLabel("bookmarks.toggle"));
        Assert.Null(mac.GetLabel("bookmarks.clearFromAllFiles"));
    }
}
