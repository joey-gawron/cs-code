using System.Collections.Concurrent;

namespace CsCode.Editor.TreeSitter.Highlighting;

/// <summary>
/// Read access to an immutable text (a document snapshot), in UTF-16 code units. Must be safe to call from the editor worker while the UI
/// edits the live buffer, i.e. backed by a snapshot. Lines follow the editor's model (<c>\r\n</c>, <c>\n</c> and <c>\r</c> all end a line).
/// </summary>
public interface ITextInput
{
    int Length { get; }

    /// <summary>Copies <c>destination.Length</c> characters starting at <paramref name="offset"/> (the caller keeps the range inside the text).</summary>
    void CopyTo(int offset, Span<char> destination);

    int LineCount { get; }

    /// <summary>Offset of the first character of <paramref name="line"/> (clamped).</summary>
    int LineStart(int line);

    /// <summary>The line containing <paramref name="offset"/>.</summary>
    int LineOfOffset(int offset);
}

/// <summary>
/// Interns TextMate-style scope stacks into small integer ids (the editor's <c>ScopeInterner</c>), so tree-sitter output lands in the same
/// <c>(startColumn, scopeStackId)</c> token rows as TextMate output and is coloured by the same theme machinery.
/// Called on the editor worker only.
/// </summary>
public interface IScopeStacks
{
    /// <summary>The id of the stack <c>[scope]</c>.</summary>
    int Root(int scopeKey);

    /// <summary>The id of <c>stack(parentId) + [scope]</c>.</summary>
    int Push(int parentId, int scopeKey);
}

/// <summary>Process-wide registry of scope strings ↔ small keys (so per-capture scope lookups never hash strings in the hot path).</summary>
public static class ScopeNames
{
    private static readonly ConcurrentDictionary<string, int> Keys = new(StringComparer.Ordinal);
    private static readonly List<string> Names = new();
    private static readonly object Gate = new();

    public static int KeyOf(string scope)
    {
        if (Keys.TryGetValue(scope, out var key)) return key;
        lock (Gate)
        {
            if (Keys.TryGetValue(scope, out key)) return key;
            key = Names.Count;
            Names.Add(scope);
            Keys[scope] = key;
            return key;
        }
    }

    public static string NameOf(int key)
    {
        lock (Gate) return Names[key];
    }
}

/// <summary>Result of one parse slice.</summary>
public enum ParseOutcome
{
    /// <summary>The tree for the text is complete.</summary>
    Completed,
    /// <summary>The slice budget ran out or a newer edit cancelled it: call <c>Parse</c> again (same text) to resume, or edit/reset.</summary>
    Halted,
}

/// <summary>
/// One edit in the editor's coordinates (UTF-16 offsets and line/character positions, the latter against the text BEFORE the edit for the
/// start/old end), as the editor's <c>TextContentChange</c> carries them.
/// </summary>
public readonly record struct TextEditInfo(int StartOffset, int OldEndOffset, int NewEndOffset, int StartLine, int StartCharacter, int OldEndLine, int OldEndCharacter, int NewEndLine, int NewEndCharacter);
