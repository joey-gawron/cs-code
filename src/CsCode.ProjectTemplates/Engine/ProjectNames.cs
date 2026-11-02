using System.Text;
using System.Text.RegularExpressions;

namespace CsCode.ProjectTemplates.Engine;

/// <summary>
/// The name rules of each toolchain, checked before anything runs, so the page says what is wrong under the Name box instead of a tool failing
/// half-way. Each returns null for a valid name, else one sentence. Every name is also a folder name (<see cref="Folder"/>).
/// </summary>
public static partial class ProjectNames
{
    private static readonly HashSet<string> WindowsDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    /// <summary>A folder name that works on every platform: no separators or characters Windows refuses, no device names, no leading or trailing blanks or dots.</summary>
    public static string? Folder(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Enter a project name.";
        if (name != name.Trim()) return "The name cannot start or end with a space.";
        if (name is "." or ".." || name.EndsWith('.')) return "The name cannot end with a dot.";
        if (name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 || name.Any(char.IsControl)) return "The name cannot contain / \\ : * ? \" < > | or control characters.";
        if (WindowsDeviceNames.Contains(name.Split('.')[0])) return $"'{name}' is a reserved device name on Windows.";
        if (name.Length > 100) return "The name is too long (100 characters at most).";
        return null;
    }

    // ---- Rust (cargo's rules: cargo/src/cargo/util/restricted_names.rs and `cargo new`'s checks, checked with cargo 1.96.1)
    private static readonly HashSet<string> RustKeywords = new(StringComparer.Ordinal)
    {
        "Self", "abstract", "as", "async", "await", "become", "box", "break", "const", "continue", "crate", "do", "dyn", "else", "enum", "extern", "false",
        "final", "fn", "for", "gen", "if", "impl", "in", "let", "loop", "macro", "match", "mod", "move", "mut", "override", "priv", "pub", "ref", "return",
        "self", "static", "struct", "super", "trait", "true", "try", "type", "typeof", "unsafe", "unsized", "use", "virtual", "where", "while", "yield",
    };
    private static readonly HashSet<string> RustReserved = new(StringComparer.Ordinal) { "test", "core", "std", "alloc", "proc_macro", "proc-macro", "deps", "examples", "build", "incremental" };

    /// <summary>A cargo package name: ASCII letters, digits, <c>-</c> and <c>_</c>, not starting with a digit, not a keyword or a name cargo reserves.</summary>
    public static string? Rust(string name)
    {
        if (Folder(name) is { } bad) return bad;
        if (!RustName().IsMatch(name)) return "A Rust package name uses letters, digits, - and _ and does not start with a digit.";
        if (RustKeywords.Contains(name)) return $"'{name}' is a Rust keyword.";
        if (RustReserved.Contains(name)) return $"cargo reserves the name '{name}' (it conflicts with Rust's built-in libraries or cargo's build directories).";
        if (name.Length > 64) return "crates.io allows 64 characters at most.";
        return null;
    }

    // ---- Python (PEP 508 project names; the import package is the normalised name, which must be an identifier)
    private static readonly HashSet<string> PythonKeywords = new(StringComparer.Ordinal)
    {
        "False", "None", "True", "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del", "elif", "else", "except", "finally",
        "for", "from", "global", "if", "import", "in", "is", "lambda", "nonlocal", "not", "or", "pass", "raise", "return", "try", "while", "with", "yield",
    };

    /// <summary>A Python project name (PEP 508: letters, digits, <c>-</c> <c>_</c> <c>.</c>, starting and ending with a letter or digit) whose package name is an identifier.</summary>
    public static string? Python(string name)
    {
        if (Folder(name) is { } bad) return bad;
        if (!PythonName().IsMatch(name)) return "A Python project name uses letters, digits, - _ and . and starts and ends with a letter or digit.";
        var package = PythonPackage(name);
        if (char.IsDigit(package[0])) return $"The package name '{package}' would start with a digit, which Python cannot import.";
        if (PythonKeywords.Contains(package)) return $"'{package}' is a Python keyword.";
        if (package is "test" or "tests") return $"'{package}' would clash with the tests folder.";
        return null;
    }

    /// <summary>The import package of a Python project name: runs of <c>-</c> <c>_</c> <c>.</c> become one <c>_</c>, lower case (PEP 503 with <c>_</c>).</summary>
    public static string PythonPackage(string name) => PythonSeparators().Replace(name, "_").ToLowerInvariant();

    // ---- C# (each dot-separated part an identifier and not a keyword: the name is the project's root namespace)
    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue", "decimal", "default", "delegate",
        "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in",
        "int", "interface", "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override", "params", "private",
        "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this",
        "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
    };

    /// <summary>A C# project name: dot-separated identifiers (it becomes the root namespace and the assembly name), none a keyword.</summary>
    public static string? CSharp(string name)
    {
        if (Folder(name) is { } bad) return bad;
        foreach (var part in name.Split('.'))
        {
            if (!CSharpIdentifier().IsMatch(part)) return "A C# project name is identifiers separated by dots: letters, digits and _, not starting with a digit.";
            if (CSharpKeywords.Contains(part)) return $"'{part}' is a C# keyword.";
        }
        return null;
    }

    // ---- CMake (the name is the project and its target)
    private static readonly HashSet<string> CMakeReservedTargets = new(StringComparer.OrdinalIgnoreCase) { "all", "clean", "help", "install", "package", "package_source", "test", "edit_cache", "rebuild_cache", "list_install_components" };

    /// <summary>A CMake project and target name: letters, digits, <c>-</c> and <c>_</c>, starting with a letter or <c>_</c>, not a target CMake reserves.</summary>
    public static string? CMake(string name)
    {
        if (Folder(name) is { } bad) return bad;
        if (!CMakeName().IsMatch(name)) return "A CMake project name uses letters, digits, - and _ and starts with a letter or _.";
        if (CMakeReservedTargets.Contains(name)) return $"CMake reserves the target name '{name}'.";
        return null;
    }

    /// <summary>The C identifier made of a CMake name (<c>-</c> becomes <c>_</c>, lower case): the prefix of the generated functions and the header's name.</summary>
    public static string CIdentifier(string name) => name.Replace('-', '_').ToLowerInvariant();

    // ---- Zig (zig init uses the folder name as the package name, an identifier of at most 32 bytes since 0.14)
    private static readonly HashSet<string> ZigKeywords = new(StringComparer.Ordinal)
    {
        "addrspace", "align", "allowzero", "and", "anyframe", "anytype", "asm", "break", "callconv", "catch", "comptime", "const", "continue", "defer",
        "else", "enum", "errdefer", "error", "export", "extern", "fn", "for", "if", "inline", "linksection", "noalias", "noinline", "nosuspend", "opaque",
        "or", "orelse", "packed", "pub", "resume", "return", "struct", "suspend", "switch", "test", "threadlocal", "try", "union", "unreachable", "var",
        "volatile", "while",
    };

    /// <summary>A Zig package name: an identifier (letters, digits, _; not starting with a digit), not a keyword, 32 characters at most.</summary>
    public static string? Zig(string name)
    {
        if (Folder(name) is { } bad) return bad;
        if (!ZigName().IsMatch(name)) return "A Zig package name uses letters, digits and _ and does not start with a digit.";
        if (ZigKeywords.Contains(name)) return $"'{name}' is a Zig keyword.";
        if (Encoding.UTF8.GetByteCount(name) > 32) return "A Zig package name has 32 characters at most.";
        return null;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_-]*$")]
    private static partial Regex RustName();

    [GeneratedRegex("^([A-Za-z0-9]|[A-Za-z0-9][A-Za-z0-9._-]*[A-Za-z0-9])$")]
    private static partial Regex PythonName();

    [GeneratedRegex("[-_.]+")]
    private static partial Regex PythonSeparators();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex CSharpIdentifier();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_-]*$")]
    private static partial Regex CMakeName();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex ZigName();
}
