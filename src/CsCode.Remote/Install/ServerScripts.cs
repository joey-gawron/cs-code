namespace CsCode.Remote.Install;

/// <summary>
/// The fixed POSIX sh scripts run on the remote. They are constants: every variable input arrives as a positional argument (quoted by
/// <see cref="Ssh.PosixShell"/>) or on stdin, never spliced into the text. Hash arguments are additionally validated as hex by the caller.
/// </summary>
public static class ServerScripts
{
    /// <summary>Exit code of <see cref="Start"/> when no candidate install exists (stdout then carries the probe lines).</summary>
    public const int NotInstalledExitCode = 85;
    public const string NotInstalledMarker = "CSCODE-NOT-INSTALLED";
    public const string InstalledMarker = "CSCODE-INSTALLED";

    /// <summary>
    /// Exit codes of <see cref="Install"/>'s own refusals: no SHA-256 tool on the remote (3), the uploaded bundle's checksum is wrong (4), the bundle
    /// has no server in it (5). Uploading again gives the same answer. Any other failure (ssh's 255, a killed upload after the install timeout, a
    /// stopped container's docker or wsl.exe code, a full disk, <c>set -e</c>'s 1 or 2) may well pass on the next attempt.
    /// </summary>
    public static readonly IReadOnlySet<int> InstallRefusalExitCodes = new HashSet<int> { 3, 4, 5 };

    /// <summary>
    /// $1 install root ("" = ~/.cscode-server) · $2 data dir ("" = root/data) · $3 marker ("" = none) · $4 "force" to ignore existing installs ·
    /// $5 persistent session id ("" = the server ends with the connection) · $6 its grace period in seconds · $7… candidate hashes, preferred first.
    /// Execs the first installed server (touching its directory so cleanup keeps it), or prints <see cref="NotInstalledMarker"/> and the probe lines
    /// (os, arch, home, dotnet runtimes) and exits <see cref="NotInstalledExitCode"/>.
    /// </summary>
    public const string Start = """
        root="$1"; [ -n "$root" ] || root="$HOME/.cscode-server"
        data="$2"; [ -n "$data" ] || data="$root/data"
        marker="$3"; force="$4"; persist="$5"; grace="$6"
        shift 6
        if [ "$force" != "force" ]; then
          for h in "$@"; do
            if [ -x "$root/bin/$h/cscode-server" ]; then
              touch "$root/bin/$h" 2>/dev/null
              mkdir -p "$data" 2>/dev/null
              server="$root/bin/$h/cscode-server"
              set -- --stdio --data-dir "$data"
              if [ -n "$marker" ]; then set -- "$@" --marker "$marker"; fi
              if [ -n "$persist" ]; then set -- "$@" --persist "$persist" --grace "$grace"; fi
              exec "$server" "$@"
            fi
          done
        fi
        echo "CSCODE-NOT-INSTALLED"
        echo "os=$(uname -s 2>/dev/null)"
        echo "arch=$(uname -m 2>/dev/null)"
        echo "home=$HOME"
        echo "root=$root"
        if command -v dotnet >/dev/null 2>&1; then dotnet --list-runtimes 2>/dev/null | sed 's/^/dotnet=/'; fi
        exit 85
        """;

    /// <summary>
    /// $1 install root · $2 hash · $3 expected sha256 · $4 "force" to replace an existing install. Reads the tar.gz bundle from stdin into a private
    /// temp dir under the root, verifies the checksum, extracts, makes <c>cscode-server</c> executable and renames the directory into place
    /// (atomic on one file system; an existing install of the same hash is kept unless forced). Then removes older installs, keeping the
    /// two most recently used plus this one. Prints <see cref="InstalledMarker"/> on success.
    /// </summary>
    public const string Install = """
        set -eu
        root="$1"; [ -n "$root" ] || root="$HOME/.cscode-server"
        hash="$2"; sum="$3"; force="$4"
        umask 077
        mkdir -p "$root/bin"
        tmp=$(mktemp -d "$root/bin/.install-XXXXXX")
        trap 'rm -rf "$tmp"' EXIT INT TERM HUP
        cat > "$tmp/bundle.tar.gz"
        if command -v sha256sum >/dev/null 2>&1; then actual=$(sha256sum "$tmp/bundle.tar.gz" | cut -d' ' -f1)
        elif command -v shasum >/dev/null 2>&1; then actual=$(shasum -a 256 "$tmp/bundle.tar.gz" | cut -d' ' -f1)
        else echo "neither sha256sum nor shasum is available on the remote" >&2; exit 3
        fi
        if [ "$actual" != "$sum" ]; then echo "checksum mismatch: expected $sum, got $actual" >&2; exit 4; fi
        mkdir "$tmp/x"
        gzip -dc "$tmp/bundle.tar.gz" | tar -xf - -C "$tmp/x"
        [ -f "$tmp/x/cscode-server" ] || { echo "the bundle has no cscode-server" >&2; exit 5; }
        chmod 755 "$tmp/x" "$tmp/x/cscode-server"
        if [ -d "$root/bin/$hash" ] && [ "$force" = "force" ]; then rm -rf "$root/bin/$hash"; fi
        if [ ! -d "$root/bin/$hash" ]; then mv "$tmp/x" "$root/bin/$hash"; fi
        touch "$root/bin/$hash"
        n=0
        for d in $(ls -1t "$root/bin"); do
          case "$d" in *[!0-9a-f]*) continue ;; esac
          [ ${#d} -eq 16 ] || continue
          [ "$d" = "$hash" ] && continue
          n=$((n + 1))
          [ $n -le 2 ] || rm -rf "$root/bin/$d"
        done
        echo "CSCODE-INSTALLED $hash"
        """;
}

/// <summary>What the start script printed when nothing was installed.</summary>
public sealed record RemoteProbe(string Os, string Arch, string? Home, string? Root, IReadOnlyList<Version> DotnetRuntimes)
{
    /// <summary>The .NET runtime id for the remote's OS/arch, or null when unsupported.</summary>
    public string? Rid => (Os.ToLowerInvariant(), Arch.ToLowerInvariant()) switch
    {
        ("linux", "x86_64" or "amd64") => "linux-x64",
        ("linux", "aarch64" or "arm64") => "linux-arm64",
        ("linux", "armv7l" or "armv7") => "linux-arm",
        ("darwin", "x86_64") => "osx-x64",
        ("darwin", "arm64") => "osx-arm64",
        ("freebsd", "amd64" or "x86_64") => "freebsd-x64",
        _ => null,
    };

    public static RemoteProbe Parse(string output)
    {
        string os = "", arch = "";
        string? home = null, root = null;
        var runtimes = new List<Version>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq];
            var value = line[(eq + 1)..].Trim();
            switch (key)
            {
                case "os": os = value; break;
                case "arch": arch = value; break;
                case "home": home = value; break;
                case "root": root = value; break;
                case "dotnet":
                    // "Microsoft.NETCore.App 10.0.12 [/usr/share/dotnet/shared/Microsoft.NETCore.App]"
                    var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && parts[0] == "Microsoft.NETCore.App" && Version.TryParse(parts[1].Split('-')[0], out var v)) runtimes.Add(v);
                    break;
            }
        }
        return new RemoteProbe(os, arch, home, root, runtimes);
    }
}

public enum InstallAction { Upload, Unsupported }

public sealed record InstallPlan(InstallAction Action, ServerBundle? Bundle, string Reason);

/// <summary>Pure installer decision (unit-tested): which bundle to upload for what the remote reported.</summary>
public static class InstallDecision
{
    public static InstallPlan Decide(RemoteProbe probe, ServerBundleSet? bundles)
    {
        if (bundles is null || bundles.Bundles.Count == 0)
            return new InstallPlan(InstallAction.Unsupported, null, "No server bundles are available on this machine (build them with tools/publish-server.sh, or set remote.ssh.serverBundles).");
        if (string.IsNullOrEmpty(probe.Os))
            return new InstallPlan(InstallAction.Unsupported, null, "The remote did not report its operating system (is its login shell POSIX sh compatible? Windows remotes are not supported yet).");
        if (probe.Rid is { } rid && bundles.SelfContained(rid) is { } sc)
            return new InstallPlan(InstallAction.Upload, sc, $"self-contained {rid} bundle");
        if (bundles.FrameworkDependent is { } fd)
        {
            var need = Version.TryParse(fd.Runtime ?? "10.0", out var n) ? n : new Version(10, 0);
            if (probe.DotnetRuntimes.Any(v => v.Major == need.Major && v >= need))
                return new InstallPlan(InstallAction.Upload, fd, $"framework-dependent bundle on the remote's .NET {probe.DotnetRuntimes.Where(v => v.Major == need.Major).Max()}");
            return new InstallPlan(InstallAction.Unsupported, null,
                $"No server bundle for {probe.Os}/{probe.Arch}{(probe.Rid is null ? " (unsupported platform)" : $" ({probe.Rid})")} and the remote has no .NET {need.Major} runtime for the portable one.");
        }
        return new InstallPlan(InstallAction.Unsupported, null, $"No server bundle for {probe.Os}/{probe.Arch}{(probe.Rid is null ? " (unsupported platform)" : $" ({probe.Rid})")}.");
    }
}
