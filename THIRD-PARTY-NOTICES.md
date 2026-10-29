# Third-party notices

CsCode uses the following third-party components. This file is owned by the Shell module; other modules add their own sections.

## Icons: Codicons (CC-BY-4.0, attribution required)

The shell's icon set is **Codicons**, Copyright (c) Microsoft Corporation, licensed under the
[Creative Commons Attribution 4.0 International licence](https://creativecommons.org/licenses/by/4.0/).
Source: <https://github.com/microsoft/vscode-codicons>.

The icons are embedded as vector path data (`src/CsCode.Ui/Icons/IconPaths.g.cs`, rounded to one decimal place and rendered by the `Icon`
control), not as a font. The path data was extracted at generation time from the `IconPacks.Avalonia.Codicons` NuGet package
(MIT licence, MahApps.Metro.IconPacks contributors); that package is **not** a dependency of CsCode. See `src/CsCode.Ui/Icons/README.md`.
Changes made: coordinates rounded; icons selected and renamed to their kebab-case Codicon names.

## UI framework and runtime libraries

| Component | Licence | Notes |
|---|---|---|
| Avalonia (`Avalonia`, `Avalonia.Desktop`, `Avalonia.Skia`, `Avalonia.Headless`, `Avalonia.Themes.Fluent`) | MIT | UI toolkit; Fluent theme supplies control templates that the workbench theme retargets |
| Dock.Avalonia (`Dock.Avalonia`, `Dock.Model.Mvvm`, `Dock.Avalonia.Themes.Fluent` and its dependent `Dock.Controls.*` packages) | MIT | Docking: document well, tool windows, floating windows |
| Avalonia.Fonts.Inter / Inter typeface | SIL Open Font License 1.1 | Fallback UI font when Segoe UI is not installed |
| SkiaSharp, HarfBuzzSharp (transitive, via Avalonia.Skia) | MIT | Rendering and text shaping |
| CommunityToolkit.Mvvm | MIT | `ObservableObject`, relay commands |
| Microsoft.Extensions.DependencyInjection / Logging (+ Abstractions) | MIT | Composition root |

## Development-time only (not shipped)

| Component | Licence | Notes |
|---|---|---|
| Microsoft.CodeAnalysis.BannedApiAnalyzers | MIT | Compile-time rule that bans blocking calls on the UI thread |
| xUnit v3, Avalonia.Headless.XUnit, Microsoft.NET.Test.Sdk | Apache-2.0 / MIT | Tests and headless UI screenshots |

## Names and ids

Colour ids (`editor.background`, `statusBar.background`, ...) and command ids (`workbench.action.*`) follow the Visual Studio Code naming
convention so that users' existing theme JSON and keybindings keep working. No Visual Studio Code or Visual Studio source code, icons or
artwork is included, and the application icon (`src/CsCode.App/Views/AppIcon.cs`) is original artwork. "Visual Studio" is a trademark of
Microsoft Corporation; the theme names "Visual Studio Dark/Blue/Light" describe a colour palette only and imply no affiliation.
