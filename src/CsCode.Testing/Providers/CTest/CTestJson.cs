using System.Text.Json;

namespace CsCode.Testing.Providers.CTest;

/// <summary>
/// One test of <c>ctest --show-only=json-v1</c>: its name, command line, labels, working directory (null: ctest's default, the build
/// directory of the CMakeLists.txt that added it), environment, whether it is disabled, and where it was added (the innermost node of its
/// backtrace: <c>add_test</c>, or <c>gtest_discover_tests</c> for the tests GoogleTest's CMake module adds).
/// </summary>
internal sealed record CTestInfo(string Name, IReadOnlyList<string> Command, IReadOnlyList<string> Labels, string? WorkingDirectory,
    IReadOnlyList<string> Environment, bool Disabled, string? File, int? Line)
{
    /// <summary>The program the test runs (null for a test without a command, which ctest reports as "Not Run").</summary>
    public string? Executable => Command.Count > 0 ? Command[0] : null;
}

/// <summary>
/// Parses ctest's JSON test listing (format <c>json-v1</c>, CMake 3.14+; cmake-ctest(1) "Show as JSON Object Model"). Shared with the C++ test
/// providers (stage 4), which read it to skip executables whose tests <c>gtest_discover_tests</c> already registered.
/// </summary>
internal static class CTestJson
{
    public static IReadOnlyList<CTestInfo> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var files = new List<string>();
        var nodes = new List<(int? File, int? Line, int? Parent)>();
        if (root.TryGetProperty("backtraceGraph", out var graph))
        {
            if (graph.TryGetProperty("files", out var f)) files.AddRange(f.EnumerateArray().Select(x => x.GetString() ?? ""));
            if (graph.TryGetProperty("nodes", out var n))
                nodes.AddRange(n.EnumerateArray().Select(x => (Int(x, "file"), Int(x, "line"), Int(x, "parent"))));
        }
        var result = new List<CTestInfo>();
        if (!root.TryGetProperty("tests", out var tests) || tests.ValueKind != JsonValueKind.Array) return result;
        foreach (var t in tests.EnumerateArray())
        {
            var name = t.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
            if (name.Length == 0) continue;
            var command = t.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.Array ? c.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : [];
            IReadOnlyList<string> labels = [];
            IReadOnlyList<string> env = [];
            string? workingDirectory = null;
            var disabled = false;
            if (t.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Array)
                foreach (var p in props.EnumerateArray())
                {
                    var value = p.TryGetProperty("value", out var v) ? v : default;
                    switch (p.TryGetProperty("name", out var pn) ? pn.GetString() : null)
                    {
                        case "LABELS": labels = Strings(value); break;
                        case "ENVIRONMENT": env = Strings(value); break;
                        case "WORKING_DIRECTORY": workingDirectory = value.ValueKind == JsonValueKind.String ? value.GetString() : null; break;
                        case "DISABLED": disabled = value.ValueKind == JsonValueKind.True || (value.ValueKind == JsonValueKind.String && IsTrue(value.GetString())); break;
                    }
                }
            string? file = null;
            int? line = null;
            if (Int(t, "backtrace") is { } bt && bt >= 0 && bt < nodes.Count)
            {
                var node = nodes[bt];
                if (node.File is { } fi && fi >= 0 && fi < files.Count) file = files[fi];
                line = node.Line;
            }
            result.Add(new CTestInfo(name, command, labels, workingDirectory, env, disabled, file, line));
        }
        return result;
    }

    private static IReadOnlyList<string> Strings(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Array => [.. value.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : x.GetRawText())],
        JsonValueKind.String => [value.GetString()!],
        _ => [],
    };

    private static bool IsTrue(string? s) => s?.ToUpperInvariant() is "1" or "ON" or "YES" or "TRUE" or "Y";

    private static int? Int(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}
