namespace CsCode.Core.Theming;

/// <summary>
/// Optional capability of an <see cref="IColorThemeService"/> (test for it with <c>service as IColorThemePreview</c>): shows a theme live
/// without selecting it. Theme pickers (the toolbar and Settings dropdown, the palette's Color Theme command) preview while the user arrows
/// through their list. A preview changes <see cref="IColorThemeService.Current"/> and fires <see cref="IColorThemeService.Changed"/>, so
/// every window repaints, but it neither changes the selection nor writes <c>workbench.colorTheme</c>; <see cref="IColorThemeService.SetCurrentAsync"/>
/// commits (and ends the preview), <see cref="EndPreview"/> reverts.
/// </summary>
public interface IColorThemePreview
{
    /// <summary>Id of the theme being previewed; null when none is (Current is then the selection).</summary>
    string? PreviewedThemeId { get; }

    /// <summary>Shows the theme with this id (or label). Unknown themes are ignored.</summary>
    void Preview(string themeId);

    /// <summary>Ends a preview: Current returns to the selection (Changed fires when that differs). A no-op without a preview.</summary>
    void EndPreview();
}
