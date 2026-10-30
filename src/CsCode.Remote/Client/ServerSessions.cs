using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CsCode.Remote.Client;

/// <summary>
/// The persistent-server session id a client uses per host (R4). Within one client process the id stays the same for a host, so every reconnect
/// reaches the same server. Across a client restart: the id is recorded with its owner's pid in <c>{GlobalStorageDir}/remote/sessions/</c>; a client
/// that finds the previous owner dead (it crashed or was killed) takes the id over and reattaches to the server it left running (warm language
/// servers) — while two live windows never share one. The record file is claimed under an exclusive lock.
/// </summary>
public sealed class ServerSessionRegistry(string? directory)
{
    private readonly Dictionary<string, string> _mine = new(StringComparer.Ordinal);

    private sealed record Entry(string Id, int Pid, long StartTicks);

    /// <summary>The id to use for <paramref name="key"/> (a host's authority): ours, a dead owner's, or a new one.</summary>
    public string Claim(string key)
    {
        lock (_mine)
        {
            if (_mine.TryGetValue(key, out var id)) return id;
            id = ClaimFromDisk(key) ?? NewId();
            _mine[key] = id;
            return id;
        }
    }

    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private string? ClaimFromDisk(string key)
    {
        if (directory is null) return null;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant() + ".json");
            using var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            string? reuse = null;
            if (file.Length is > 0 and < 4096)
            {
                try
                {
                    var previous = JsonSerializer.Deserialize<Entry>(file);
                    if (previous is not null && IsValid(previous.Id) && !IsAlive(previous.Pid, previous.StartTicks)) reuse = previous.Id;
                    else if (previous is not null && IsAlive(previous.Pid, previous.StartTicks)) return null; // another live window owns it: ours is new (not recorded)
                }
                catch (JsonException) { }
            }
            var id = reuse ?? NewId();
            file.SetLength(0);
            JsonSerializer.Serialize(file, new Entry(id, Environment.ProcessId, StartTicks(Environment.ProcessId)));
            return id;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; } // locked by a concurrent claim, unwritable: a fresh id
    }

    private static bool IsValid(string id) => id.Length == 32 && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static long StartTicks(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return 0; }
    }

    private static bool IsAlive(int pid, long startTicks) => pid > 0 && StartTicks(pid) is var t && t != 0 && (startTicks == 0 || t == startTicks);
}
