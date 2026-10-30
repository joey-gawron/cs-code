using System.Runtime.InteropServices;

namespace CsCode.Editor.TreeSitter.Native;

// The structs below mirror tree_sitter/api.h of libtree-sitter 0.25/0.26 field for field (all blittable, so every call is a plain
// pinvoke without marshalling stubs). Only what the highlighter needs is declared: this is deliberately a thin layer, not a binding.

[StructLayout(LayoutKind.Sequential)]
internal struct TSPoint
{
    public uint Row;
    public uint Column; // in BYTES of the input encoding (UTF-16LE here: 2 per UTF-16 code unit)

    public TSPoint(uint row, uint column)
    {
        Row = row;
        Column = column;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct TSRange
{
    public TSPoint StartPoint;
    public TSPoint EndPoint;
    public uint StartByte;
    public uint EndByte;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TSInputEdit
{
    public uint StartByte;
    public uint OldEndByte;
    public uint NewEndByte;
    public TSPoint StartPoint;
    public TSPoint OldEndPoint;
    public TSPoint NewEndPoint;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TSNode
{
    public uint Context0;
    public uint Context1;
    public uint Context2;
    public uint Context3;
    public IntPtr Id;
    public IntPtr Tree;

    public readonly bool IsNull => Id == IntPtr.Zero;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TSQueryCapture
{
    public TSNode Node;
    public uint Index;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct TSQueryMatch
{
    public uint Id;
    public ushort PatternIndex;
    public ushort CaptureCount;
    public TSQueryCapture* Captures;
}

internal enum TSQueryPredicateStepType : uint
{
    Done = 0,
    Capture = 1,
    String = 2,
}

[StructLayout(LayoutKind.Sequential)]
internal struct TSQueryPredicateStep
{
    public TSQueryPredicateStepType Type;
    public uint ValueId;
}

internal enum TSQueryError : uint
{
    None = 0,
    Syntax,
    NodeType,
    Field,
    Capture,
    Structure,
    Language,
}

internal enum TSQuantifier : uint
{
    Zero = 0,
    ZeroOrOne,
    ZeroOrMore,
    One,
    OneOrMore,
}

internal enum TSInputEncoding : uint
{
    Utf8 = 0,
    Utf16LE = 1,
    Utf16BE = 2,
    Custom = 3,
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct TSInput
{
    public void* Payload;
    public delegate* unmanaged[Cdecl]<void*, uint, TSPoint, uint*, byte*> Read;
    public TSInputEncoding Encoding;
    public void* Decode;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct TSParseState
{
    public void* Payload;
    public uint CurrentByteOffset;
    public byte HasError;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct TSParseOptions
{
    public void* Payload;
    public delegate* unmanaged[Cdecl]<TSParseState*, byte> ProgressCallback;
}

/// <summary>
/// The tree-sitter C API entry points used by CsCode. The import name <see cref="Library"/> is resolved by <see cref="TsRuntime"/>
/// (a DllImport resolver that hands out the library it found and checked), so nothing here is called before <see cref="TsRuntime.IsAvailable"/>.
///
/// Threading (tree-sitter's own rules): a parser, a tree, a query cursor are NOT thread-safe; a query and a language are immutable
/// once created and may be shared. CsCode confines all of them to the editor worker.
/// </summary>
internal static unsafe partial class TsApi
{
    public const string Library = "tree-sitter";

    // ---- parser ----
    [LibraryImport(Library)] public static partial IntPtr ts_parser_new();
    [LibraryImport(Library)] public static partial void ts_parser_delete(IntPtr parser);
    [LibraryImport(Library)] public static partial byte ts_parser_set_language(IntPtr parser, IntPtr language);
    [LibraryImport(Library)] public static partial byte ts_parser_set_included_ranges(IntPtr parser, TSRange* ranges, uint count);
    [LibraryImport(Library)] public static partial IntPtr ts_parser_parse_with_options(IntPtr parser, IntPtr oldTree, TSInput input, TSParseOptions options);
    [LibraryImport(Library)] public static partial void ts_parser_reset(IntPtr parser);

    // ---- tree ----
    [LibraryImport(Library)] public static partial IntPtr ts_tree_copy(IntPtr tree);
    [LibraryImport(Library)] public static partial void ts_tree_delete(IntPtr tree);
    [LibraryImport(Library)] public static partial TSNode ts_tree_root_node(IntPtr tree);
    [LibraryImport(Library)] public static partial void ts_tree_edit(IntPtr tree, TSInputEdit* edit);
    [LibraryImport(Library)] public static partial TSRange* ts_tree_get_changed_ranges(IntPtr oldTree, IntPtr newTree, uint* length);

    // ---- node ----
    [LibraryImport(Library)] public static partial byte* ts_node_type(TSNode node);
    [LibraryImport(Library)] public static partial ushort ts_node_symbol(TSNode node);
    [LibraryImport(Library)] public static partial uint ts_node_start_byte(TSNode node);
    [LibraryImport(Library)] public static partial uint ts_node_end_byte(TSNode node);
    [LibraryImport(Library)] public static partial TSPoint ts_node_start_point(TSNode node);
    [LibraryImport(Library)] public static partial TSPoint ts_node_end_point(TSNode node);
    [LibraryImport(Library)] public static partial byte ts_node_is_named(TSNode node);
    [LibraryImport(Library)] public static partial byte ts_node_has_error(TSNode node);
    [LibraryImport(Library)] public static partial TSNode ts_node_parent(TSNode node);
    [LibraryImport(Library)] public static partial TSNode ts_node_child(TSNode node, uint index);
    [LibraryImport(Library)] public static partial uint ts_node_child_count(TSNode node);
    [LibraryImport(Library)] public static partial uint ts_node_named_child_count(TSNode node);
    [LibraryImport(Library)] public static partial TSNode ts_node_named_child(TSNode node, uint index);
    [LibraryImport(Library)] public static partial uint ts_node_descendant_count(TSNode node);
    [LibraryImport(Library)] public static partial TSNode ts_node_named_descendant_for_byte_range(TSNode node, uint start, uint end);
    [LibraryImport(Library)] public static partial byte* ts_node_string(TSNode node);

    // ---- query ----
    [LibraryImport(Library)] public static partial IntPtr ts_query_new(IntPtr language, byte* source, uint length, uint* errorOffset, TSQueryError* errorType);
    [LibraryImport(Library)] public static partial void ts_query_delete(IntPtr query);
    [LibraryImport(Library)] public static partial uint ts_query_pattern_count(IntPtr query);
    [LibraryImport(Library)] public static partial uint ts_query_capture_count(IntPtr query);
    [LibraryImport(Library)] public static partial uint ts_query_string_count(IntPtr query);
    [LibraryImport(Library)] public static partial byte* ts_query_capture_name_for_id(IntPtr query, uint index, uint* length);
    [LibraryImport(Library)] public static partial byte* ts_query_string_value_for_id(IntPtr query, uint index, uint* length);
    [LibraryImport(Library)] public static partial TSQueryPredicateStep* ts_query_predicates_for_pattern(IntPtr query, uint patternIndex, uint* stepCount);
    [LibraryImport(Library)] public static partial TSQuantifier ts_query_capture_quantifier_for_id(IntPtr query, uint patternIndex, uint captureIndex);

    // ---- query cursor ----
    [LibraryImport(Library)] public static partial IntPtr ts_query_cursor_new();
    [LibraryImport(Library)] public static partial void ts_query_cursor_delete(IntPtr cursor);
    [LibraryImport(Library)] public static partial void ts_query_cursor_exec(IntPtr cursor, IntPtr query, TSNode node);
    [LibraryImport(Library)] public static partial byte ts_query_cursor_set_byte_range(IntPtr cursor, uint start, uint end);
    [LibraryImport(Library)] public static partial void ts_query_cursor_set_match_limit(IntPtr cursor, uint limit);
    [LibraryImport(Library)] public static partial byte ts_query_cursor_did_exceed_match_limit(IntPtr cursor);
    [LibraryImport(Library)] public static partial byte ts_query_cursor_next_capture(IntPtr cursor, TSQueryMatch* match, uint* captureIndex);
    [LibraryImport(Library)] public static partial byte ts_query_cursor_next_match(IntPtr cursor, TSQueryMatch* match);

    // ---- language ----
    [LibraryImport(Library)] public static partial uint ts_language_abi_version(IntPtr language);
    [LibraryImport(Library)] public static partial byte* ts_language_name(IntPtr language);
    [LibraryImport(Library)] public static partial uint ts_language_symbol_count(IntPtr language);

    // ---- allocator ----
    [LibraryImport(Library)]
    public static partial void ts_set_allocator(
        delegate* unmanaged[Cdecl]<nuint, void*> newMalloc,
        delegate* unmanaged[Cdecl]<nuint, nuint, void*> newCalloc,
        delegate* unmanaged[Cdecl]<void*, nuint, void*> newRealloc,
        delegate* unmanaged[Cdecl]<void*, void> newFree);

    /// <summary>A UTF-8, NUL-terminated string owned by tree-sitter (never freed by the caller) as a managed string.</summary>
    public static string? Utf8(byte* s) => s is null ? null : Marshal.PtrToStringUTF8((IntPtr)s);

    public static string Utf8(byte* s, uint length) => s is null ? string.Empty : System.Text.Encoding.UTF8.GetString(s, (int)length);
}

/// <summary>The C library's allocator, for the optional allocation accounting and for measuring native heap use in benchmarks.</summary>
internal static unsafe partial class LibC
{
    private const string Library = "libc.so.6";

    [LibraryImport(Library)] public static partial void* malloc(nuint size);
    [LibraryImport(Library)] public static partial void* calloc(nuint count, nuint size);
    [LibraryImport(Library)] public static partial void* realloc(void* ptr, nuint size);
    [LibraryImport(Library)] public static partial void free(void* ptr);
    [LibraryImport(Library)] public static partial nuint malloc_usable_size(void* ptr);
    [LibraryImport(Library)] public static partial MallInfo2 mallinfo2();
    [LibraryImport(Library)] public static partial int malloc_trim(nuint pad);

    [StructLayout(LayoutKind.Sequential)]
    public struct MallInfo2
    {
        public nuint Arena;
        public nuint Ordblks;
        public nuint Smblks;
        public nuint Hblks;
        public nuint Hblkhd;
        public nuint Usmblks;
        public nuint Fsmblks;
        public nuint Uordblks;
        public nuint Fordblks;
        public nuint Keepcost;
    }
}
