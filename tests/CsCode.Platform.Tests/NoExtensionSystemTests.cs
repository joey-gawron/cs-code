using System.Reflection;
using System.Text.RegularExpressions;
using CsCode.Core.Platform;
using CsCode.Platform.Keybindings;
using Xunit;

namespace CsCode.Platform.Tests;

/// <summary>
/// CsCode deliberately has no extension system (ARCHITECTURE §1, decision 2026-10-28). These guards keep its remnants — members, command
/// ids, paths and "extension host" wording in the shared contracts — from creeping back into Core and Platform.
/// </summary>
public partial class NoExtensionSystemTests
{
    private static readonly Assembly[] Assemblies = [typeof(IAppPaths).Assembly, typeof(AppPaths).Assembly];

    [Fact]
    public void Core_and_Platform_types_have_no_extension_members()
    {
        var offenders = new List<string>();
        foreach (var type in Assemblies.SelectMany(a => a.GetExportedTypes()))
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var m in type.GetMembers(all))
                if (m.Name is "ExtensionId" or "ExtensionsDir" or "ExtensionHost") offenders.Add($"{type.FullName}.{m.Name}");
            foreach (var p in type.GetConstructors().SelectMany(c => c.GetParameters()))
                if (p.Name is "ExtensionId" or "extensionId") offenders.Add($"{type.FullName}({p.Name})");
        }
        Assert.Empty(offenders);
    }

    [Fact]
    public void No_well_known_command_and_no_keymap_preset_refers_to_extensions()
    {
        var ids = typeof(KnownCommands).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => f.GetRawConstantValue() as string).OfType<string>().ToArray();
        Assert.NotEmpty(ids);
        Assert.DoesNotContain(ids, id => id.Contains("extensions", StringComparison.OrdinalIgnoreCase));

        var platform = typeof(KeybindingService).Assembly;
        var presets = platform.GetManifestResourceNames().Where(n => n.EndsWith(".json", StringComparison.Ordinal)).ToArray();
        Assert.Equal(KeybindingService.Presets.Count, presets.Length);
        foreach (var name in presets)
        {
            using var reader = new StreamReader(platform.GetManifestResourceStream(name)!);
            Assert.DoesNotContain("workbench.extensions.", reader.ReadToEnd(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Core_sources_and_the_readme_do_not_describe_an_extension_host()
    {
        var root = RepositoryRoot();
        Assert.SkipWhen(root is null, "the sources are not next to this test build (CsCode.slnx not found above the output directory)");

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root!, "src", "CsCode.Core"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                // What may legitimately say "extension": file extensions, C# extension-method classes, Microsoft.Extensions, and the statement that there is none.
                var rest = AllowedExtensionWording().Replace(lines[i], "");
                if (rest.Contains("extension", StringComparison.OrdinalIgnoreCase) || rest.Contains("contributes.", StringComparison.Ordinal)
                    || rest.Contains("Open VSX", StringComparison.OrdinalIgnoreCase))
                    offenders.Add($"{Path.GetRelativePath(root!, file)}:{i + 1}: {lines[i].Trim()}");
            }
        }
        Assert.Empty(offenders);

        var readme = File.ReadAllText(Path.Combine(root!, "README.md"));
        Assert.DoesNotContain("Open VSX", readme, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("extension compatibility", readme, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("module: Extensions", File.ReadAllText(Path.Combine(root!, "Directory.Packages.props")), StringComparison.Ordinal);
    }

    [GeneratedRegex(@"Microsoft\.Extensions|\b[A-Z]\w*Extensions\b|\bExtensions\b|file extension|NO extension system")]
    private static partial Regex AllowedExtensionWording();

    private static string? RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "CsCode.slnx"))) return dir.FullName;
        return null;
    }
}
