using CsCode.Core.Platform;
using CsCode.Remote.Shell;
using CsCode.Server;
using Xunit;

namespace CsCode.Remote.Tests;

/// <summary>Tab completion of a remote folder path, and the nix develop environment of a remote workspace (no nix needed).</summary>
public class NixAndCompletionTests
{
    private static CancellationToken Ct => TestEnv.Ct;

    private static readonly Dictionary<string, DirectoryEntry[]> Tree = new(StringComparer.Ordinal)
    {
        ["/"] = [new("home", FileType.Directory), new("etc", FileType.Directory), new("boot.log", FileType.File)],
        ["/home/lima.guest/"] =
        [
            new("src", FileType.Directory), new("scratch", FileType.Directory), new("sync", FileType.SymbolicLink), new(".cache", FileType.Directory),
            new("notes.txt", FileType.File), new("Src", FileType.Directory),
        ],
    };

    private static ValueTask<IReadOnlyList<DirectoryEntry>> List(Uri uri, CancellationToken ct) =>
        Tree.TryGetValue(uri.AbsolutePath, out var entries) ? ValueTask.FromResult<IReadOnlyList<DirectoryEntry>>(entries)
            : ValueTask.FromException<IReadOnlyList<DirectoryEntry>>(new DirectoryNotFoundException(uri.ToString()));

    private static Task<IReadOnlyList<string>> Complete(string text) =>
        RemotePathCompletion.CompleteAsync(List, p => new Uri("lima://nixos" + p), "/home/lima.guest", text, Ct);

    [Fact]
    public async Task Completion_lists_directories_that_start_with_the_typed_name_sorted()
    {
        Assert.Equal(["/home/lima.guest/scratch/", "/home/lima.guest/src/", "/home/lima.guest/sync/"], await Complete("/home/lima.guest/s")); // a symlink may lead to a folder
        Assert.Equal(["/home/lima.guest/Src/"], await Complete("/home/lima.guest/S"));  // case-sensitive, as the host's file system is
        Assert.Equal(["/etc/", "/home/"], await Complete("/"));
        Assert.Equal(["/home/"], await Complete("/h"));
        Assert.Empty(await Complete("/home/lima.guest/n")); // a file is not a folder to open
        Assert.Empty(await Complete("/nowhere/x"));         // an unreadable directory offers nothing
    }

    [Fact]
    public async Task Completion_hides_dot_directories_until_a_dot_is_typed_and_takes_tilde_and_relative_text_from_home()
    {
        Assert.DoesNotContain("/home/lima.guest/.cache/", await Complete("/home/lima.guest/"));
        Assert.Equal(["/home/lima.guest/.cache/"], await Complete("/home/lima.guest/."));
        Assert.Equal(["/home/lima.guest/scratch/"], await Complete("~/sc"));
        Assert.Equal(await Complete("/home/lima.guest/sr"), await Complete("~/sr"));
        Assert.Equal(await Complete("/home/lima.guest/sr"), await Complete("sr"));
        Assert.Contains("/home/lima.guest/src/", await Complete("~"));
    }

    [Fact]
    public void The_nix_command_runs_the_dev_shell_of_the_flake_and_prints_the_environment_between_markers()
    {
        var args = NixDevelopEnvironment.Arguments("/home/me/proj", "M");
        Assert.Equal(["--extra-experimental-features", "nix-command flakes", "develop", "--no-write-lock-file", "/home/me/proj", "--command", "sh", "-c"], args.Take(8));
        Assert.Equal("printf '%s' 'M'; env -0; printf '%s' 'M'", args[8]);
        Assert.Equal("/home/me/proj", NixDevelopEnvironment.FlakeReference("/home/me/proj/", ""));
        Assert.Equal("/home/me/proj#dev", NixDevelopEnvironment.FlakeReference("/home/me/proj", "#dev"));
        Assert.Equal("github:org/repo#ci", NixDevelopEnvironment.FlakeReference("/home/me/proj", "github:org/repo#ci"));
    }

    [Fact]
    public void The_dev_shells_toolchain_is_merged_and_put_back_but_not_the_variables_that_describe_the_shell()
    {
        var env = new Dictionary<string, string?> { ["PATH"] = "/usr/bin", ["HOME"] = "/home/me", ["SHELL"] = "/bin/zsh" };
        var dev = new Dictionary<string, string>
        {
            ["PATH"] = "/nix/store/x-rust/bin:/usr/bin", ["RUST_SRC_PATH"] = "/nix/store/x-src", ["IN_NIX_SHELL"] = "impure",
            ["HOME"] = "/homeless-shelter", ["SHELL"] = "/nix/store/x-bash/bin/bash", ["TERM"] = "dumb", ["TMPDIR"] = "/build", ["PWD"] = "/x", ["CSCODE_HOME"] = "/elsewhere",
        };
        var overlay = new NixDevelopEnvironment.Overlay();
        void Set(string k, string? v) { if (v is null) env.Remove(k); else env[k] = v; }
        string? Get(string k) => env.GetValueOrDefault(k);
        Assert.Equal(3, overlay.Apply(dev, Get, Set));
        Assert.Equal("/nix/store/x-rust/bin:/usr/bin", env["PATH"]);
        Assert.Equal("/nix/store/x-src", env["RUST_SRC_PATH"]);
        Assert.Equal(("/home/me", "/bin/zsh"), (env["HOME"], env["SHELL"])); // the terminal keeps the user's shell and home
        Assert.DoesNotContain("TMPDIR", env.Keys);
        overlay.Revert(Set);
        Assert.Equal(["HOME", "PATH", "SHELL"], env.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("/usr/bin", env["PATH"]);
    }

    [Fact]
    public void The_environment_between_nix_develops_markers_is_read_whatever_the_shell_hook_prints()
    {
        var marker = "cscode-nix-1";
        var output = "shellHook says hello\n" + marker + "PATH=/nix/store/x/bin\0IN_NIX_SHELL=impure\0" + marker + "trailing";
        var env = LoginShellEnvironment.Parse(output, marker)!;
        Assert.Equal(("/nix/store/x/bin", "impure"), (env["PATH"], env["IN_NIX_SHELL"]));
    }

    [Fact]
    public async Task Waiting_for_the_environment_also_waits_for_the_nix_resolution()
    {
        using var _ = LoginShellEnvironment.PendForTests(Task.FromResult<LoginShellEnvironment.Resolution?>(null));
        var gate = new TaskCompletionSource();
        LoginShellEnvironment.Also(gate.Task);
        var waiting = LoginShellEnvironment.WaitAsync(Ct);
        await Task.Delay(100, Ct);
        Assert.False(waiting.IsCompleted);
        gate.SetResult();
        await waiting.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        // a failing resolution does not fault what waits
        var failing = Task.FromException(new InvalidOperationException("nix failed"));
        LoginShellEnvironment.Also(failing);
        await LoginShellEnvironment.WaitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }
}
