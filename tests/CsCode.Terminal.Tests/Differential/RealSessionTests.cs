using CsCode.Terminal.Tests.Support;
using Xunit;

namespace CsCode.Terminal.Tests.Differential;

/// <summary>
/// Real sessions, recorded on a real PTY at test time (bash with a coloured prompt and <c>ls --color</c>, <c>git log</c> through less, nvim
/// editing a file, top in batch and interactive mode, the Python REPL, a progress bar drawn with <c>\r</c>), replayed into CsCode's emulator,
/// tmux and libvterm; the final screens must agree. Every program runs in the sandbox (temp HOME and XDG dirs, no rc files).
/// </summary>
[Collection(SerialCollection.Name)]
public sealed class RealSessionTests : IDisposable
{
    private readonly ShellSandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    public static TheoryData<string> Sessions() => new(["bash-ls-color", "git-log-less", "nvim-edit", "top-batch", "top-interactive", "python-repl", "progress-bar"]);

    private async Task<byte[]> Record(string name)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "real sessions are recorded on the Linux PTY host");
        var (bytes, missing) = await RealSessions.RecordAsync(name, _sandbox);
        Assert.SkipWhen(bytes is null, missing ?? "missing");
        Assert.True(bytes!.Length > 0, "nothing was recorded");
        return bytes;
    }

    [Theory]
    [MemberData(nameof(Sessions))]
    public async Task Matches_tmux(string name)
    {
        Assert.SkipUnless(TmuxOracle.Executable is not null, "tmux is not installed");
        var bytes = await Record(name);
        using var tmux = new TmuxOracle();
        DifferentialTests.AssertSame(tmux, "real:" + name, bytes, RealSessions.Columns, RealSessions.Rows);
    }

    [Theory]
    [MemberData(nameof(Sessions))]
    public async Task Matches_libvterm(string name)
    {
        Assert.SkipUnless(LibVtermOracle.IsAvailable, "libvterm is not available");
        var bytes = await Record(name);
        using var vterm = new LibVtermOracle();
        DifferentialTests.AssertSame(vterm, "real:" + name, bytes, RealSessions.Columns, RealSessions.Rows);
    }
}
