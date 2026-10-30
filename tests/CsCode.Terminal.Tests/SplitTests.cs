using System.Diagnostics;
using CsCode.Core.Terminal;
using CsCode.Terminal.Sessions;
using CsCode.Terminal.Tests.Support;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Terminal.Tests;

/// <summary>
/// Terminal groups (VS Code's split terminals) in the service over real sandboxed shells: a split joins its parent's group right of it and
/// becomes active, starts in the parent's current directory (splitCwd), kill closes a pane and activates its neighbour, the last pane closes
/// the group, unsplit and join move terminals between groups.
/// </summary>
[Collection(SerialCollection.Name)]
public sealed class SplitTests : IDisposable
{
    private readonly ShellSandbox _sandbox = new();
    private readonly TestConfigurationService _config = new();
    private readonly TerminalService _service;

    public SplitTests()
    {
        _config.Set(TerminalSettingKeys.Prefix + "shellIntegration.enabled", false);
        _service = new TerminalService(_config, environment: () => _sandbox.Environment());
    }

    public void Dispose()
    {
        _service.Dispose();
        _sandbox.Dispose();
    }

    private TerminalCreateOptions Bash(string? name = null) => new()
    {
        Name = name,
        ShellPath = ShellSandbox.Bash,
        ShellArgs = ["--noprofile", "--norc"],
        Cwd = _sandbox.Home,
        Show = false,
    };

    private static async Task<string> WaitScreenAsync(TerminalSession s, string text, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        var screen = "";
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            screen = await s.InvokeAsync(e => e.ScreenText());
            if (screen.Contains(text, StringComparison.Ordinal)) return screen;
            await Task.Delay(20);
        }
        Assert.Fail($"'{text}' did not appear on the screen:\n{screen}");
        return screen;
    }

    [Fact]
    public async Task A_split_joins_the_parents_group_right_of_it_and_becomes_active()
    {
        var groupsChanged = 0;
        using var sub = _service.GroupsChanged.Subscribe(_ => Interlocked.Increment(ref groupsChanged));
        var first = (TerminalSession)await _service.CreateAsync(Bash("a"));
        var second = (TerminalSession)await _service.CreateAsync(Bash("b"));
        _service.SetActive(first);
        var split = await _service.SplitAsync(first);

        Assert.Equal(2, _service.Groups.Count);
        var group = _service.Groups[0];
        Assert.Equal([first, split], group.Terminals);
        Assert.True(group.IsSplit);
        Assert.Same(split, _service.ActiveSession);
        Assert.Same(group, _service.ActiveGroup);
        Assert.Same(split, group.ActiveTerminal);
        Assert.Equal([first, split, second], _service.Sessions); // group order
        Assert.Equal("bash", Path.GetFileName(split.Options.ShellPath)); // the parent's program
        Assert.True(groupsChanged >= 3);
        await split.SendTextAsync("echo split-$((20+22))");
        await WaitScreenAsync(split, "split-42");
    }

    [Fact]
    public async Task A_split_starts_in_the_directory_the_parent_shell_is_in()
    {
        var work = Directory.CreateDirectory(_sandbox.Dir.Combine("work dir")).FullName;
        var first = (TerminalSession)await _service.CreateAsync(Bash());
        await first.SendTextAsync($"cd '{work}' && echo moved");
        await WaitScreenAsync(first, "moved");
        var split = await _service.SplitAsync(first);
        await split.SendTextAsync("pwd");
        await WaitScreenAsync(split, work);

        _config.Set(TerminalSettingKeys.SplitCwd, "initial");
        var initial = await _service.SplitAsync(first);
        await initial.SendTextAsync("echo \"[$PWD]\"");
        await WaitScreenAsync(initial, $"[{_sandbox.Home}]");
    }

    [Fact]
    public async Task Kill_closes_a_pane_and_activates_its_neighbour_and_the_last_pane_closes_the_group()
    {
        var a = (TerminalSession)await _service.CreateAsync(Bash("a"));
        var b = await _service.SplitAsync(a);
        var c = await _service.SplitAsync(b);
        var other = (TerminalSession)await _service.CreateAsync(Bash("other"));
        _service.SetActive(b);
        Assert.Equal([a, b, c], _service.Groups[0].Terminals);

        var actives = new List<ITerminal?>();
        using var sub = _service.ActiveChanged.Subscribe(actives.Add);
        await _service.KillAsync(b); // the middle pane: the one on its left takes over
        Assert.Equal([a, c], _service.Groups[0].Terminals);
        Assert.Same(a, _service.ActiveSession);
        Assert.Same(a, Assert.Single(actives));
        await _service.KillAsync(a); // the left pane: the right one takes over
        Assert.Same(c, _service.ActiveSession);
        Assert.False(_service.Groups[0].IsSplit);

        await _service.KillAsync(c); // the group's last pane: the group goes, the next group is shown
        Assert.Same(other, Assert.Single(_service.Groups).ActiveTerminal);
        Assert.Same(other, _service.ActiveSession);
        Assert.Equal([other], _service.Sessions);
    }

    [Fact]
    public async Task Killing_an_inactive_pane_keeps_the_active_terminal()
    {
        var a = (TerminalSession)await _service.CreateAsync(Bash("a"));
        var b = await _service.SplitAsync(a);
        var other = (TerminalSession)await _service.CreateAsync(Bash("other"));
        _service.SetActive(other);
        var actives = new List<ITerminal?>();
        using var sub = _service.ActiveChanged.Subscribe(actives.Add);
        await _service.KillAsync(b);
        Assert.Same(other, _service.ActiveSession);
        Assert.Empty(actives);
        Assert.Same(a, _service.Groups[0].ActiveTerminal); // the group remembers a pane that still exists
    }

    [Fact]
    public async Task Unsplit_and_join_move_terminals_between_groups()
    {
        var a = (TerminalSession)await _service.CreateAsync(Bash("a"));
        var b = await _service.SplitAsync(a);
        var c = (TerminalSession)await _service.CreateAsync(Bash("c"));
        _service.Unsplit(b);
        Assert.Equal(3, _service.Groups.Count);
        Assert.Equal([a], _service.Groups[0].Terminals);
        Assert.Equal([b], _service.Groups[1].Terminals); // right after the group it left
        Assert.Equal([c], _service.Groups[2].Terminals);

        _service.Join(a, [b, c]);
        var group = Assert.Single(_service.Groups);
        Assert.Equal([a, b, c], group.Terminals);
        _service.SetActive(c);
        Assert.Same(c, group.ActiveTerminal);
    }

    [Fact]
    public async Task A_split_of_a_debuggee_terminal_runs_the_default_profile()
    {
        _config.Set(TerminalSettingKeys.DefaultProfileLinux, "sandboxed");
        _config.Set(TerminalSettingKeys.ProfilesLinux, new Dictionary<string, object>
        {
            ["sandboxed"] = new { path = ShellSandbox.Bash, args = new[] { "--noprofile", "--norc" } },
        });
        var debuggee = (TerminalSession)await _service.CreateAsync(new TerminalCreateOptions
        {
            Name = "debuggee", ShellPath = ShellSandbox.Sh, ShellArgs = ["-c", "echo debuggee-ran; sleep 30"], IsTransient = true, Show = false,
        });
        await WaitScreenAsync(debuggee, "debuggee-ran");
        var split = await _service.SplitAsync(debuggee);
        Assert.False(split.IsTransient);
        Assert.Null(split.Options.ShellPath); // the default profile, not the debuggee's program
        await split.SendTextAsync("echo shell-$((1+1))");
        await WaitScreenAsync(split, "shell-2");
    }
}
