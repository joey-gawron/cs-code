using System.Text;

namespace CsCode.ProjectTemplates.Templates;

/// <summary>The files every generator adds for a good start: <c>.gitignore</c>, <c>.editorconfig</c> and <c>README.md</c>, per language.</summary>
public static class CommonFiles
{
    private const string OsAndEditors = """
        # OS and editor files
        .DS_Store
        Thumbs.db
        *.swp
        *~
        .idea/
        """;

    public const string PythonIgnore = """
        # Python
        __pycache__/
        *.py[cod]
        .venv/
        build/
        dist/
        *.egg-info/
        .pytest_cache/
        .ruff_cache/
        .mypy_cache/
        .coverage
        htmlcov/
        """;

    public const string RustIgnore = """
        # Rust
        /target
        """;

    public const string CMakeIgnore = """
        # CMake
        /build/
        /out/
        CMakeUserPresets.json
        .cache/
        """;

    public const string ZigIgnore = """
        # Zig
        .zig-cache/
        zig-out/
        """;

    /// <summary>A <c>.gitignore</c> of the language's lines (null: none) and the usual OS and editor files.</summary>
    public static string GitIgnore(string? language) => language is null ? OsAndEditors : language + "\n\n" + OsAndEditors;

    /// <summary>
    /// An <c>.editorconfig</c>: UTF-8, LF, final newline, trimmed trailing blanks, four-space indents (two for JSON, YAML and TOML), plus the
    /// language's own <paramref name="sections"/>.
    /// </summary>
    public static string EditorConfig(string? sections = null)
    {
        var sb = new StringBuilder("""
            # https://editorconfig.org
            root = true

            [*]
            charset = utf-8
            end_of_line = lf
            insert_final_newline = true
            trim_trailing_whitespace = true
            indent_style = space
            indent_size = 4

            [*.{json,jsonc,yml,yaml,toml}]
            indent_size = 2

            [*.md]
            trim_trailing_whitespace = false

            """);
        if (sections is { Length: > 0 }) sb.Append('\n').Append(sections.TrimEnd()).Append('\n');
        return sb.ToString();
    }

    /// <summary>A README: the name, one line on what it is, and how to build, run and test it from CsCode and from a shell.</summary>
    public static string Readme(string name, string summary, IReadOnlyList<(string What, string Command)> commands)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(name).Append("\n\n").Append(summary).Append("\n\n");
        sb.Append("## Getting started\n\n");
        sb.Append("In CsCode, **F5** builds and debugs the project (`.vscode/launch.json`), **Terminal ▸ Run Build Task…** builds it and the Test ")
            .Append("Explorer runs its tests (`.vscode/tasks.json` holds the tasks).\n");
        if (commands.Count > 0)
        {
            sb.Append("\nFrom a shell:\n\n");
            foreach (var (what, command) in commands) sb.Append("* ").Append(what).Append(": `").Append(command).Append("`\n");
        }
        return sb.ToString();
    }
}
