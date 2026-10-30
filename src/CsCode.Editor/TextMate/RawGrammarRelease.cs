using System.Collections;
using System.Reflection;
using TextMateSharp.Grammars;
using TextMateSharp.Internal.Grammars;
using TextMateSharp.Internal.Grammars.Parser;
using TextMateSharp.Internal.Types;
using TextMateSharp.Registry;
using CompiledGrammar = TextMateSharp.Internal.Grammars.Grammar;

namespace CsCode.Editor.TextMate;

/// <summary>
/// T37: TextMateSharp keeps every grammar's raw JSON tree after compiling it — measured at 15–20 MB of the managed heap with 20 documents
/// open (docs/MEMORY.md §7, §12.1): the registry keeps the parsed tree of every grammar it loaded (a grammar's whole include closure: Markdown
/// pulls in dozens of fenced-code languages), and each compiled grammar keeps a deep copy of its own tree plus one of every grammar it
/// included. None of it is read again once the grammar is compiled: <c>RuleFactory</c> builds the whole reachable rule graph (external
/// grammars included) on the first line, and the injections are collected on that line too; the compiled rules hold their regex sources,
/// never the tree. So the grammar is compiled at once here, on the editor worker, and then the trees are dropped: the compiled grammar keeps
/// a stub with its name and file types (all it still reads), and the registry forgets its parsed trees — a grammar compiled later finds
/// them missing and <c>Registry.LoadGrammar</c> reads its whole include closure again from the sources (a few ms of JSON parsing, once per
/// language).
///
/// Done by reflection against TextMateSharp 2.0.3's private layout; when it differs, nothing is dropped (the memory stays as it was) and the
/// test of this class says so. Editor worker only, like the registry.
/// </summary>
internal static class RawGrammarRelease
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private static readonly FieldInfo? SyncRegistryField = typeof(Registry).GetField("syncRegistry", Private);
    private static readonly FieldInfo? RawGrammarsField = typeof(SyncRegistry).GetField("_rawGrammars", Private);
    private static readonly FieldInfo? GrammarRawField = typeof(CompiledGrammar).GetField("_rawGrammar", Private);
    private static readonly FieldInfo? IncludedField = typeof(CompiledGrammar).GetField("_includedGrammars", Private);
    private static readonly FieldInfo? RootIdField = typeof(CompiledGrammar).GetField("_rootId", Private);
    private static readonly FieldInfo? InjectionsField = typeof(CompiledGrammar).GetField("_injections", Private);

    /// <summary>True when TextMateSharp's private layout is the one this was written against (tests).</summary>
    internal static bool Supported =>
        SyncRegistryField is not null && RawGrammarsField is not null && GrammarRawField is not null && IncludedField is not null
        && RootIdField is not null && InjectionsField is not null;

    /// <summary>Compiles <paramref name="grammar"/> now and drops the raw trees nothing reads any more. False when nothing was dropped.</summary>
    public static bool Compile(Registry registry, IGrammar grammar)
    {
        if (!Supported || grammar is not CompiledGrammar compiled) return false;
        try
        {
            // the first line builds the whole rule graph (and the injection list): one empty line is enough
            compiled.TokenizeLine("", null, TimeSpan.FromSeconds(5));
            if (RootIdField!.GetValue(compiled) is null || InjectionsField!.GetValue(compiled) is null) return false; // not compiled: keep everything
            if (GrammarRawField!.GetValue(compiled) is IRawGrammar raw) GrammarRawField.SetValue(compiled, Stub(raw));
            (IncludedField!.GetValue(compiled) as IDictionary)?.Clear();
            if (SyncRegistryField!.GetValue(registry) is { } sync) (RawGrammarsField!.GetValue(sync) as IDictionary)?.Clear();
            return true;
        }
        catch (Exception ex) when (ex is TargetException or ArgumentException or InvalidCastException or FieldAccessException)
        {
            return false;
        }
    }

    /// <summary>What a compiled grammar still reads of its tree (<c>GetName</c>, <c>GetFileTypes</c>, <c>GetScopeName</c>).</summary>
    private static Raw Stub(IRawGrammar raw)
    {
        var stub = new Raw();
        if (raw.GetName() is { } name) stub["name"] = name;
        if (raw.GetScopeName() is { } scope) stub["scopeName"] = scope;
        if (raw.GetFileTypes() is { Count: > 0 } types) stub["fileTypes"] = types.Cast<object>().ToList();
        return stub;
    }

    /// <summary>Tests: the raw trees the registry still holds, and whether a compiled grammar still holds its own (more than a stub) or any included one.</summary>
    internal static (int RegistryTrees, bool GrammarTree, int IncludedTrees) Held(Registry registry, IGrammar grammar)
    {
        var registryTrees = SyncRegistryField?.GetValue(registry) is { } sync && RawGrammarsField?.GetValue(sync) is IDictionary trees ? trees.Count : -1;
        var own = grammar is CompiledGrammar g && GrammarRawField?.GetValue(g) is IDictionary rawTree && rawTree.Count > 3;
        var included = grammar is CompiledGrammar h && IncludedField?.GetValue(h) is IDictionary inc ? inc.Count : -1;
        return (registryTrees, own, included);
    }
}
