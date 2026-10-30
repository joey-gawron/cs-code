namespace CsCode.Server;

/// <summary>
/// <c>cscode-server [--stdio] [--data-dir DIR] [--marker TAG] [--persist ID [--grace SECONDS]] [--version] [--askpass PROMPT]</c>; internal:
/// <c>--daemon ID</c> (the persistent server a <c>--persist</c> relay starts).
/// </summary>
public sealed record ServerOptions(bool Stdio, string DataDir, string? Marker, bool ShowVersion, string? AskpassPrompt, bool Askpass)
{
    /// <summary>Default time a persistent server waits for its client to come back (VS Code's reconnection grace time).</summary>
    public const int DefaultGraceSeconds = 3 * 60 * 60;

    /// <summary><c>--persist ID</c>: relay stdio to the persistent server of session ID (starting it when it is not running).</summary>
    public string? Persist { get; init; }

    /// <summary><c>--daemon ID</c>: be the persistent server of session ID (listening on its socket).</summary>
    public string? Daemon { get; init; }

    /// <summary><c>--grace SECONDS</c>: a persistent server with no client for this long exits.</summary>
    public int GraceSeconds { get; init; } = DefaultGraceSeconds;

    public static ServerOptions Parse(IReadOnlyList<string> args)
    {
        bool stdio = true, version = false, askpass = false;
        string? dataDir = null, marker = null, prompt = null, persist = null, daemon = null;
        var grace = DefaultGraceSeconds;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--stdio": stdio = true; break;
                case "--version": version = true; break;
                case "--data-dir" when i + 1 < args.Count: dataDir = args[++i]; break;
                case "--marker" when i + 1 < args.Count: marker = args[++i]; break;
                case "--persist" when i + 1 < args.Count: persist = PersistencePaths.ValidSession(args[++i]); break;
                case "--daemon" when i + 1 < args.Count: daemon = PersistencePaths.ValidSession(args[++i]); break;
                case "--grace" when i + 1 < args.Count:
                    if (!int.TryParse(args[++i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out grace) || grace < 1)
                        throw new ArgumentException($"Invalid grace period '{args[i]}'");
                    break;
                case "--askpass":
                    askpass = true;
                    prompt = i + 1 < args.Count ? string.Join(' ', args.Skip(i + 1)) : "";
                    i = args.Count;
                    break;
                default: throw new ArgumentException($"Unknown argument '{args[i]}'");
            }
        }
        dataDir ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cscode-server", "data");
        return new ServerOptions(stdio, Path.GetFullPath(dataDir), marker, version, prompt, askpass) { Persist = persist, Daemon = daemon, GraceSeconds = grace };
    }
}
