using System.Text.Json;
using CsCode.Core.Contributions;
using CsCode.Core.Platform;
using CsCode.Editor.View;
using CsCode.Panels.Debug.ToolWindows;
using CsCode.Panels.Terminal;
using CsCode.Platform;
using CsCode.Platform.Keybindings;
using Xunit;

namespace CsCode.Docs.Tests;

/// <summary>
/// docs/RUN-AND-DEBUG.md quotes the keys per OS and per preset: its tables must say what the keymap presets
/// (<c>src/CsCode.Platform/Resources/Keymaps</c>) and the modules' built-in keys actually bind, in the labels CsCode shows (Ctrl+F5, ⌃F5).
/// </summary>
public class RunAndDebugKeysTests
{
    private const string Guide = "docs/RUN-AND-DEBUG.md";
    private const string PresetTable = "| Action | Command | Visual Studio preset, Windows/Linux |";
    private const string BuiltInTable = "| Action | Command | Windows/Linux | macOS |";

    private sealed record Binding(string Command, string? Key, string? Mac, string? Linux, string? Win);

    /// <summary>The labels of <paramref name="command"/>'s keys on <paramref name="os"/> (an entry's "mac"/"linux"/"win" replaces "key" there).</summary>
    private static HashSet<string> Labels(IEnumerable<Binding> bindings, string command, HostOs os) =>
        bindings.Where(b => b.Command == command)
            .Select(b => os switch { HostOs.MacOs => b.Mac ?? b.Key, HostOs.Linux => b.Linux ?? b.Key, _ => b.Win ?? b.Key })
            .OfType<string>()
            .Select(k => KeyBindingFormatter.Format(KeyStrokeParser.ParseChord(k), os))
            .ToHashSet(StringComparer.Ordinal);

    private static IReadOnlyList<Binding> Preset(string name)
    {
        using var stream = typeof(KeybindingService).Assembly.GetManifestResourceStream($"CsCode.Platform.Keymaps.{name}.json")
            ?? throw new InvalidOperationException("no preset " + name);
        using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        static string? Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return [.. doc.RootElement.EnumerateArray().Select(e => new Binding(Str(e, "command")!, Str(e, "key"), Str(e, "mac"), Str(e, "linux"), Str(e, "win")))];
    }

    private static IReadOnlyList<Binding> BuiltIn() =>
    [
        .. EditorMenus.Keybindings.Concat(TaskCommandSet.Keybindings)
            .Append(new KeybindingContribution(DebugPanelsRuntime.AddToWatchCommand, DebugPanelsRuntime.AddToWatchKey))
            .Select(k => new Binding(k.Command, k.Key, k.Mac, k.Linux, k.Win)),
    ];

    /// <summary>A "Windows/Linux" column is right only when both OSes bind the same keys.</summary>
    private static HashSet<string> WindowsAndLinux(IEnumerable<Binding> bindings, string command)
    {
        var linux = Labels(bindings, command, HostOs.Linux);
        var windows = Labels(bindings, command, HostOs.Windows);
        Assert.True(linux.SetEquals(windows), $"{command}: Linux ({string.Join(", ", linux)}) and Windows ({string.Join(", ", windows)}) differ; the guide shows one column");
        return linux;
    }

    private static void AssertCell(string cell, HashSet<string> actual, string what) =>
        Assert.True(RepoDocs.Keys(cell).SetEquals(actual), $"{what}: the guide says '{cell}', the keymap binds '{(actual.Count == 0 ? "—" : string.Join(" or ", actual.Order(StringComparer.Ordinal)))}'");

    [Fact]
    public void The_guides_preset_table_quotes_each_presets_keys_per_OS()
    {
        var rows = RepoDocs.Table(RepoDocs.Read(Guide), PresetTable);
        Assert.NotEmpty(rows);
        var vs = Preset("visualstudio");
        var code = Preset("vscode");
        foreach (var row in rows)
        {
            Assert.Equal(6, row.Length);
            var command = row[1];
            AssertCell(row[2], WindowsAndLinux(vs, command), $"{command}, Visual Studio preset, Windows/Linux");
            AssertCell(row[3], Labels(vs, command, HostOs.MacOs), $"{command}, Visual Studio preset, macOS");
            AssertCell(row[4], WindowsAndLinux(code, command), $"{command}, VS Code preset, Windows/Linux");
            AssertCell(row[5], Labels(code, command, HostOs.MacOs), $"{command}, VS Code preset, macOS");
        }
    }

    /// <summary>A debugging key added to a preset must reach the guide too.</summary>
    [Fact]
    public void Every_debugging_command_a_preset_binds_is_in_the_guides_preset_table()
    {
        var documented = RepoDocs.Table(RepoDocs.Read(Guide), PresetTable).Select(r => r[1]).ToHashSet(StringComparer.Ordinal);
        var debugging = Preset("visualstudio").Concat(Preset("vscode")).Select(b => b.Command)
            .Where(c => c.StartsWith("workbench.action.debug.", StringComparison.Ordinal) || c.StartsWith("editor.debug.", StringComparison.Ordinal)
                || c.StartsWith("workbench.debug.", StringComparison.Ordinal) || c.StartsWith("debug.", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(debugging);
        Assert.Empty(debugging.Except(documented));
    }

    [Fact]
    public void The_guides_built_in_key_table_quotes_the_modules_keys_per_OS()
    {
        var rows = RepoDocs.Table(RepoDocs.Read(Guide), BuiltInTable);
        Assert.NotEmpty(rows);
        var builtIn = BuiltIn();
        foreach (var row in rows)
        {
            Assert.Equal(4, row.Length);
            var command = row[1];
            Assert.True(builtIn.Any(b => b.Command == command), $"{command}: no module contributes a key for it");
            AssertCell(row[2], WindowsAndLinux(builtIn, command), $"{command}, Windows/Linux");
            AssertCell(row[3], Labels(builtIn, command, HostOs.MacOs), $"{command}, macOS");
        }
    }

    /// <summary>The debugging keys the editor, the debug windows and the task runner add are all in the guide.</summary>
    [Fact]
    public void Every_debugging_key_the_modules_add_is_in_the_guides_built_in_table()
    {
        var documented = RepoDocs.Table(RepoDocs.Read(Guide), BuiltInTable).Select(r => r[1]).ToHashSet(StringComparer.Ordinal);
        var debugging = BuiltIn().Select(b => b.Command)
            .Where(c => c.Contains("debug", StringComparison.OrdinalIgnoreCase) || c.StartsWith("workbench.action.tasks.", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Empty(debugging.Except(documented));
    }

    /// <summary>The table parser itself: escapes, code spans and "or" lists.</summary>
    [Fact]
    public void The_table_reader_handles_escapes_code_spans_and_alternatives()
    {
        const string md = "x\n| Action | Command | A | B |\n|---|---|---|---|\n| T | `a.b` | Ctrl+\\` | Ctrl+\\\\, Ctrl+E or F1 |\n\nafter\n";
        var row = Assert.Single(RepoDocs.Table(md, "| Action | Command |"));
        Assert.Equal(["T", "a.b", "Ctrl+`", "Ctrl+\\, Ctrl+E or F1"], row);
        Assert.Equal(new HashSet<string> { "Ctrl+\\, Ctrl+E", "F1" }, RepoDocs.Keys(row[3]));
        Assert.Empty(RepoDocs.Keys("—"));
    }
}
