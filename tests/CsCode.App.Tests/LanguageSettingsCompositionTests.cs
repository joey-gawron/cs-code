using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using CsCode.App.Docking;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.View;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>
/// T30 through the REAL composition: the Platform settings service reads settings.json from a temp <c>CSCODE_HOME</c> and hands language sections
/// back with their dotted keys expanded; the editor views, the indentation they type and the document's save participants must follow the
/// <c>"[csharp]"</c> section while another language keeps the global values.
/// </summary>
public class LanguageSettingsCompositionTests
{
    private static async Task<CodeEditorControl> OpenAsync(DocumentWell well, string path)
    {
        var tab = await well.OpenAsync(new Uri(path));
        Assert.True(await RealAppHost.WaitFor(() => tab.LiveView is CodeEditorControl, 10_000), "the editor was not presented");
        return (CodeEditorControl)tab.LiveView!;
    }

    private static void AddSettings(RealAppHost h, string settings)
    {
        var path = h.Home.Combine("config/settings.json");
        var current = File.ReadAllText(path).TrimEnd();
        File.WriteAllText(path, current[..^1].TrimEnd() + ",\n" + settings + "\n}\n");
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task A_csharp_section_sets_tab_size_and_tabs_for_csharp_only_in_the_real_app()
    {
        await using var host = RealAppHost.Create(h =>
        {
            AddSettings(h, """
                "vim.enable": false,
                "editor.tabSize": 4,
                "editor.insertSpaces": true,
                "files.trimTrailingWhitespace": false,
                "[csharp]": { "editor.tabSize": 2, "editor.insertSpaces": false, "files.trimTrailingWhitespace": true }
                """);
            h.Files.WriteFile("a.cs", "class C\n{\n}\n");
            h.Files.WriteFile("b.py", "x = 1\n");
        });
        await host.StartAsync();
        var config = host.Get<IConfigurationService>();
        Assert.Equal(System.Text.Json.JsonValueKind.Object, config.GetRaw("[csharp]")!.Value.GetProperty("editor").ValueKind); // the nested shape
        var well = host.Shell.Well;

        var cs = await OpenAsync(well, host.Files.Combine("a.cs"));
        Assert.Equal("csharp", cs.Document.LanguageId);
        Assert.Equal(2, cs.Modal.TabSize);
        Assert.False(cs.Modal.InsertSpaces);

        var py = await OpenAsync(well, host.Files.Combine("b.py"));
        Assert.Equal("python", py.Document.LanguageId);
        Assert.Equal(4, py.Modal.TabSize);
        Assert.True(py.Modal.InsertSpaces);

        // What Enter types inside the braces follows the section: a tab in C#.
        string? Indent() => host.Get<IStatusBarService>().Items.FirstOrDefault(i => i.Id == "editor.indent")?.Text;
        Assert.True(await RealAppHost.WaitFor(() => Indent() == "Spaces: 4"), Indent());
        cs = await OpenAsync(well, host.Files.Combine("a.cs")); // active again (the shell may have recreated its view)
        await DevScene.SettleAsync(5);
        Assert.Equal(2, cs.Modal.TabSize);
        Assert.True(await RealAppHost.WaitFor(() => Indent() == "Tab Size: 2"), Indent()); // the status bar reads the section too
        cs.Modal.Controller.SetSelection(TextRange.At(1, 1));
        cs.Modal.Controller.Focus();
        await DevScene.SettleAsync(3);
        Assert.True(await RealAppHost.WaitFor(() => cs.Modal.Controller.IsFocused));
        host.Window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
        host.Window.KeyRelease(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
        await DevScene.SettleAsync(3);
        Assert.Equal("class C\n{\n\t\n}\n", cs.Document.GetText().Replace("\r\n", "\n"));

        // The save participant from the section (trim trailing whitespace) for C# only.
        cs.Document.ApplyEdits([new TextEdit(TextRange.At(2, 1), "int x;   ")]);
        py.Document.ApplyEdits([new TextEdit(TextRange.At(0, 5), "   ")]);
        await cs.Document.SaveAsync();
        await py.Document.SaveAsync();
        Assert.Equal("class C\n{\n\tint x;\n}\n", File.ReadAllText(host.Files.Combine("a.cs")));
        Assert.Equal("x = 1   \n", File.ReadAllText(host.Files.Combine("b.py")));

        // A live change of the section, the way the Settings panel writes it.
        await config.UpdateAsync("[csharp]", new Dictionary<string, object> { ["editor.tabSize"] = 3 });
        Assert.True(await RealAppHost.WaitFor(() => cs.Modal.TabSize == 3), $"tab size {cs.Modal.TabSize}");
        Assert.True(cs.Modal.InsertSpaces);
        Assert.Equal(4, py.Modal.TabSize);
    }
}
