using System.Reflection;
using System.Text.Json;
using CsCode.Profiling.Testing;
using Xunit;

namespace CsCode.MemoryTests.Toolkit;

/// <summary>Event hygiene across every CsCode assembly, and the recommended runtime configuration kept in step with its MSBuild props.</summary>
public sealed class HygieneTests(ITestOutputHelper output)
{
    /// <summary>
    /// Static events that exist today and are known: each is either Avalonia-facing glue with a disposable unsubscription or the end pass's to review.
    /// A NEW static event fails this test (ARCHITECTURE §8 rule 1: cross-service notifications use Emitter&lt;T&gt; with disposable subscriptions).
    /// </summary>
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal);

    [Fact]
    public void No_CsCode_assembly_declares_a_new_static_event()
    {
        var assemblies = Directory.GetFiles(AppContext.BaseDirectory, "*.dll")
            .Where(f => Path.GetFileName(f) is var n && (n.StartsWith("CsCode.", StringComparison.Ordinal) || n is "cscode.dll" or "cscode-server.dll"))
            .Where(f => !Path.GetFileName(f).Contains("Tests", StringComparison.Ordinal) && !Path.GetFileName(f).Contains("TestDoubles", StringComparison.Ordinal))
            .Select(f => Assembly.LoadFrom(f)).ToArray();
        Assert.True(assemblies.Length >= 10, $"only {assemblies.Length} CsCode assemblies found");
        var found = EventHygiene.StaticEvents(assemblies);
        output.WriteLine($"{assemblies.Length} assemblies; static events: {(found.Count == 0 ? "none" : string.Join(", ", found))}");
        Assert.DoesNotContain(found, e => !Known.Contains(e));
    }

    [Fact]
    public void The_recommended_runtimeconfig_and_build_MemoryDefaults_props_agree()
    {
        var root = CsCode.MemoryTests.App.AppWorkspace.RepoRoot();
        Assert.SkipWhen(root is null, "not running inside a checkout");
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(root!, "src", "CsCode.Profiling", "runtimeconfig.recommended.json")));
        var config = json.RootElement.GetProperty("runtimeOptions").GetProperty("configProperties");
        var props = System.Xml.Linq.XDocument.Load(Path.Combine(root!, "build", "MemoryDefaults.props"));
        string? Property(string name) => props.Descendants(name).SingleOrDefault()?.Value;
        Assert.Equal(config.GetProperty("System.GC.Server").GetBoolean().ToString().ToLowerInvariant(), Property("ServerGarbageCollector"));
        Assert.Equal(config.GetProperty("System.GC.Concurrent").GetBoolean().ToString().ToLowerInvariant(), Property("ConcurrentGarbageCollection"));
        Assert.Equal(config.GetProperty("System.GC.RetainVM").GetBoolean().ToString().ToLowerInvariant(), Property("RetainVMGarbageCollection"));
        Assert.Equal(config.GetProperty("System.Runtime.TieredPGO").GetBoolean().ToString().ToLowerInvariant(), Property("TieredPGO"));
        Assert.Equal(config.GetProperty("System.Globalization.Invariant").GetBoolean().ToString().ToLowerInvariant(), Property("InvariantGlobalization"));
        var conserve = props.Descendants("RuntimeHostConfigurationOption").Single(e => (string?)e.Attribute("Include") == "System.GC.ConserveMemory");
        Assert.Equal(config.GetProperty("System.GC.ConserveMemory").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture), (string?)conserve.Attribute("Value"));
    }
}
