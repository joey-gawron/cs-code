using System.Runtime.CompilerServices;
using CsCode.Editor.TreeSitter;
using CsCode.Editor.TreeSitter.Grammars;
using CsCode.Editor.TreeSitter.Highlighting;
using CsCode.Editor.TreeSitter.Native;
using Xunit;

namespace CsCode.Editor.Tests.TreeSitter;

/// <summary>
/// Shared plumbing for the tree-sitter tests: the runtime and grammars are the app's bundle when tools/treesitter/build-grammars.sh has built
/// one (copied next to the test assembly as tree-sitter/), otherwise whatever this machine has (the development fallback: libtree-sitter 0.26
/// and the grammar packages in the Nix store, read-only). A test that needs them SKIPS with the reason when they are missing.
/// </summary>
internal static class TreeSitterTestSupport
{
    /// <summary>Exact native accounting in tests (a counting allocator; must be installed before the runtime's first use).</summary>
    [ModuleInitializer]
    internal static void EnableAllocationTracking()
    {
        if (Environment.GetEnvironmentVariable("CSCODE_TREESITTER_TRACK_ALLOC") != "0") TsRuntime.TrackAllocations = true;
    }

    private static readonly GrammarLocator Locator = GrammarLocator.CreateDefault(null);

    /// <summary>
    /// A registry over the machine's grammars. Registries are confined to one thread (their injection parsers are not thread-safe), so every
    /// test makes its own; the grammar bundles (languages + compiled queries) behind them are shared process-wide.
    /// </summary>
    public static GrammarRegistry NewRegistry() => new(Locator, TreeSitterLanguages.RootScopeFor);

    public static void RequireRuntime() =>
        Assert.SkipUnless(TsRuntime.Load(), "libtree-sitter is not available on this machine: " + TsRuntime.FailureReason);

    public static LanguageBundle RequireGrammar(string grammar)
    {
        RequireRuntime();
        using var registry = NewRegistry();
        var bundle = registry.Get(grammar, out var error);
        Assert.SkipWhen(bundle is null, $"grammar {grammar} is not usable here: {error}");
        return bundle!;
    }

    /// <summary>Parses <paramref name="text"/> and returns the scope stack (outermost first) of every character.</summary>
    public static string[][] Highlight(LanguageBundle bundle, string text, TestStacks? stacks = null)
    {
        stacks ??= new TestStacks();
        using var registry = NewRegistry();
        using var doc = new SyntaxDocument(registry, bundle);
        var input = new StringInput(text);
        Assert.Equal(ParseOutcome.Completed, doc.Parse(input, long.MaxValue));
        var paint = new PaintBuffer();
        doc.Highlight(input, 0, text.Length, stacks, paint);
        var result = new string[text.Length][];
        for (var i = 0; i < text.Length; i++) result[i] = stacks.Scopes(paint.StackAt(i));
        return result;
    }

    /// <summary>The scope stack of the first character of the first occurrence of <paramref name="needle"/>.</summary>
    public static string[] ScopesOf(string text, string[][] scopes, string needle, int occurrence = 0)
    {
        var at = -1;
        for (var i = 0; i <= occurrence; i++)
        {
            at = text.IndexOf(needle, at + 1, StringComparison.Ordinal);
            Assert.True(at >= 0, $"'{needle}' not in the sample");
        }
        return scopes[at];
    }

    public static bool Has(string[] stack, string scopePrefix) =>
        stack.Any(s => s == scopePrefix || s.StartsWith(scopePrefix + ".", StringComparison.Ordinal));
}

/// <summary>A string as tree-sitter input.</summary>
internal sealed class StringInput : ITextInput
{
    private readonly string _text;
    private readonly int[] _lineStarts;

    public StringInput(string text)
    {
        _text = text;
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            if (text[i] is '\n' or '\r') starts.Add(i + 1);
        }
        _lineStarts = starts.ToArray();
    }

    public int Length => _text.Length;
    public int LineCount => _lineStarts.Length;
    public void CopyTo(int offset, Span<char> destination) => _text.AsSpan(offset, destination.Length).CopyTo(destination);
    public int LineStart(int line) => _lineStarts[Math.Clamp(line, 0, _lineStarts.Length - 1)];

    public int LineOfOffset(int offset)
    {
        var i = Array.BinarySearch(_lineStarts, offset);
        return i >= 0 ? i : ~i - 1;
    }
}

/// <summary>A plain scope-stack interner for engine tests.</summary>
internal sealed class TestStacks : IScopeStacks
{
    private readonly List<string[]> _stacks = new();
    private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);

    public string[] Scopes(int id) => _stacks[id];

    public int Root(int scopeKey) => Intern([ScopeNames.NameOf(scopeKey)]);

    public int Push(int parentId, int scopeKey) => Intern([.. _stacks[parentId], ScopeNames.NameOf(scopeKey)]);

    private int Intern(string[] stack)
    {
        var key = string.Join('\u0001', stack);
        if (_ids.TryGetValue(key, out var id)) return id;
        _stacks.Add(stack);
        return _ids[key] = _stacks.Count - 1;
    }
}
