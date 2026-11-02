using System.Text;
using System.Text.RegularExpressions;
using CsCode.Testing.Engine;

namespace CsCode.Testing.Providers.Rust;

/// <summary>
/// Reads the plain output of a Rust test binary (libtest, the stable format): <c>test a::b ... ok</c> / <c>FAILED</c> / <c>ignored[, reason]</c>
/// as each test ends, then for each failure a <c>---- a::b stdout ----</c> section with what it printed and its panic message. A failed test is
/// reported once its section has been read (or when the output ends). Every line also goes to the output. libtest reports no per-test time on
/// stable, so results carry none. Not thread-safe: one instance per test binary, fed lines in order, then <see cref="Complete"/>.
/// </summary>
public sealed partial class LibtestOutputParser(TestProject project, ITestRunSink sink, Func<string, string> toTestId)
{
    private readonly Dictionary<string, StringBuilder> _sections = new(StringComparer.Ordinal);
    private readonly List<string> _failed = [];
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private string? _section;

    /// <summary>The ids of the tests a result was reported for.</summary>
    public IReadOnlyCollection<string> Reported => _reported;

    public void OnLine(string line)
    {
        sink.Output(line);
        if (line.StartsWith("---- ", StringComparison.Ordinal) && line.EndsWith(" ----", StringComparison.Ordinal))
        {
            // "---- tests::fails stdout ----"
            var inner = line[5..^5];
            var space = inner.LastIndexOf(' ');
            _section = space > 0 ? inner[..space] : inner;
            _sections[_section] = new StringBuilder();
            return;
        }
        if (_section is not null)
        {
            if (line is "failures:" or "successes:" || line.StartsWith("test result:", StringComparison.Ordinal))
            {
                _section = null;
                return;
            }
            var sb = _sections[_section];
            if (sb.Length < TestLimits.MaxOutputPerTest) sb.AppendLine(line);
            return;
        }
        var m = ResultLine().Match(line);
        if (!m.Success) return;
        var name = m.Groups["name"].Value;
        switch (m.Groups["outcome"].Value)
        {
            case "ok":
                Report(new TestResult(toTestId(name), TestOutcome.Passed));
                break;
            case "FAILED":
                _failed.Add(name);
                break;
            default: // ignored, ignored, <reason>
                var reason = m.Groups["reason"].Success ? m.Groups["reason"].Value : null;
                Report(new TestResult(toTestId(name), TestOutcome.Skipped, Message: reason ?? "#[ignore]"));
                break;
        }
    }

    /// <summary>Reports the failures (with their sections) once the output has ended.</summary>
    public void Complete()
    {
        foreach (var name in _failed)
        {
            var text = _sections.TryGetValue(name, out var sb) ? sb.ToString().TrimEnd() : "";
            var (output, message) = SplitPanic(text);
            Report(new TestResult(toTestId(name), TestOutcome.Failed, Message: TestLimits.Clip(message ?? "The test failed."), Output: TestLimits.Clip(output),
                FailureLocation: StackTraces.FromRustPanic(text, project.Directory), StackTrace: TestLimits.Clip(message is null ? null : text)));
        }
        _failed.Clear();
    }

    private void Report(TestResult result)
    {
        _reported.Add(result.TestId);
        sink.Finished(project, result);
    }

    /// <summary>The section split at the panic: what the test printed before it, and the panic message (without the backtrace note).</summary>
    internal static (string? Output, string? Message) SplitPanic(string text)
    {
        var at = text.IndexOf("thread '", StringComparison.Ordinal);
        if (at < 0) return (text.Length > 0 ? text : null, null);
        var output = text[..at].TrimEnd();
        var panic = text[at..];
        var colon = panic.IndexOf(":\n", StringComparison.Ordinal);
        if (colon < 0) colon = panic.IndexOf(":\r\n", StringComparison.Ordinal);
        var message = colon >= 0 ? panic[(colon + 1)..].TrimStart('\r', '\n') : panic;
        var note = message.IndexOf("\nnote: run with `RUST_BACKTRACE", StringComparison.Ordinal);
        if (note >= 0) message = message[..note];
        return (output.Length > 0 ? output : null, message.Trim());
    }

    // a name may hold spaces: doc-tests are "src/lib.rs - add (line 3)"
    [GeneratedRegex(@"^test (?<name>.+?) \.\.\. (?<outcome>ok|FAILED|ignored)(?:, (?<reason>.+))?$", RegexOptions.CultureInvariant)]
    private static partial Regex ResultLine();
}
