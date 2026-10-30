using System.Runtime.InteropServices;
using Avalonia.Headless.XUnit;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>
/// lsp-core-8 over the REAL composition: the editor's save goes through Platform's file system, which writes the target itself (temp + rename that
/// keeps owner, group, mode and xattrs, or in place for a hard-linked file). Before, the editor wrote a temp file and renamed it over the target:
/// every save dropped the executable bit (Platform's rename does not copy the mode) and broke hard links.
/// </summary>
public class SaveKeepsTheFileTests
{
    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link([MarshalAs(UnmanagedType.LPUTF8Str)] string existing, [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Saving_from_the_editor_keeps_the_executable_bit_and_the_hard_links()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Skip("Unix modes and hard links (Linux)"); return; }
        using var env = new RealAppHome();
        var script = env.Workspace.WriteFile("run.sh", "#!/bin/sh\necho a\n");
        const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead
            | UnixFileMode.GroupExecute | UnixFileMode.OtherRead;
        File.SetUnixFileMode(script, mode);
        var alias = env.Workspace.Combine("alias.sh");
        Assert.Equal(0, Link(script, alias));
        await using var run = new RealAppRun(env);
        await run.MainWindowAsync();

        var doc = await run.Services.GetRequiredService<INavigationService>().OpenAsync(new Uri(script)) ?? throw new InvalidOperationException("not opened");
        var end = doc.PositionAt(doc.Length);
        Assert.True(doc.ApplyEdits([new TextEdit(new TextRange(end, end), "echo b\n")]));
        await run.Services.GetRequiredService<ICommandService>().ExecuteAsync(KnownCommands.Save);

        Assert.False(doc.IsDirty);
        Assert.Equal("#!/bin/sh\necho a\necho b\n", File.ReadAllText(script).Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Equal(mode, File.GetUnixFileMode(script));                 // was 0664 after every save
        Assert.Equal(File.ReadAllText(script), File.ReadAllText(alias));  // the other name sees the save
        Assert.Equal(["alias.sh", "run.sh"], Directory.GetFiles(env.Workspace.Path).Select(Path.GetFileName).Order()); // no temp file left
    }

    // files-5: the mode is kept on every Unix (macOS included), not only where statx tells about hard links.
    [AvaloniaFact(Timeout = 120_000)]
    public async Task Saving_from_the_editor_keeps_the_executable_bit_on_every_unix()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("Unix modes"); return; }
        using var env = new RealAppHome();
        var script = env.Workspace.WriteFile("tool.sh", "#!/bin/sh\n");
        const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
        File.SetUnixFileMode(script, mode);
        await using var run = new RealAppRun(env);
        await run.MainWindowAsync();

        var doc = await run.Services.GetRequiredService<INavigationService>().OpenAsync(new Uri(script)) ?? throw new InvalidOperationException("not opened");
        Assert.True(doc.ApplyEdits([new TextEdit(new TextRange(doc.PositionAt(doc.Length), doc.PositionAt(doc.Length)), "true\n")]));
        await run.Services.GetRequiredService<ICommandService>().ExecuteAsync(KnownCommands.Save);

        Assert.False(doc.IsDirty);
        Assert.Equal("#!/bin/sh\ntrue\n", File.ReadAllText(script).Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Equal(mode, File.GetUnixFileMode(script));
    }
}
