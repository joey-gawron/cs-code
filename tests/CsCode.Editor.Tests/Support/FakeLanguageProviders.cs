using CsCode.Core.Languages;
using CsCode.Core.Text;

namespace CsCode.Editor.Tests.Support;

/// <summary>Delegate-driven fakes for the language-feature provider interfaces (every behaviour — instant, slow, hung, throwing — is a lambda).</summary>
public sealed class FakeSignatureProvider(
    Func<ITextDocument, TextPosition, SignatureHelpContext, CancellationToken, Task<SignatureHelp?>> provide,
    IReadOnlyList<string>? triggers = null,
    IReadOnlyList<string>? retriggers = null) : ISignatureHelpProvider
{
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);
    public List<SignatureHelpContext> Contexts { get; } = new();
    public IReadOnlyList<string> TriggerCharacters { get; } = triggers ?? ["(", ","];
    public IReadOnlyList<string> RetriggerCharacters { get; } = retriggers ?? [",", ")"];

    public async ValueTask<SignatureHelp?> ProvideSignatureHelpAsync(ITextDocument document, TextPosition position, SignatureHelpContext context, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        lock (Contexts) Contexts.Add(context);
        return await provide(document, position, context, ct).ConfigureAwait(false);
    }
}

public sealed class FakeCodeActionProvider(Func<ITextDocument, TextRange, CodeActionContext, CancellationToken, Task<IReadOnlyList<CodeAction>?>> provide) : ICodeActionProvider
{
    public List<CodeActionContext> Contexts { get; } = new();
    public Func<CodeAction, CodeAction>? Resolve { get; set; }

    public async ValueTask<IReadOnlyList<CodeAction>?> ProvideCodeActionsAsync(ITextDocument document, TextRange range, CodeActionContext context, CancellationToken ct)
    {
        lock (Contexts) Contexts.Add(context);
        return await provide(document, range, context, ct).ConfigureAwait(false);
    }

    public ValueTask<CodeAction> ResolveCodeActionAsync(CodeAction action, CancellationToken ct) => ValueTask.FromResult(Resolve?.Invoke(action) ?? action);
}

public sealed class FakeDefinitionProvider(Func<ITextDocument, TextPosition, DefinitionKind, Task<IReadOnlyList<LocationLink>?>> provide) : IDefinitionProvider
{
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);

    public async ValueTask<IReadOnlyList<LocationLink>?> ProvideDefinitionAsync(ITextDocument document, TextPosition position, DefinitionKind kind, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        return await provide(document, position, kind).ConfigureAwait(false);
    }
}

public sealed class FakeReferenceProvider(Func<ITextDocument, TextPosition, Task<IReadOnlyList<Location>?>> provide) : IReferenceProvider
{
    public ValueTask<IReadOnlyList<Location>?> ProvideReferencesAsync(ITextDocument document, TextPosition position, bool includeDeclaration, CancellationToken ct) =>
        new(provide(document, position));
}

public sealed class FakeRenameProvider(
    Func<ITextDocument, TextPosition, string, Task<WorkspaceEdit?>> rename,
    Func<ITextDocument, TextPosition, PrepareRenameResult?>? prepare = null) : IRenameProvider
{
    public List<string> RequestedNames { get; } = new();

    public ValueTask<PrepareRenameResult?> PrepareRenameAsync(ITextDocument document, TextPosition position, CancellationToken ct) =>
        ValueTask.FromResult(prepare?.Invoke(document, position));

    public async ValueTask<WorkspaceEdit?> ProvideRenameAsync(ITextDocument document, TextPosition position, string newName, CancellationToken ct)
    {
        lock (RequestedNames) RequestedNames.Add(newName);
        return await rename(document, position, newName).ConfigureAwait(false);
    }
}

public sealed class FakeFormattingProvider(
    Func<ITextDocument, FormattingOptions, Task<IReadOnlyList<TextEdit>?>> document,
    Func<ITextDocument, TextRange, Task<IReadOnlyList<TextEdit>?>>? range = null,
    Func<ITextDocument, TextPosition, string, Task<IReadOnlyList<TextEdit>?>>? onType = null,
    IReadOnlyList<string>? onTypeTriggers = null) : IDocumentFormattingProvider
{
    public IReadOnlyList<string> OnTypeTriggerCharacters { get; } = onTypeTriggers ?? [];
    public int DocumentCalls;
    public List<FormattingOptions> Options { get; } = new();

    public async ValueTask<IReadOnlyList<TextEdit>?> FormatDocumentAsync(ITextDocument d, FormattingOptions options, CancellationToken ct)
    {
        Interlocked.Increment(ref DocumentCalls);
        lock (Options) Options.Add(options);
        return await document(d, options).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<TextEdit>?> FormatRangeAsync(ITextDocument d, TextRange r, FormattingOptions options, CancellationToken ct) =>
        range is null ? null : await range(d, r).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<TextEdit>?> FormatOnTypeAsync(ITextDocument d, TextPosition position, string typedCharacter, FormattingOptions options, CancellationToken ct) =>
        onType is null ? null : await onType(d, position, typedCharacter).ConfigureAwait(false);
}

public sealed class FakeHighlightProvider(Func<ITextDocument, TextPosition, Task<IReadOnlyList<DocumentHighlight>?>> provide) : IDocumentHighlightProvider
{
    public ValueTask<IReadOnlyList<DocumentHighlight>?> ProvideDocumentHighlightsAsync(ITextDocument document, TextPosition position, CancellationToken ct) => new(provide(document, position));
}
