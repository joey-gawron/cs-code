using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CsCode.Core.Platform;
using CsCode.Remote.Ssh;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCode.Remote.Askpass;

/// <summary>
/// Routes ssh's prompts (key passphrase, password, 2FA code, host-key confirmation) to the GUI. For one connection attempt it opens a Unix socket in a
/// fresh owner-only (0700) directory, generates a random one-time token, and writes a tiny wrapper script that ssh runs as <c>SSH_ASKPASS</c>
/// (<c>SSH_ASKPASS_REQUIRE=force</c>): the script execs <c>cscode --askpass "&lt;prompt&gt;"</c>, which sends the token and prompt over the socket
/// (<see cref="AskpassClient"/>); the answer comes from <see cref="IWindowService"/> and goes back the same way. Nothing is stored; the directory,
/// socket and token die with the attempt. Unix only (Windows' OpenSSH has no askpass path here yet).
/// </summary>
public sealed class AskpassServer : IDisposable
{
    public const string SocketVariable = "CSCODE_ASKPASS_SOCKET";
    public const string TokenVariable = "CSCODE_ASKPASS_TOKEN";

    private readonly IWindowService _window;
    private readonly ILogger _log;
    private readonly Socket _listener;
    private readonly string _token;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _acceptThread;
    private int _prompts;
    private int _disposed;

    private AskpassServer(IWindowService window, string directory, Socket listener, string token, string script, ILogger log)
    {
        _window = window;
        Directory = directory;
        _listener = listener;
        _token = token;
        ScriptPath = script;
        _log = log;
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "CsCode.Remote-askpass" };
        _acceptThread.Start();
    }

    public string Directory { get; }
    public string ScriptPath { get; }
    public string SocketPath => Path.Combine(Directory, "s");
    /// <summary>Prompts answered so far (tests).</summary>
    public int Prompts => Volatile.Read(ref _prompts);

    /// <summary>Environment for the ssh process.</summary>
    public IReadOnlyDictionary<string, string?> Environment => new Dictionary<string, string?>
    {
        ["SSH_ASKPASS"] = ScriptPath,
        ["SSH_ASKPASS_REQUIRE"] = "force",
        [SocketVariable] = SocketPath,
        [TokenVariable] = _token,
    };

    /// <param name="executable">The program ssh should run (the app's own executable, or the server's), plus any arguments before <c>--askpass</c>.</param>
    public static AskpassServer Start(IWindowService window, IReadOnlyList<string> executable, ILogger? log = null, string? runtimeDir = null)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("askpass forwarding is implemented for Unix ssh clients");
        var baseDir = runtimeDir ?? (System.Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } x && System.IO.Directory.Exists(x) ? x : Path.GetTempPath());
        var dir = Path.Combine(baseDir, "cscode-askpass-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)));
        System.IO.Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            var path = Path.Combine(dir, "s");
            socket.Bind(new UnixDomainSocketEndPoint(path));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            socket.Listen(4);
            var script = Path.Combine(dir, "askpass.sh");
            var line = string.Join(' ', executable.Select(PosixShell.Quote)) + " --askpass \"$@\"";
            File.WriteAllText(script, "#!/bin/sh\nexec " + line + "\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            return new AskpassServer(window, dir, socket, token, script, log ?? NullLogger.Instance);
        }
        catch
        {
            try { System.IO.Directory.Delete(dir, true); } catch (IOException) { }
            throw;
        }
    }

    private void AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket client;
            try { client = _listener.Accept(); }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { return; }
            if (_cts.IsCancellationRequested) { client.Dispose(); return; } // Dispose's wake-up (macOS)
            _ = HandleAsync(client);
        }
    }

    private sealed record Request(string? Token, string? Prompt);
    private sealed record Reply(bool Ok, string? Answer);

    private async Task HandleAsync(Socket client)
    {
        using (client)
        await using (var stream = new NetworkStream(client, ownsSocket: false))
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var line = await ReadLineAsync(stream, timeout.Token).ConfigureAwait(false);
                var request = line is null ? null : JsonSerializer.Deserialize<Request>(line, JsonSerializerOptions.Web);
                if (request?.Token is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(request.Token), Encoding.ASCII.GetBytes(_token)))
                {
                    _log.LogWarning("askpass: rejected a request with a wrong token");
                    return;
                }
                Interlocked.Increment(ref _prompts);
                var answer = await AskAsync(request.Prompt ?? "", _cts.Token).ConfigureAwait(false);
                var reply = JsonSerializer.Serialize(new Reply(answer is not null, answer), JsonSerializerOptions.Web) + "\n";
                await stream.WriteAsync(Encoding.UTF8.GetBytes(reply), _cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or JsonException or OperationCanceledException or ObjectDisposedException)
            {
                _log.LogDebug(ex, "askpass request failed");
            }
        }
    }

    /// <summary>A yes/no question for host-key confirmations, a (masked unless it is a code/user name) input box for everything else.</summary>
    internal async Task<string?> AskAsync(string prompt, CancellationToken ct)
    {
        var text = prompt.Trim();
        if (IsConfirmation(text))
        {
            var choice = await _window.ShowMessageAsync(MessageSeverity.Warning, text, [new MessageAction("Yes"), new MessageAction("No", IsCloseAffordance: true)], ct).ConfigureAwait(false);
            return choice == "Yes" ? "yes" : choice is null ? null : "no";
        }
        return await _window.ShowInputBoxAsync(new InputBoxOptions(Title: "SSH", Prompt: text, Password: IsSecret(text)), ct).ConfigureAwait(false);
    }

    internal static bool IsConfirmation(string prompt) =>
        prompt.Contains("(yes/no", StringComparison.OrdinalIgnoreCase) || prompt.Contains("continue connecting", StringComparison.OrdinalIgnoreCase);

    internal static bool IsSecret(string prompt) =>
        prompt.Contains("passphrase", StringComparison.OrdinalIgnoreCase) || prompt.Contains("password", StringComparison.OrdinalIgnoreCase)
        || prompt.Contains("PIN", StringComparison.Ordinal) || !prompt.Contains("code", StringComparison.OrdinalIgnoreCase) && !prompt.Contains("user", StringComparison.OrdinalIgnoreCase);

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new List<byte>(256);
        var one = new byte[1];
        while (buffer.Count < 64 * 1024)
        {
            if (await stream.ReadAsync(one, ct).ConfigureAwait(false) == 0) return buffer.Count == 0 ? null : Encoding.UTF8.GetString(buffer.ToArray());
            if (one[0] == '\n') return Encoding.UTF8.GetString(buffer.ToArray());
            buffer.Add(one[0]);
        }
        return null;
    }

    /// <summary>
    /// macOS: closing a listening socket does not end an <c>accept</c> blocked on it in another thread, and the runtime's close waits for that
    /// call, so Dispose hung. A connection of our own ends the call; the loop sees the cancellation and leaves before the close.
    /// </summary>
    private void WakeAcceptLoop()
    {
        try
        {
            using var wake = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            wake.Connect(new UnixDomainSocketEndPoint(SocketPath));
        }
        catch (SocketException) { return; } // nothing listens any more: no call to end
        _acceptThread.Join(TimeSpan.FromSeconds(1));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        if (OperatingSystem.IsMacOS()) WakeAcceptLoop();
        try { _listener.Dispose(); } catch (SocketException) { }
        try { System.IO.Directory.Delete(Directory, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _cts.Dispose();
    }
}

/// <summary>The <c>--askpass</c> mode of <c>cscode</c> / <c>cscode-server</c>: asks the GUI that started ssh, prints the answer for ssh.</summary>
public static class AskpassClient
{
    /// <returns>Process exit code: 0 with the answer on <paramref name="stdout"/>, 1 when the user cancelled or nothing could be asked.</returns>
    public static int Run(string prompt, TextWriter stdout, TextWriter stderr, Func<string, string?>? getEnv = null)
    {
        getEnv ??= System.Environment.GetEnvironmentVariable;
        var path = getEnv(AskpassServer.SocketVariable);
        var token = getEnv(AskpassServer.TokenVariable);
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(token))
        {
            stderr.WriteLine("cscode --askpass: not started by CsCode (no askpass socket)");
            return 1;
        }
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(path));
            using var stream = new NetworkStream(socket, ownsSocket: false);
            var request = JsonSerializer.Serialize(new { token, prompt }, JsonSerializerOptions.Web) + "\n";
            stream.Write(Encoding.UTF8.GetBytes(request));
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var line = reader.ReadLine();
            if (line is null) return 1;
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean()) return 1;
            stdout.Write(doc.RootElement.GetProperty("answer").GetString() ?? "");
            stdout.Write('\n');
            stdout.Flush();
            return 0;
        }
        catch (Exception ex) when (ex is SocketException or IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            stderr.WriteLine("cscode --askpass: " + ex.Message);
            return 1;
        }
    }
}
