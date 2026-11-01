using System.Text.Json;
using CsCode.Core.Platform;

namespace CsCode.Panels.Testing;

/// <summary>The settings the Test Explorer owns (registered with <see cref="IConfigurationService.RegisterProperties"/> so the Settings UI shows them).</summary>
public static class TestingSettings
{
    public const string GutterEnabled = "testing.gutterEnabled";
    public const string CodeLens = "testing.codeLens";
    public const string DefaultGutterClickAction = "testing.defaultGutterClickAction";
    public const string OpenTesting = "testing.openTesting";
    public const string AutoDiscover = "cscode.testing.autoDiscover";
    public const string DotNetEnabled = "cscode.testing.dotnet.enabled";
    public const string RustEnabled = "cscode.testing.rust.enabled";
    public const string JavaScriptEnabled = "cscode.testing.javascript.enabled";

    /// <summary>The provider id → its "enabled" setting.</summary>
    public static string EnabledKey(string providerId) => providerId switch
    {
        "dotnet" => DotNetEnabled,
        "rust" => RustEnabled,
        _ => JavaScriptEnabled,
    };

    private static ConfigurationProperty P(string key, object? value, string description, string schemaJson) =>
        new(key, JsonSerializer.SerializeToElement(value), description, JsonDocument.Parse(schemaJson).RootElement.Clone());

    public static IReadOnlyList<ConfigurationProperty> Properties() =>
    [
        P(GutterEnabled, true, "Whether the editor shows a run / debug glyph (with the last result) next to each test the Test Explorer knows.", """{"type":"boolean"}"""),
        P(CodeLens, true, "Whether the editor shows \"Run Test | Debug Test\" above each test the Test Explorer knows (when editor.codeLens is on).", """{"type":"boolean"}"""),
        P(DefaultGutterClickAction, "run", "What a click on a test's glyph in the editor does (right-click always offers every action).",
            """{"type":"string","enum":["run","debug","contextMenu"],"enumDescriptions":["Run the test.","Debug the test.","Open the menu of test actions."]}"""),
        P(OpenTesting, "openOnTestStart", "When the Test Explorer is shown during a run.",
            """{"type":"string","enum":["neverOpen","openOnTestStart","openOnTestFailure","openExplorerOnTestStart"],"enumDescriptions":["Never show it.","Show it when a run starts.","Show it when a test fails.","Show it when a run starts."]}"""),
        P(AutoDiscover, true, "Discover the tests of a folder when it opens (a Rust package is built to list its tests). Off: only when the Test Explorer's Refresh runs.", """{"type":"boolean"}"""),
        P(DotNetEnabled, true, "Find .NET test projects (xUnit v3 run directly, xUnit v2 / NUnit / MSTest through dotnet test).", """{"type":"boolean"}"""),
        P(RustEnabled, true, "Find cargo packages and their #[test] functions.", """{"type":"boolean"}"""),
        P(JavaScriptEnabled, true, "Find JavaScript / TypeScript packages that use Jest or Vitest.", """{"type":"boolean"}"""),
    ];
}
