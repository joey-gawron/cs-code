using System.Text.Json;
using CsCode.Remote.Protocol;

namespace CsCode.Remote.Transport;

/// <summary>A Lima VM as <c>limactl list --json</c> describes it.</summary>
public sealed record LimaInstance(string Name, string Status, string? Arch, string? VmType)
{
    public bool Running => string.Equals(Status, "Running", StringComparison.OrdinalIgnoreCase);

    /// <summary>The <c>lima://name</c> authority that connects to it.</summary>
    public string Target => RemoteSchemes.Lima + "://" + Name;
}

/// <summary>The Lima VMs this machine knows (<c>limactl list --json</c>), for the Connect to Lima VM picker.</summary>
public static class LimaInstances
{
    /// <summary>
    /// Reads <c>limactl list --json</c> output: one JSON object per line (limactl 1.x and 2.x), or a JSON array. Entries without a name that
    /// <see cref="RemoteAuthority"/> accepts as one argument (letters, digits, <c>_ . -</c>) are skipped, as is anything that is not JSON.
    /// </summary>
    public static IReadOnlyList<LimaInstance> Parse(string output)
    {
        var found = new List<LimaInstance>();
        var text = output.Trim();
        if (text.Length == 0) return found;
        if (text.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                foreach (var item in doc.RootElement.EnumerateArray()) Add(found, item);
            }
            catch (JsonException) { }
            return Sorted(found);
        }
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                Add(found, doc.RootElement);
            }
            catch (JsonException) { }
        }
        return Sorted(found);
    }

    private static List<LimaInstance> Sorted(List<LimaInstance> l) => [.. l.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)];

    private static void Add(List<LimaInstance> into, JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || Str(e, "name") is not { Length: > 0 } name) return;
        try { _ = new RemoteAuthority(name, scheme: RemoteSchemes.Lima); }
        catch (ArgumentException) { return; }
        into.Add(new LimaInstance(name, Str(e, "status") ?? "Unknown", Str(e, "arch"), Str(e, "vmType")));
    }

    private static string? Str(JsonElement e, string property) =>
        e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Runs <c>limactl list --json</c>. Fails with an <see cref="IOException"/> naming the problem (limactl missing, or its own error).</summary>
    public static async Task<IReadOnlyList<LimaInstance>> ListAsync(string limactlPath, IReadOnlyDictionary<string, string?>? environment, CancellationToken ct = default)
    {
        ShellResult result;
        try
        {
            result = await RemoteShellExtensions.RunAsync(new ProcessLaunch(limactlPath, ["list", "--json"]) { Environment = environment },
                null, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            throw new IOException($"Cannot run {limactlPath} (is Lima installed? Set remote.lima.path): {ex.Message}", ex);
        }
        if (result.ExitCode != 0) throw new IOException($"{limactlPath} list failed ({result.ExitCode}): {result.Stderr.Trim()}");
        return Parse(result.Stdout);
    }
}
