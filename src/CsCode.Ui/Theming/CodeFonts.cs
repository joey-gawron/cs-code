using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using CsCode.Core.Theming;

namespace CsCode.Ui.Theming;

/// <summary>
/// The code font CsCode ships: Fira Code's static faces (Light to Bold, assets/fonts/fira-code, SIL OFL 1.1) embedded in this assembly as
/// Avalonia resources and registered with the font manager as the collection <see cref="FontFamilies.BundledCollection"/>, so the default
/// code font is the same on every OS and needs nothing installed. Settings name it "Fira Code"; <see cref="FontFamilies.Normalize(string)"/>
/// turns that into <c>fonts:CsCode#Fira Code</c>, which only this collection answers (a Fira Code installed on the system is not consulted).
/// </summary>
public static class CodeFonts
{
    /// <summary>Registers the bundled code font, as <c>WithInterFont</c> does Inter. Every AppBuilder that lays out code text calls it.</summary>
    public static AppBuilder WithCodeFonts(this AppBuilder builder) => builder.ConfigureFonts(Register);

    /// <summary>Adds the collection to <paramref name="fonts"/> (replacing an earlier one: the key is the same).</summary>
    public static void Register(FontManager fonts) => fonts.AddFontCollection(new FiraCodeCollection());

    private sealed class FiraCodeCollection() : EmbeddedFontCollection(
        new Uri(FontFamilies.BundledCollection, UriKind.Absolute), new Uri("avares://CsCode.Ui/Assets/Fonts/FiraCode", UriKind.Absolute));
}
