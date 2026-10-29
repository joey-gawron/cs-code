using CsCode.Sessions.Ipc;

namespace CsCode.Sessions.Launcher;

/// <summary>
/// The launcher's decision table (pure: the same world and request always give the same plan; docs/SESSIONS.md has the table).
/// <list type="number">
/// <item>Which session: <c>--session NAME</c>; <c>--new-window</c> → a new unnamed one; a folder → the saved session whose primary folder it is,
/// else a new unnamed one; files only or nothing → <c>cscode.sessions.default</c> (<c>last</c>: the session shown last; <c>new</c>; or a name).</item>
/// <item>A responsive process owns it → <see cref="LaunchAction.Show"/>.</item>
/// <item>Its lock is held but no process answers (wedged) → <see cref="LaunchAction.Start"/> a new unnamed session seeded from it, with a warning.</item>
/// <item>A warm spare answers → <see cref="LaunchAction.Claim"/> (and a new spare is started).</item>
/// <item>Otherwise → <see cref="LaunchAction.Start"/> a session process (and a spare, if none exists).</item>
/// <item>Afterwards, hidden sessions beyond <c>maxResidentSessions</c> are quit, least recently shown first (<see cref="ResidentPolicy"/>).</item>
/// </list>
/// </summary>
public static class LaunchPlanner
{
    public static LaunchPlan Plan(LaunchRequest request, LaunchWorld world, LaunchSettings settings) => request.Kind switch
    {
        LaunchKind.ListSessions => new LaunchPlan(LaunchAction.List),
        LaunchKind.Quit => PlanQuit(request, world),
        LaunchKind.Background => PlanBackground(world, settings),
        _ => PlanOpen(request, world, settings),
    };

    private static LaunchPlan PlanOpen(LaunchRequest request, LaunchWorld world, LaunchSettings settings)
    {
        if (request.Session is { } explicitName && SessionNames.Validate(explicitName) is { } invalid)
            return new LaunchPlan(LaunchAction.Fail) { Error = $"Invalid session name '{explicitName}': {invalid}" };

        var (name, isNew) = ResolveSession(request, world, settings);
        var warnings = new List<string>();
        var spares = world.Processes.Where(p => p.IsSpare).ToArray();

        if (world.Processes.FirstOrDefault(p => p.OwnsSession(name)) is { } owner)
        {
            return new LaunchPlan(LaunchAction.Show)
            {
                Session = name, Pid = owner.Pid, EnsureSpare = settings.WarmSpare && spares.Length == 0,
                Evict = ResidentPolicy.SelectEvictions(world.Processes, settings.MaxResidentSessions, keepPid: owner.Pid),
            };
        }

        string? seedFrom = null;
        if (!isNew && world.IsSessionLocked(name))
        {
            // Its owner holds the lock but does not answer: never wait for it, never steal its session. Open a copy instead.
            seedFrom = name;
            var copy = SessionNames.NextUnnamed(TakenNames(world));
            warnings.Add($"Session '{name}' is held by a CsCode process that does not respond; opened a copy of it as '{copy}'.");
            name = copy;
            isNew = true;
        }

        if (seedFrom is null && spares.FirstOrDefault() is { } spare)
        {
            return new LaunchPlan(LaunchAction.Claim)
            {
                Session = name, Pid = spare.Pid, NewUnnamed = isNew, EnsureSpare = settings.WarmSpare && spares.Length <= 1, Warnings = warnings,
                Evict = ResidentPolicy.SelectEvictions(world.Processes.Where(p => p.Pid != spare.Pid), settings.MaxResidentSessions - 1, keepPid: null),
            };
        }

        return new LaunchPlan(LaunchAction.Start)
        {
            Session = name, SeedFrom = seedFrom, NewUnnamed = isNew, EnsureSpare = settings.WarmSpare && spares.Length == 0, Warnings = warnings,
            Evict = ResidentPolicy.SelectEvictions(world.Processes, settings.MaxResidentSessions - 1, keepPid: null),
        };
    }

    /// <summary>The session a request means, and whether it is a new unnamed one.</summary>
    internal static (string Name, bool IsNew) ResolveSession(LaunchRequest request, LaunchWorld world, LaunchSettings settings)
    {
        if (request.Session is { } explicitName) return (explicitName, false);
        if (request.NewWindow) return (SessionNames.NextUnnamed(TakenNames(world)), true);

        if (request.Targets.FirstOrDefault(t => t.IsDirectory) is { } folder)
        {
            var uri = FolderUri(folder.Path);
            var match = world.Saved
                .Where(s => s.Folders.Count > 0 && SameFolder(s.Folders[0], uri))
                .OrderByDescending(s => world.Processes.Any(p => p.OwnsSession(s.Name)))
                .ThenByDescending(s => s.LastUsedUtc)
                .FirstOrDefault();
            return match is not null ? (match.Name, false) : (SessionNames.NextUnnamed(TakenNames(world)), true);
        }

        var preference = settings.DefaultSession;
        if (string.Equals(preference, "new", StringComparison.Ordinal)) return (SessionNames.NextUnnamed(TakenNames(world)), true);
        if (!string.Equals(preference, "last", StringComparison.Ordinal) && SessionNames.IsValid(preference)) return (preference, false);
        // Residents first: when the last session is running, the saved sessions are never read (the launcher reads them lazily).
        if (world.LastActive is { } last && (world.Processes.Any(p => p.OwnsSession(last)) || world.Saved.Any(s => s.Name == last))) return (last, false);
        // No last session yet: the most recently shown resident one, else the most recently used saved one, else a new one.
        if (world.Processes.Where(p => p.Responsive && p.Session is not null && p.State != ProcessStates.Quitting)
                .OrderByDescending(p => p.LastShownUtc ?? DateTimeOffset.MinValue).FirstOrDefault() is { Session: { } resident })
            return (resident, false);
        if (world.Saved.FirstOrDefault() is { } recent) return (recent.Name, false);
        return (SessionNames.NextUnnamed(TakenNames(world)), true);
    }

    private static IEnumerable<string> TakenNames(LaunchWorld world) =>
        world.Saved.Select(s => s.Name).Concat(world.Processes.Select(p => p.Session).OfType<string>());

    /// <summary>The URI form sessions store folders in (<c>file:///path/</c>).</summary>
    public static string FolderUri(string path) => new Uri(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + "/").ToString();

    private static bool SameFolder(string a, string b) => string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private static LaunchPlan PlanQuit(LaunchRequest request, LaunchWorld world)
    {
        if (request.All)
        {
            var warnings = world.Processes.Where(p => !p.Responsive)
                .Select(p => $"CsCode process {p.Pid} does not respond; it was not asked to quit.").ToArray();
            return new LaunchPlan(LaunchAction.Quit) { QuitPids = world.Processes.Where(p => p.Responsive).Select(p => p.Pid).ToArray(), Warnings = warnings };
        }
        var name = request.Session ?? world.LastActive;
        if (name is null) return new LaunchPlan(LaunchAction.Quit) { Warnings = ["No session to quit (name one, or use --all)."] };
        var owner = world.Processes.FirstOrDefault(p => p.OwnsSession(name));
        return owner is null
            ? new LaunchPlan(LaunchAction.Quit) { Session = name, Warnings = [$"Session '{name}' is not running."] }
            : new LaunchPlan(LaunchAction.Quit) { Session = name, QuitPids = [owner.Pid] };
    }

    private static LaunchPlan PlanBackground(LaunchWorld world, LaunchSettings settings)
    {
        var start = new List<string>();
        if (settings.RestoreOnLogin)
        {
            var resident = world.Processes.Count(p => p.Responsive && p.Session is not null);
            foreach (var s in world.Saved.Where(s => s.WasResident).OrderByDescending(s => s.LastUsedUtc))
            {
                if (resident + start.Count >= settings.MaxResidentSessions) break;
                if (world.Processes.Any(p => p.OwnsSession(s.Name)) || world.IsSessionLocked(s.Name)) continue;
                start.Add(s.Name);
            }
        }
        return new LaunchPlan(LaunchAction.Background) { EnsureSpare = settings.WarmSpare && !world.Processes.Any(p => p.IsSpare), StartHidden = start };
    }
}

/// <summary>
/// Which hidden sessions to quit so that at most <c>maxResidentSessions</c> stay resident: least recently shown first; never a shown window, a
/// session with a running debug session (un-evictable), the warm spare (it does not count), or the session being opened.
/// </summary>
public static class ResidentPolicy
{
    public static IReadOnlyList<int> SelectEvictions(IEnumerable<ResidentProcess> processes, int maxResident, int? keepPid)
    {
        var residents = processes.Where(p => p.Responsive && p.Session is not null && p.State is not ProcessStates.Quitting and not ProcessStates.Spare).ToArray();
        var excess = residents.Length - Math.Max(0, maxResident);
        if (excess <= 0) return [];
        return residents
            .Where(p => p.State == ProcessStates.Hidden && !p.Debugging && p.Pid != keepPid)
            .OrderBy(p => p.LastShownUtc ?? DateTimeOffset.MinValue)
            .ThenBy(p => p.Pid)
            .Take(excess)
            .Select(p => p.Pid)
            .ToArray();
    }
}
