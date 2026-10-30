using CsCode.Sessions.Ipc;
using CsCode.Sessions.Launcher;
using Xunit;

namespace CsCode.Sessions.Tests;

/// <summary>The launcher decision table (docs/SESSIONS.md): one test per row.</summary>
public class LaunchPlannerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly LaunchSettings Defaults = new();
    /// <summary>The warm spare is off by default (docs/briefs/wave-3.md); the spare rows of the table turn it on.</summary>
    private static readonly LaunchSettings WithSpare = new() { WarmSpare = true };

    private static ResidentProcess Proc(int pid, string? session, string state, int shownMinutesAgo = 0, bool debugging = false, bool responsive = true) =>
        new(pid, $"/run/{pid}.sock", session, state, session is null ? null : T0.AddMinutes(-shownMinutesAgo), debugging, 100_000_000, responsive);

    private static SessionSummary Saved(string name, int usedMinutesAgo = 0, string? folder = null, bool wasResident = false) =>
        new(name, false, T0.AddMinutes(-usedMinutesAgo), folder is null ? [] : [folder], 1, wasResident);

    private static LaunchWorld World(IEnumerable<ResidentProcess>? procs = null, IEnumerable<SessionSummary>? saved = null, string? last = null, params string[] locked) =>
        new((procs ?? []).ToArray(), (saved ?? []).ToArray(), last, n => locked.Contains(n));

    private static LaunchRequest Open(string? session = null, bool newWindow = false, params LaunchTarget[] targets) =>
        new(LaunchKind.Open) { Session = session, NewWindow = newWindow, Targets = targets };

    [Fact]
    public void Resident_session_is_shown()
    {
        var plan = LaunchPlanner.Plan(Open("work"), World([Proc(10, "work", ProcessStates.Hidden), Proc(11, null, ProcessStates.Spare)]), WithSpare);
        Assert.Equal((LaunchAction.Show, 10, "work", false), (plan.Action, plan.Pid, plan.Session, plan.EnsureSpare));
    }

    [Fact]
    public void Non_resident_session_claims_the_warm_spare_and_a_new_spare_is_started()
    {
        var plan = LaunchPlanner.Plan(Open("work"), World([Proc(11, null, ProcessStates.Spare)], [Saved("work")]), WithSpare);
        Assert.Equal((LaunchAction.Claim, 11, "work", true, false), (plan.Action, plan.Pid, plan.Session, plan.EnsureSpare, plan.NewUnnamed));
    }

    [Fact]
    public void By_default_no_spare_is_started_but_one_that_runs_is_still_claimed()
    {
        Assert.False(Defaults.WarmSpare);
        Assert.False(LaunchPlanner.Plan(Open("work"), World(), Defaults).EnsureSpare);
        Assert.False(LaunchPlanner.Plan(Open("work"), World([Proc(10, "work", ProcessStates.Hidden)]), Defaults).EnsureSpare);
        Assert.False(LaunchPlanner.Plan(new LaunchRequest(LaunchKind.Background), World(), Defaults).EnsureSpare);
        // A spare left from when the setting was on is used (and not replaced).
        var claim = LaunchPlanner.Plan(Open("work"), World([Proc(11, null, ProcessStates.Spare)]), Defaults);
        Assert.Equal((LaunchAction.Claim, 11, false), (claim.Action, claim.Pid, claim.EnsureSpare));
    }

    [Fact]
    public void Cold_start_when_there_is_no_spare_also_starts_one()
    {
        var plan = LaunchPlanner.Plan(Open("work"), World(), WithSpare);
        Assert.Equal((LaunchAction.Start, "work", true), (plan.Action, plan.Session, plan.EnsureSpare));
        var noSpare = LaunchPlanner.Plan(Open("work"), World(), WithSpare with { WarmSpare = false });
        Assert.False(noSpare.EnsureSpare);
    }

    [Fact]
    public void A_spare_that_is_still_starting_is_not_claimed_and_not_duplicated()
    {
        var starting = Proc(11, null, ProcessStates.Starting);
        var start = LaunchPlanner.Plan(Open("work"), World([starting]), WithSpare);
        Assert.Equal((LaunchAction.Start, false), (start.Action, start.EnsureSpare));
        var show = LaunchPlanner.Plan(Open("work"), World([starting, Proc(10, "work", ProcessStates.Hidden)]), WithSpare);
        Assert.Equal((LaunchAction.Show, false), (show.Action, show.EnsureSpare));
        Assert.False(LaunchPlanner.Plan(new LaunchRequest(LaunchKind.Background), World([starting]), WithSpare).EnsureSpare);
    }

    [Fact]
    public void Unresponsive_processes_are_never_targets()
    {
        // A spare that does not answer is not claimed; a stale owner (gone from the list) means the session is free.
        var plan = LaunchPlanner.Plan(Open("work"), World([Proc(11, null, ProcessStates.Spare, responsive: false)]), Defaults);
        Assert.Equal(LaunchAction.Start, plan.Action);
    }

    [Fact]
    public void A_locked_session_without_a_responsive_owner_opens_a_copy_with_a_warning()
    {
        var plan = LaunchPlanner.Plan(Open("work"), World([Proc(11, null, ProcessStates.Spare), Proc(12, null, ProcessStates.Starting, responsive: false)], [Saved("work")], null, "work"), Defaults);
        Assert.Equal(LaunchAction.Start, plan.Action);
        Assert.Equal("work", plan.SeedFrom);
        Assert.Equal("window-1", plan.Session);
        Assert.True(plan.NewUnnamed);
        Assert.Contains(plan.Warnings, w => w.Contains("does not respond"));
    }

    [Fact]
    public void New_window_is_a_new_unnamed_session()
    {
        var plan = LaunchPlanner.Plan(Open(newWindow: true), World([Proc(10, "window-1", ProcessStates.Shown)], [Saved("window-2")]), Defaults);
        Assert.Equal(("window-3", true), (plan.Session, plan.NewUnnamed));
    }

    [Fact]
    public void A_folder_reopens_the_session_that_has_it_else_gets_a_new_one()
    {
        var folder = Path.Combine(Path.GetTempPath(), "proj");
        var uri = LaunchPlanner.FolderUri(folder);
        var world = World([], [Saved("other", 1, "file:///elsewhere/"), Saved("proj-session", 5, uri), Saved("older-proj", 50, uri)]);
        var plan = LaunchPlanner.Plan(Open(targets: new LaunchTarget(folder, true)), world, Defaults);
        Assert.Equal(("proj-session", false), (plan.Session, plan.NewUnnamed));

        var fresh = LaunchPlanner.Plan(Open(targets: new LaunchTarget(Path.Combine(Path.GetTempPath(), "new-proj"), true)), world, Defaults);
        Assert.True(fresh.NewUnnamed);
    }

    [Fact]
    public void Files_only_go_to_the_last_active_session()
    {
        var world = World([Proc(10, "a", ProcessStates.Hidden), Proc(11, "b", ProcessStates.Hidden)], [Saved("a"), Saved("b")], last: "b");
        var plan = LaunchPlanner.Plan(Open(targets: new LaunchTarget("/x/f.cs", false, 3, 4)), world, Defaults);
        Assert.Equal((LaunchAction.Show, 11), (plan.Action, plan.Pid));
    }

    [Theory]
    [InlineData("last", "b")]
    [InlineData("a", "a")]
    public void No_arguments_follow_the_default_session_setting(string setting, string expected)
    {
        var world = World([], [Saved("a", 1), Saved("b", 10)], last: "b");
        Assert.Equal(expected, LaunchPlanner.Plan(Open(), world, Defaults with { DefaultSession = setting }).Session);
    }

    [Fact]
    public void Default_new_and_a_first_run_give_a_new_unnamed_session()
    {
        Assert.True(LaunchPlanner.Plan(Open(), World([], [Saved("a")], last: "a"), Defaults with { DefaultSession = "new" }).NewUnnamed);
        var first = LaunchPlanner.Plan(Open(), World(), Defaults);
        Assert.Equal(("window-1", true), (first.Session, first.NewUnnamed));
    }

    [Fact]
    public void Without_a_last_active_marker_the_most_recently_shown_resident_wins()
    {
        var world = World([Proc(10, "a", ProcessStates.Hidden, 30), Proc(11, "b", ProcessStates.Hidden, 2)], [Saved("c")]);
        Assert.Equal("b", LaunchPlanner.Plan(Open(), world, Defaults).Session);
    }

    [Fact]
    public void An_invalid_session_name_fails()
    {
        var plan = LaunchPlanner.Plan(Open("../etc"), World(), Defaults);
        Assert.Equal(LaunchAction.Fail, plan.Action);
        Assert.Contains("Invalid session name", plan.Error);
    }

    [Fact]
    public void Opening_a_session_beyond_the_maximum_evicts_the_least_recently_shown_hidden_one()
    {
        var procs = new[]
        {
            Proc(1, "a", ProcessStates.Hidden, shownMinutesAgo: 50),
            Proc(2, "b", ProcessStates.Hidden, shownMinutesAgo: 90, debugging: true), // un-evictable
            Proc(3, "c", ProcessStates.Shown, shownMinutesAgo: 120),                 // shown: never evicted
            Proc(4, null, ProcessStates.Spare),
        };
        var plan = LaunchPlanner.Plan(Open("d"), World(procs), Defaults with { MaxResidentSessions = 3 });
        Assert.Equal(LaunchAction.Claim, plan.Action);
        Assert.Equal([1], plan.Evict);

        var show = LaunchPlanner.Plan(Open("a"), World(procs), Defaults with { MaxResidentSessions = 2 });
        Assert.Equal(LaunchAction.Show, show.Action);
        Assert.Empty(show.Evict); // a is kept (being shown), b debugs, c is shown
    }

    [Fact]
    public void Resident_policy_orders_by_last_shown_and_ignores_spares_and_quitting()
    {
        var procs = new[]
        {
            Proc(1, "a", ProcessStates.Hidden, 10), Proc(2, "b", ProcessStates.Hidden, 30), Proc(3, "c", ProcessStates.Hidden, 20),
            Proc(4, null, ProcessStates.Spare), Proc(5, "q", ProcessStates.Quitting, 99),
        };
        Assert.Equal([2, 3], ResidentPolicy.SelectEvictions(procs, 1, keepPid: null));
        Assert.Equal([2], ResidentPolicy.SelectEvictions(procs, 2, keepPid: null));
        Assert.Empty(ResidentPolicy.SelectEvictions(procs, 3, keepPid: null));
        Assert.Equal([3, 1], ResidentPolicy.SelectEvictions(procs, 1, keepPid: 2));
    }

    [Fact]
    public void Quit_all_targets_every_responsive_process_and_warns_about_the_rest()
    {
        var plan = LaunchPlanner.Plan(new LaunchRequest(LaunchKind.Quit) { All = true },
            World([Proc(1, "a", ProcessStates.Shown), Proc(2, null, ProcessStates.Spare), Proc(3, null, ProcessStates.Starting, responsive: false)]), Defaults);
        Assert.Equal([1, 2], plan.QuitPids);
        Assert.Contains(plan.Warnings, w => w.Contains("3"));
    }

    [Fact]
    public void Quit_by_name_and_quit_of_the_last_session()
    {
        var world = World([Proc(1, "a", ProcessStates.Shown), Proc(2, "b", ProcessStates.Hidden)], [], last: "b");
        Assert.Equal([1], LaunchPlanner.Plan(new LaunchRequest(LaunchKind.Quit) { Session = "a" }, world, Defaults).QuitPids);
        Assert.Equal([2], LaunchPlanner.Plan(new LaunchRequest(LaunchKind.Quit), world, Defaults).QuitPids);
        var notRunning = LaunchPlanner.Plan(new LaunchRequest(LaunchKind.Quit) { Session = "zzz" }, world, Defaults);
        Assert.Empty(notRunning.QuitPids);
        Assert.Contains(notRunning.Warnings, w => w.Contains("not running"));
    }

    [Fact]
    public void Background_ensures_the_spare_and_with_restore_on_login_starts_the_resident_set_hidden()
    {
        var saved = new[] { Saved("a", 5, wasResident: true), Saved("b", 1, wasResident: true), Saved("c", 2), Saved("d", 9, wasResident: true), Saved("e", 3, wasResident: true) };
        var world = World([Proc(1, "e", ProcessStates.Hidden)], saved);

        var plain = LaunchPlanner.Plan(new LaunchRequest(LaunchKind.Background), world, WithSpare);
        Assert.True(plain.EnsureSpare);
        Assert.Empty(plain.StartHidden);

        var restore = LaunchPlanner.Plan(new LaunchRequest(LaunchKind.Background), world, Defaults with { RestoreOnLogin = true, MaxResidentSessions = 3 });
        Assert.Equal(["b", "a"], restore.StartHidden); // most recent first, e already resident, capped at 3 resident
        Assert.False(restore.EnsureSpare); // the spare is off by default

        var withSpare = LaunchPlanner.Plan(new LaunchRequest(LaunchKind.Background), World([Proc(2, null, ProcessStates.Spare)]), WithSpare);
        Assert.False(withSpare.EnsureSpare);
    }
}
