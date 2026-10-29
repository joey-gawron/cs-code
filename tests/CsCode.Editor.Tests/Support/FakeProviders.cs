using CsCode.Core.Languages;
using CsCode.Core.Text;

namespace CsCode.Editor.Tests.Support;

/// <summary>Configurable <see cref="ICompletionProvider"/>: every behaviour (instant, slow, never completes, throws, huge) is a delegate.</summary>
public sealed class FakeCompletionProvider(
    Func<ITextDocument, TextPosition, CompletionContext, CancellationToken, Task<CompletionList?>> provide,
    IReadOnlyList<string>? triggerCharacters = null) : ICompletionProvider
{
    private int _calls;
    private readonly List<CompletionContext> _contexts = new();

    public IReadOnlyList<string> TriggerCharacters { get; } = triggerCharacters ?? [];
    public int Calls => Volatile.Read(ref _calls);
    public IReadOnlyList<CompletionContext> Contexts { get { lock (_contexts) return _contexts.ToArray(); } }
    public Func<CompletionItem, CancellationToken, Task<CompletionItem>>? Resolve { get; set; }

    public async ValueTask<CompletionList?> ProvideCompletionAsync(ITextDocument document, TextPosition position, CompletionContext context, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        lock (_contexts) _contexts.Add(context);
        return await provide(document, position, context, ct).ConfigureAwait(false);
    }

    public async ValueTask<CompletionItem> ResolveCompletionAsync(CompletionItem item, CancellationToken ct) =>
        Resolve is null ? item : await Resolve(item, ct).ConfigureAwait(false);

    public static FakeCompletionProvider Items(params string[] labels) =>
        new((_, _, _, _) => Task.FromResult<CompletionList?>(new CompletionList(labels.Select(l => new CompletionItem { Label = l, Kind = CompletionItemKind.Method }).ToArray())));

    public static FakeCompletionProvider Of(IReadOnlyList<CompletionItem> items, bool incomplete = false, IReadOnlyList<string>? triggers = null) =>
        new((_, _, _, _) => Task.FromResult<CompletionList?>(new CompletionList(items, incomplete)), triggers);
}

public sealed class FakeHoverProvider(Func<ITextDocument, TextPosition, CancellationToken, Task<Hover?>> provide) : IHoverProvider
{
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);

    public async ValueTask<Hover?> ProvideHoverAsync(ITextDocument document, TextPosition position, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        return await provide(document, position, ct).ConfigureAwait(false);
    }
}
