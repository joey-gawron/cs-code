using Avalonia.Headless.XUnit;
using CsCode.Panels.Terminal.Tests.Infrastructure;
using Xunit;

namespace CsCode.Panels.Terminal.Tests;

/// <summary>
/// Headless renders of the real application with the terminal running real programs (sandboxed), written to the git-ignored
/// <c>.scratch/screenshots/terminal/</c>: <c>ls --color</c>, nvim editing a file, <c>git log</c> in less. They also assert what is on screen.
/// </summary>
public sealed class ScreenshotTests
{
    private static async Task<TerminalAppHost> TallTerminalAsync()
    {
        var app = await TerminalAppHost.StartAsync();
        await app.RunAsync(TerminalCommandSet.New);
        Assert.True(await app.WaitForScreenAsync("$"));
        app.View!.ToggleMaximize(); // a taller panel shows more of the program
        await TerminalAppHost.SettleAsync(20);
        return app;
    }

    [AvaloniaFact]
    public async Task Ls_color()
    {
        await using var app = await TallTerminalAsync();
        var dir = app.Sandbox.Combine("listing");
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        Directory.CreateDirectory(Path.Combine(dir, "docs"));
        foreach (var f in new[] { "README.md", "build.sh", "archive.tar.gz", "photo.png", "Program.cs" }) File.WriteAllText(Path.Combine(dir, f), "x");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(dir, "build.sh"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.CreateSymbolicLink(Path.Combine(dir, "readme-link"), Path.Combine(dir, "README.md"));
        var session = app.Terminals.ActiveSession!;
        await session.SendTextAsync($"cd '{dir}' && ls --color=always -la && printf '\\e[1;31mbold red\\e[0m \\e[4mul\\e[0m \\e[7mreverse\\e[0m \\e[38;2;255;165;0mtruecolor\\e[0m 中文 ✓\\n'");
        Assert.True(await app.WaitForScreenAsync("truecolor"), app.ScreenText());
        app.Capture("terminal-ls-color");
    }

    [AvaloniaFact]
    public async Task Nvim_editing_a_file()
    {
        var nvim = Support.Which("nvim") ?? (File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nix-profile/bin/nvim"))
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nix-profile/bin/nvim") : null);
        Assert.SkipWhen(nvim is null, "nvim is not installed");
        await using var app = await TallTerminalAsync();
        var file = app.Sandbox.WriteFile("hello.c", "#include <stdio.h>\n\nint main(void)\n{\n    printf(\"hello from the CsCode terminal\\n\");\n    return 0;\n}\n");
        var session = app.Terminals.ActiveSession!;
        await session.SendTextAsync($"'{nvim}' -u NONE -i NONE -n '{file}'");
        Assert.True(await app.WaitForScreenAsync("int main"), app.ScreenText());
        await session.SendTextAsync(":set number\r:syntax on\rGo// edited in the integrated terminal\u001b", addNewLine: false);
        Assert.True(await app.WaitForScreenAsync("edited in the integrated terminal"), app.ScreenText());
        await TerminalAppHost.SettleAsync(20);
        Assert.True(app.View!.Control.Snapshot!.Modes.AlternateScreen);
        app.Capture("terminal-nvim");
        await session.SendTextAsync(":qa!");
        Assert.True(await TerminalAppHost.WaitFor(() => app.View.Control.Snapshot is { Modes.AlternateScreen: false }), "nvim did not leave the alternate screen");
    }

    [AvaloniaFact]
    public async Task Git_log_in_less()
    {
        var repo = Support.RepoRoot();
        Assert.SkipWhen(repo is null || Support.Which("git") is null || Support.Which("less") is null, "git, less or the repository is missing");
        await using var app = await TallTerminalAsync();
        var session = app.Terminals.ActiveSession!;
        await session.SendTextAsync($"GIT_PAGER=less LESS=R GIT_CONFIG_NOSYSTEM=1 git -C '{repo}' log --color=always --decorate --graph --oneline -n 200");
        // --decorate always prints HEAD on the first commit; the subjects change with the history
        Assert.True(await app.WaitForScreenAsync("HEAD"), app.ScreenText());
        await TerminalAppHost.SettleAsync(30);
        app.Capture("terminal-git-log");
        await session.SendTextAsync("q", addNewLine: false);
    }
}
