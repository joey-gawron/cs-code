using CsCode.Dap.Adapters;
using CsCode.Dap.Session;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>
/// netcoredbg binds a breakpoint only to the path the compiler recorded, which has every link resolved: on macOS a project under /var/folders
/// (a link to /private/var/folders) never stopped. The session sends physical paths and maps the adapter's back (docs/CSHARP.md).
/// </summary>
public class SourcePathAliasesTests
{
    [Fact]
    public void A_linked_directory_goes_out_physically_and_comes_back_as_the_editor_opened_it()
    {
        var aliases = new SourcePathAliases(dir => dir.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + dir : dir);

        Assert.Equal("/private/var/folders/x/T/p/Program.cs", aliases.ToAdapter("/var/folders/x/T/p/Program.cs"));
        Assert.Equal("/var/folders/x/T/p/Program.cs", aliases.FromAdapter("/private/var/folders/x/T/p/Program.cs"));
        // a file of the same directory, or below it, that never had a breakpoint (a step into it) maps back too
        Assert.Equal("/var/folders/x/T/p/sub/Other.cs", aliases.FromAdapter("/private/var/folders/x/T/p/sub/Other.cs"));
        // only whole directory names match, and paths elsewhere are left to the caller
        Assert.Null(aliases.FromAdapter("/private/var/folders/x/T/pq/Program.cs"));
        Assert.Null(aliases.FromAdapter("/usr/share/dotnet/x.cs"));
    }

    [Fact]
    public void A_path_without_links_is_sent_unchanged_and_never_mapped()
    {
        var aliases = new SourcePathAliases(dir => dir);
        const string path = "/home/me/src/app/Program.cs";
        Assert.Same(path, aliases.ToAdapter(path));
        Assert.Null(aliases.FromAdapter(path));
    }

    [Fact]
    public void The_longest_translated_directory_wins()
    {
        var aliases = new SourcePathAliases(dir => dir switch
        {
            "/w" => "/real/w",
            "/w/linked" => "/elsewhere/linked",
            _ => dir,
        });
        aliases.ToAdapter("/w/a.cs");
        aliases.ToAdapter("/w/linked/b.cs");
        Assert.Equal("/w/linked/b.cs", aliases.FromAdapter("/elsewhere/linked/b.cs"));
        Assert.Equal("/w/a.cs", aliases.FromAdapter("/real/w/a.cs"));
    }

    [Fact]
    public void A_real_symbolic_link_is_resolved()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "directory symbolic links need privileges on Windows");
        using var temp = new TempDirectory("aliases-");
        var real = Directory.CreateDirectory(Path.Combine(temp.Path, "real")).FullName;
        var link = Path.Combine(temp.Path, "link");
        Directory.CreateSymbolicLink(link, real);
        var physicalReal = SourcePathAliases.PhysicalDirectory(real); // the temp directory itself may be behind a link (macOS)

        var aliases = new SourcePathAliases();
        Assert.Equal(Path.Combine(physicalReal, "Program.cs"), aliases.ToAdapter(Path.Combine(link, "Program.cs")));
        Assert.Equal(Path.Combine(link, "Program.cs"), aliases.FromAdapter(Path.Combine(physicalReal, "Program.cs")));
    }

    [Fact]
    public void Netcoredbg_sessions_get_the_physical_path_quirk_and_other_adapters_do_not()
    {
        Assert.True(NetCoreDbgAdapterQuirks.Instance.BindsPhysicalSourcePaths);
        Assert.False(DebugAdapterQuirks.None.BindsPhysicalSourcePaths);
        Assert.All(NetCoreDbgAdapterFactory.Builtins(null!), b => Assert.Same(NetCoreDbgAdapterQuirks.Instance, b.Quirks));
    }
}
