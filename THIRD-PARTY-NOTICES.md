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
| Fira Code typeface 6.2, Copyright (c) 2014 The Fira Code Project Authors (<https://github.com/tonsky/FiraCode>) | SIL Open Font License 1.1 (`assets/fonts/fira-code/OFL.txt`) | Default code font on every OS (editor, code panels, terminal): the unmodified static Light, Regular, Medium, SemiBold and Bold TTFs, embedded in `CsCode.Ui` |
| SkiaSharp, HarfBuzzSharp (transitive, via Avalonia.Skia) | MIT | Rendering and text shaping |
| CommunityToolkit.Mvvm | MIT | `ObservableObject`, relay commands |
| Microsoft.Extensions.DependencyInjection / Logging (+ Abstractions) | MIT | Composition root |

## Colour themes (embedded in TextMateSharp.Grammars 2.0.3, MIT)

CsCode offers the 21 colour themes that the `TextMateSharp.Grammars` 2.0.3 NuGet package (MIT licence expression in its nuspec; authors
"Daniel Peñalba"; repository <https://github.com/danipen/TextMateSharp>, commit `4532112f6ee96c8d7847ee66a79719dfe58e9f43`) embeds as resources
(`TextMateSharp.Grammars.Resources.Themes.*.json`). They are read from the package at run time (`src/CsCode.Editor/Theming/BuiltinColorThemes.cs`);
no theme file is copied into this repository. CsCode completes their workbench colours for its Visual Studio 2022 style shell and may adjust chrome
text colours for contrast (docs/SHELL.md); the token colours are used as they are.

| Theme (label in CsCode) | Resource file | Origin | Licence |
|---|---|---|---|
| Dark+ (default dark), Light+ (default light), Dark (Visual Studio), Light (Visual Studio), Dark High Contrast, Light High Contrast | `dark_plus.json`, `light_plus.json`, `dark_vs.json`, `light_vs.json`, `hc_black.json`, `hc_light.json` | Visual Studio Code's built-in `theme-defaults` extension (the files carry VS Code's `vscode://schemas/color-theme` schema and VS Code's names) | MIT, Copyright (c) Microsoft Corporation (Visual Studio Code, <https://github.com/microsoft/vscode>) |
| Abyss, Kimbie Dark, Monokai, Monokai Dimmed, Quiet Light, Red, Solarized Dark, Solarized Light, Tomorrow Night Blue | `abyss-color-theme.json`, `kimbie-dark-color-theme.json`, `monokai-color-theme.json`, `dimmed-monokai-color-theme.json`, `quietlight-color-theme.json`, `Red-color-theme.json`, `solarized-dark-color-theme.json`, `solarized-light-color-theme.json`, `tomorrow-night-blue-color-theme.json` | Visual Studio Code's built-in theme extensions (`theme-abyss`, `theme-kimbie-dark`, `theme-monokai`, `theme-monokai-dimmed`, `theme-quietlight`, `theme-red`, `theme-solarized-dark`, `theme-solarized-light`, `theme-tomorrow-night-blue`); the palettes adapt Monokai (Wimer Hazenberg), Solarized (Ethan Schoonover), Kimbie (Jan T. Sott) and Tomorrow (Chris Kempson)* | MIT, Copyright (c) Microsoft Corporation (Visual Studio Code) |
| Atom One Dark, Atom One Light, One Dark, Dracula, Visual Studio Dark (TextMateSharp), Visual Studio Light (TextMateSharp) | `atom-one-dark-color-theme.json`, `atom-one-light-color-theme.json`, `onedark-color-theme.json`, `dracula-color-theme.json`, `visual-studio-dark-theme.json`, `visual-studio-light-theme.json` | Shipped by TextMateSharp beside VS Code's themes (not part of VS Code): all six share one file template (the same 69 workbench keys and 19 token rules); named after Atom's One Dark/One Light (GitHub), Dracula (Zeno Rocha)* and Visual Studio | MIT (the TextMateSharp.Grammars package licence) |

What was checked, offline, for this table (2026-10-29): the package's nuspec (licence expression `MIT`, repository and commit above); every theme
file's text (none carries a licence, copyright or author notice; Monokai's opens with a comment that its colours "are based on the original
Monokai"); the six VS Code default themes carry VS Code's schema id and names; the nine other VS Code themes have the file names and labels of
VS Code's built-in theme extensions; the six TextMateSharp themes differ from each other only in name and colour values. The package embeds VS
Code extension manifests (with `"license": "MIT"`) for its grammars but none for its themes, so the per-theme licence above rests on the package
licence and on VS Code's repository licence (MIT) for the files that come from it; the upstream repositories could not be consulted offline. \* The
palette designers are credited from general knowledge of these well-known schemes, not from anything in the package; no file of theirs is included.

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

## Tree-sitter (bundled runtime, grammars and queries)

The syntax engine behind `editor.syntaxEngine` (`src/CsCode.Editor.TreeSitter`, docs/TREE-SITTER.md) calls the tree-sitter C library
through its own P/Invoke declarations. The app ships a `tree-sitter/` folder built by `tools/treesitter/build-grammars.sh` from the pinned
sources in `tools/treesitter/manifest.json` (GitHub archives at the commits nvim-treesitter pins, each checked against its SHA-256):

* the tree-sitter runtime (`lib/`), **MIT**, Copyright (c) 2018 Max Brunsfeld;
* 42 grammars compiled from 38 grammar projects: **MIT** except tree-sitter-elixir, tree-sitter-erlang and tree-sitter-hcl (**Apache-2.0**);
* nvim-treesitter's query files (`grammars/queries/`), copied unmodified, **Apache-2.0**, plus the grammar projects' own `tags.scm` (their licence).
* nvim-treesitter-textobjects' `textobjects.scm` files (`grammars/queries/<name>/`, Vim's syntax text objects), copied unmodified, **Apache-2.0**.

The folder carries a NOTICE (`tools/treesitter/NOTICE`) and the full licence text of every source (and tree-sitter-elixir's NOTICE) in
`tree-sitter/licenses/`. The build refuses any licence outside MIT, Apache-2.0, ISC, BSD-2/3-Clause, 0BSD and CC0-1.0, so no GPL/LGPL/EUPL
grammar can be bundled. CsCode's own queries (`src/CsCode.Editor.TreeSitter/OwnQueries`) are CsCode's.
