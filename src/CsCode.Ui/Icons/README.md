# Icons

`IconPaths.g.cs` is generated: the Codicon vector path data for every name in `icons.txt`, rounded to one decimal
(1 unit = 0.16 px at 16 px) to keep the file small. The icons are Microsoft's **Codicons** (CC-BY-4.0, see
`THIRD-PARTY-NOTICES.md`); the path data was extracted from the MIT-licensed `IconPacks.Avalonia.Codicons` NuGet package, which stores
it as a JSON map inside its assembly. We ship vector geometry rather than the codicon font so that no binary is committed and
icons are crisp at any DPI and take the foreground colour.

Regenerate (scratch project, nothing from it is committed except the generated `.cs`):

1. `dotnet new console -o /tmp/icongen && cd /tmp/icongen && dotnet add package IconPacks.Avalonia.Codicons --version 2.0.0`
2. Replace `Program.cs` with `IconGen.Program.cs.txt`, `dotnet build`.
3. `dotnet bin/Debug/net10.0/icongen.dll <repo>/src/CsCode.Ui/Icons/icons.txt <repo>/src/CsCode.Ui/Icons/IconPaths.g.cs`
   (`... list` prints every available icon name).
