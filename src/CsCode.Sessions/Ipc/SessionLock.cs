namespace CsCode.Sessions.Ipc;

/// <summary>
/// What makes a process the owner (the only writer) of a session: <see cref="SessionStore.LockPathOf"/>, next to the session's data. Builds before
/// the lock moved there locked <see cref="RuntimeDirectory.LegacySessionLockPath"/> instead, so during an upgrade a process of such a build can
/// still be running (quitting, wedged, or simply resident) with only that lock: it is taken and checked too. A process of this build therefore
/// never starts a second writer next to an older one, and an older one (its own Switch Session, or a host it starts) finds this one's sessions
/// taken. The launcher lock has no such twin: an older launcher only ever runs inside an older process, and the session locks alone already rule
/// out a second writer.
/// </summary>
public sealed class SessionLock : IDisposable
{
    private readonly FileLock _lock;
    private readonly FileLock? _legacy;

    private SessionLock(FileLock @lock, FileLock? legacy)
    {
        _lock = @lock;
        _legacy = legacy;
    }

    /// <summary>
    /// Takes session <paramref name="name"/>'s lock; null when another process holds it (or its older-build twin). Throws <see cref="IOException"/>
    /// when the lock cannot be taken at all (the data directory cannot be written): waiting or another name would not help.
    /// </summary>
    public static SessionLock? TryAcquire(SessionStore store, RuntimeDirectory runtime, string name)
    {
        var held = FileLock.TryAcquire(store.LockPathOf(name), out var problem);
        if (held is null) return problem is null ? null : throw new IOException(problem);
        // The older build's lock: held there means an older process owns the session. It cannot be taken at all only when the runtime directory
        // is unusable, and then no process of any build can be reached there either: the lock in the data directory decides alone.
        var legacy = FileLock.TryAcquire(runtime.LegacySessionLockPath(name), out var legacyProblem);
        if (legacy is null && legacyProblem is null)
        {
            held.Dispose();
            return null;
        }
        return new SessionLock(held, legacy);
    }

    /// <summary>True when another process owns session <paramref name="name"/> (its lock, or the older-build one, is held).</summary>
    public static bool IsHeldElsewhere(SessionStore store, RuntimeDirectory runtime, string name) =>
        FileLock.IsHeldElsewhere(store.LockPathOf(name)) || FileLock.IsHeldElsewhere(runtime.LegacySessionLockPath(name));

    public void Dispose()
    {
        _legacy?.Dispose();
        _lock.Dispose();
    }
}
