using CsCode.Core.Lifecycle;

namespace CsCode.Core.Remote;

// Additive Core contract added by the Remote module (docs/REMOTE.md, "Remote machine settings"). Implemented by CsCode.Remote; consumed by the
// Settings page (its Remote tab) and the Open Remote Settings commands.

/// <summary>
/// The connected remote host's own machine settings file (VS Code's "Remote [SSH: host]" settings): <c>&lt;server data dir&gt;/machine/settings.json</c>
/// on that host. In a remote window the server layers it over the client's user settings: machine-scoped settings (the programs, paths and
/// environments it runs) come only from it, application-scoped ones never do, everything else is user &lt; this file &lt; workspace.
/// Safe from any thread; <see cref="Changed"/> fires on background threads.
/// </summary>
public interface IRemoteMachineSettings
{
    /// <summary>The file on the connected host (<c>ssh://me@box/home/me/.cscode-server/data/machine/settings.json</c>); null when no host is connected.</summary>
    Uri? SettingsUri { get; }

    /// <summary>"SSH: box", "Docker: web", "WSL: Ubuntu" while connected (the Settings tab reads "Remote [SSH: box]"); null otherwise.</summary>
    string? HostLabel { get; }

    /// <summary>Fires when <see cref="SettingsUri"/> changes (connected, disconnected, another host) and when the file changed (a write through
    /// <see cref="UpdateAsync"/>, an edit in the editor, a change made on the host).</summary>
    IEventSource<Uri?> Changed { get; }

    /// <summary>
    /// Sets <paramref name="key"/> to <paramref name="value"/> in the file (null removes it). The remote server writes it with the same comment-preserving
    /// editor as the user settings.json and applies it at once. Throws <see cref="IOException"/> when no host is connected and
    /// <see cref="InvalidOperationException"/> for an application-scoped setting, which a machine settings file cannot set.
    /// </summary>
    ValueTask UpdateAsync(string key, object? value, CancellationToken ct = default);
}
