using System.Text.Json;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using CsCode.Core.Platform;
using CsCode.Editor.Documents;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.View;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>
/// T30: per-language settings (<c>"[csharp]": {...}</c>) in every shape a configuration service hands them back. The Platform settings service
/// expands dotted keys inside language sections (<c>{"editor": {"tabSize": 2}}</c>); hand-written or other services keep them flat
/// (<c>{"editor.tabSize": 2}</c>); the editor read only the flat one, so in the real app every per-language editor setting was ignored.
/// </summary>
public class LanguageSettingsTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static TheoryData<string> Shapes => new()
    {
        """{ "editor.tabSize": 2, "editor.insertSpaces": false, "editor.rulers": [72], "editor.guides.indentation": false }""",
        """{ "editor": { "tabSize": 2, "insertSpaces": false, "rulers": [72], "guides": { "indentation": false } } }""",
        """{ "editor": { "tabSize": 2, "insertSpaces": false, "rulers": [72], "guides.indentation": false } }""",
        """{ "editor.tabSize": 2, "editor": { "insertSpaces": false, "rulers": [72] }, "editor.guides": { "indentation": false } }""",
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void A_language_section_is_read_in_every_shape(string section)
    {
        var cfg = new TestConfigurationService();
        cfg.Set("editor.tabSize", 8);
        cfg.Set("editor.rulers", new[] { 100 });
        cfg.Set("[csharp]", Json(section));

        var cs = EditorSettings.Read(cfg, "csharp");
        Assert.Equal(2, cs.TabSize);
        Assert.False(cs.InsertSpaces);
        Assert.Equal([72], cs.Rulers);
        Assert.False(cs.IndentGuides);

        var other = EditorSettings.Read(cfg, "python"); // another language: the global values
        Assert.Equal(8, other.TabSize);
        Assert.True(other.InsertSpaces);
        Assert.Equal([100], other.Rulers);
        Assert.True(other.IndentGuides);
        var none = EditorSettings.Read(cfg, null);
        Assert.Equal(8, none.TabSize);
    }

    [Fact]
    public void A_wrongly_typed_or_null_override_falls_back_to_the_global_value()
    {
        var cfg = new TestConfigurationService();
        cfg.Set("editor.tabSize", 6);
        cfg.Set("editor.fontSize", 16);
        cfg.Set("[csharp]", Json("""{ "editor": { "tabSize": "two", "fontSize": null } }"""));
        var cs = EditorSettings.Read(cfg, "csharp");
        Assert.Equal(6, cs.TabSize);
        Assert.Equal(16, cs.FontSize);
    }

    [Fact]
    public void Object_and_string_valued_settings_honour_the_language_section()
    {
        var cfg = new TestConfigurationService();
        cfg.Set("[markdown]", Json("""{ "editor": { "quickSuggestions": { "other": "off" }, "wordWrap": "on" } }"""));
        var md = EditorSettings.Read(cfg, "markdown");
        Assert.Equal("off", md.QuickSuggestions);
        Assert.True(md.WordWrap);
        var cs = EditorSettings.Read(cfg, "csharp");
        Assert.Equal("on", cs.QuickSuggestions);
        Assert.False(cs.WordWrap);
    }

    [Fact]
    public void File_settings_honour_the_language_section_and_keep_the_size_limits_global()
    {
        var cfg = new TestConfigurationService();
        cfg.Set("files.trimTrailingWhitespace", false);
        cfg.Set("cscode.editor.maxFileSizeBytes", 1234567);
        cfg.Set("[markdown]", Json("""{ "files": { "trimTrailingWhitespace": true, "insertFinalNewline": true }, "editor": { "formatOnSave": true }, "cscode": { "editor": { "maxFileSizeBytes": 1 } } }"""));
        var md = FileSettings.Read(cfg, "markdown");
        Assert.True(md.TrimTrailingWhitespace);
        Assert.True(md.InsertFinalNewline);
        Assert.True(md.FormatOnSave);
        Assert.Equal(1234567, md.MaxFileSizeBytes);
        var plain = FileSettings.Read(cfg, "plaintext");
        Assert.False(plain.TrimTrailingWhitespace);
        Assert.False(plain.FormatOnSave);
        Assert.False(FileSettings.Read(cfg).TrimTrailingWhitespace);
    }

    [AvaloniaFact]
    public async Task A_view_takes_its_languages_nested_section_and_follows_a_change_of_it()
    {
        using var host = ViewHost.Create();
        host.Config.Set("[csharp]", Json("""{ "editor": { "tabSize": 2, "insertSpaces": false } }"""));
        var (_, cs, window) = await host.OpenAsync("class C {}\n", "a.cs");
        var (_, txt, _) = await host.OpenAsync("text\n", "b.txt");
        Assert.Equal(2, cs.Settings.TabSize);
        Assert.False(cs.Settings.InsertSpaces);
        Assert.Equal(2, cs.Editor.Options.IndentationSize);
        Assert.False(cs.Editor.Options.ConvertTabsToSpaces);
        Assert.Equal(4, txt.Settings.TabSize);
        Assert.True(txt.Settings.InsertSpaces);

        host.Config.Set("[csharp]", Json("""{ "editor": { "tabSize": 3 } }""")); // live
        await ViewHost.WaitFor(() => cs.Settings.TabSize == 3, "the view re-read its language section");
        Assert.True(cs.Settings.InsertSpaces);
        Assert.Equal(4, txt.Settings.TabSize);
        _ = window;
    }

    [AvaloniaFact]
    public async Task Save_participants_follow_the_documents_language_section()
    {
        using var host = ViewHost.Create();
        host.Config.Set("[markdown]", Json("""{ "files": { "trimTrailingWhitespace": true } }"""));
        var (md, _, _) = await host.OpenAsync("# title   \n", "a.md");
        var (txt, _, _) = await host.OpenAsync("text   \n", "b.txt");
        md.ApplyEdits([new CsCode.Core.Text.TextEdit(CsCode.Core.Text.TextRange.At(1, 0), "x  ")]);
        txt.ApplyEdits([new CsCode.Core.Text.TextEdit(CsCode.Core.Text.TextRange.At(1, 0), "y  ")]);
        await md.SaveAsync();
        await txt.SaveAsync();
        ViewHost.Pump();
        Assert.Equal("# title\nx", File.ReadAllText(host.Dir.Combine("a.md")));
        Assert.Equal("text   \ny  ", File.ReadAllText(host.Dir.Combine("b.txt")));
    }
}
