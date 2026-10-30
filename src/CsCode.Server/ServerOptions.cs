namespace CsCode.Server;

/// <summary><c>cscode-server [--stdio] [--data-dir DIR] [--marker TAG] [--version] [--askpass PROMPT]</c></summary>
public sealed record ServerOptions(bool Stdio, string DataDir, string? Marker, bool ShowVersion, string? AskpassPrompt, bool Askpass)
{
    public static ServerOptions Parse(IReadOnlyList<string> args)
    {
        bool stdio = true, version = false, askpass = false;
        string? dataDir = null, marker = null, prompt = null;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--stdio": stdio = true; break;
                case "--version": version = true; break;
                case "--data-dir" when i + 1 < args.Count: dataDir = args[++i]; break;
                case "--marker" when i + 1 < args.Count: marker = args[++i]; break;
                case "--askpass":
                    askpass = true;
                    prompt = i + 1 < args.Count ? string.Join(' ', args.Skip(i + 1)) : "";
                    i = args.Count;
                    break;
                default: throw new ArgumentException($"Unknown argument '{args[i]}'");
            }
        }
        dataDir ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cscode-server", "data");
        return new ServerOptions(stdio, Path.GetFullPath(dataDir), marker, version, prompt, askpass);
    }
}
