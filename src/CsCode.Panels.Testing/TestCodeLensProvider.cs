using System.Text.Json;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;

namespace CsCode.Panels.Testing;

/// <summary>
/// "Run Test | Debug Test" above each test the Test Explorer knows (the C# Dev Kit / rust-analyzer look), through the editor's code lens: the
/// same tests as the gutter glyphs (<see cref="TestGutterProvider"/>), so they appear after discovery and follow the explorer's tree. A click runs
/// <see cref="RunCommand"/> / <see cref="DebugCommand"/> with the test's id. <c>testing.codeLens</c> = false turns them off.
/// </summary>
internal sealed class TestCodeLensProvider : ICodeLensProvider, IDisposable
{
    public const string RunCommand = "testing.codeLens.run";
    public const string DebugCommand = "testing.codeLens.debug";

    private readonly TestGutterProvider _tests;
    private readonly Func<bool> _enabled;
    private readonly Emitter<Uri?> _changed = new();
    private readonly IDisposable _subscription;

    public TestCodeLensProvider(TestGutterProvider tests, Func<bool> enabled)
    {
        _tests = tests;
        _enabled = enabled;
        _subscription = tests.StatesChanged.Subscribe(_changed.Fire);
    }

    public IEventSource<Uri?> CodeLensesChanged => _changed;

    /// <summary>The setting changed: every editor asks again.</summary>
    internal void Invalidate() => _changed.Fire(null);

    public async ValueTask<IReadOnlyList<CodeLens>?> ProvideCodeLensesAsync(ITextDocument document, CancellationToken ct)
    {
        if (!_enabled()) return null;
        var tests = await _tests.ProvideTestsAsync(document, ct).ConfigureAwait(false);
        if (tests is null || tests.Count == 0) return null;
        var lenses = new List<CodeLens>(tests.Count * 2);
        foreach (var t in tests)
        {
            var at = TextRange.At(t.Line, 0);
            var id = JsonSerializer.SerializeToElement(t.TestId);
            lenses.Add(new CodeLens(at, new Command("Run Test", RunCommand, [id])));
            lenses.Add(new CodeLens(at, new Command("Debug Test", DebugCommand, [id])));
        }
        return lenses;
    }

    /// <summary>The handler of both commands: the test id is the only argument.</summary>
    internal static async ValueTask<object?> RunAsync(TestGutterProvider tests, CommandInvocation invocation, bool debug)
    {
        if (invocation.Arg<string>(0) is { Length: > 0 } id) await tests.RunAsync(id, debug, invocation.Cancellation).ConfigureAwait(false);
        return null;
    }

    public void Dispose()
    {
        _subscription.Dispose();
        _changed.Clear();
    }
}
