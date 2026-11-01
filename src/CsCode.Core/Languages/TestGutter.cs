using CsCode.Core.Lifecycle;
using CsCode.Core.Text;

namespace CsCode.Core.Languages;

/// <summary>What the editor's test glyph on a line shows: the last result of the test declared there.</summary>
public enum TestGutterState { None, Queued, Running, Passed, Failed, Skipped }

/// <summary>A test declared on <paramref name="Line"/> (0-based) of a document: its id in the Test Explorer, a label and its last state.</summary>
public sealed record TestGutterItem(int Line, string TestId, string Label, TestGutterState State, string? Message = null);

/// <summary>
/// The editor's "run / debug this test" glyphs (Test Explorer, <c>CsCode.Panels.Testing</c>): the tests a document declares, mapped to the
/// tests the explorer discovered, and the actions behind a glyph. Registered in <see cref="ILanguageFeatureRegistry"/> for the languages that
/// have a test provider; the editor asks again when the document changes and when <see cref="StatesChanged"/> fires.
/// </summary>
public interface ITestGutterProvider : ILanguageProvider
{
    /// <summary>The tests declared in <paramref name="document"/> (lines from its current text), or null when it declares none the explorer knows.</summary>
    ValueTask<IReadOnlyList<TestGutterItem>?> ProvideTestsAsync(ITextDocument document, CancellationToken ct);

    /// <summary>Fires when tests or results changed: the document whose glyphs may differ, or null for every document.</summary>
    IEventSource<Uri?> StatesChanged { get; }

    /// <summary>Runs (or debugs) the test <paramref name="testId"/>.</summary>
    ValueTask RunAsync(string testId, bool debug, CancellationToken ct = default);

    /// <summary>Shows the test in the Test Explorer, selected.</summary>
    ValueTask RevealAsync(string testId, CancellationToken ct = default);
}
