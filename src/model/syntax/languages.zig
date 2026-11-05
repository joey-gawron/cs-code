//! Static language table: how files map to grammars, and per-language
//! editing conventions (comments).

const std = @import("std");
const rope_mod = @import("../text/rope.zig");
const theme_mod = @import("../../framework/ui/theme.zig");
const syntax_mod = @import("syntax.zig");
const org_highlight = @import("../org/highlight.zig");
const agenda = @import("../org/agenda.zig");
const search = @import("../project/search.zig");
const git_status = @import("../git/status.zig");
const injection = @import("injection.zig");

/// Highlighter for languages without a tree-sitter grammar.
pub const CustomHighlight = *const fn (std.mem.Allocator, *const rope_mod.Rope, usize, usize, *const theme_mod.Syntax, *std.ArrayList(syntax_mod.HighlightSpan)) void;

pub const LanguageConfig = struct {
    /// Grammar directory and `tree_sitter_<grammar>` symbol; null for languages
    /// we highlight without tree-sitter.
    grammar: ?[]const u8,
    /// Human name, as shown in the status bar and "Set Syntax:" commands.
    name: []const u8,
    extensions: []const []const u8 = &.{},
    /// Base names ("Makefile"), or path tails when they contain a slash
    /// (".git/config"). `*` matches any run of characters within a component
    /// ("Dockerfile.*", "requirements*.txt").
    filenames: []const []const u8 = &.{},
    line_comment: ?[]const u8 = null,
    block_comment: ?[2][]const u8 = null,
    /// Highlight queries of these grammars are prepended (like nvim's "; inherits").
    query_inherits: []const []const u8 = &.{},
    /// ... and of these appended (like nvim's "; extends").
    query_extends: []const []const u8 = &.{},
    /// Whether a later highlight pattern overrides an earlier one on the same
    /// node (nvim-treesitter's convention); null guesses from the query.
    query_last_wins: ?bool = null,
    /// Python-style: indent after a trailing ':'.
    indent_after_colon: bool = false,
    custom_highlight: ?CustomHighlight = null,
};

pub const plain_text: LanguageConfig = .{ .grammar = null, .name = "Plain Text" };

const c_comments: [2][]const u8 = .{ "/* ", " */" };
const xml_comments: [2][]const u8 = .{ "<!-- ", " -->" };
const ml_comments: [2][]const u8 = .{ "(* ", " *)" };
const haskell_comments: [2][]const u8 = .{ "{- ", " -}" };
const lisp_comments: [2][]const u8 = .{ "#| ", " |#" };

// The first block, Crystal, Cython, Dune, Emacs Lisp, Less and Standard ML use
// nixpkgs' grammar packages and their own queries; the rest come from
// nvim-treesitter, whose parsers are built at the revisions its queries are
// written for (see `grammars` in flake.nix). Those queries let later patterns
// win, and inherit nvim's own C/C++/CSS/HTML/Go queries, which the farm keeps
// under "nvim/<lang>".
pub const all = [_]LanguageConfig{
    .{ .grammar = "zig", .name = "Zig", .extensions = &.{ "zig", "zon" }, .line_comment = "// " },
    .{ .grammar = "c", .name = "C", .extensions = &.{ "c", "h" }, .line_comment = "// ", .block_comment = c_comments },
    .{ .grammar = "cpp", .name = "C++", .extensions = &.{ "cpp", "cc", "cxx", "c++", "hpp", "hh", "hxx", "h++", "ipp", "inl", "tpp", "cppm", "ixx" }, .line_comment = "// ", .block_comment = c_comments, .query_inherits = &.{"c"} },
    .{ .grammar = "rust", .name = "Rust", .extensions = &.{"rs"}, .line_comment = "// ", .block_comment = c_comments },
    .{ .grammar = "python", .name = "Python", .extensions = &.{ "py", "pyi", "pyw" }, .filenames = &.{ "SConstruct", "SConscript" }, .line_comment = "# ", .indent_after_colon = true },
    .{ .grammar = "javascript", .name = "JavaScript", .extensions = &.{ "js", "mjs", "cjs", "jsx" }, .line_comment = "// ", .block_comment = c_comments, .query_extends = &.{ "javascript_jsx", "javascript_params" } },
    .{ .grammar = "typescript", .name = "TypeScript", .extensions = &.{ "ts", "mts", "cts" }, .line_comment = "// ", .block_comment = c_comments, .query_inherits = &.{"javascript"} },
    .{ .grammar = "tsx", .name = "TypeScriptReact", .extensions = &.{"tsx"}, .line_comment = "// ", .block_comment = c_comments, .query_inherits = &.{ "javascript", "typescript" }, .query_extends = &.{"javascript_jsx"} },
    .{ .grammar = "json", .name = "JSON", .extensions = &.{ "json", "jsonc", "jsonl", "jsonld", "geojson", "topojson", "webmanifest", "har", "ipynb", "code-workspace", "sublime-settings", "sublime-keymap" }, .filenames = &.{ "flake.lock", "composer.lock", ".babelrc", ".jshintrc", ".swcrc", ".watchmanconfig" } },
    .{ .grammar = "bash", .name = "Shell Script (Bash)", .extensions = &.{ "sh", "bash", "zsh", "ksh", "bats", "ebuild", "eclass" }, .filenames = &.{ ".bashrc", ".bash_profile", ".bash_aliases", ".bash_logout", ".zshrc", ".zshenv", ".zprofile", ".zlogin", ".zlogout", ".profile", ".envrc", ".xinitrc", ".xprofile", "PKGBUILD", "APKBUILD" }, .line_comment = "# " },
    .{ .grammar = "go", .name = "Go", .extensions = &.{"go"}, .line_comment = "// ", .block_comment = c_comments },
    .{ .grammar = "nix", .name = "Nix", .extensions = &.{"nix"}, .line_comment = "# ", .block_comment = c_comments },
    .{ .grammar = "lua", .name = "Lua", .extensions = &.{ "lua", "rockspec" }, .filenames = &.{".luacheckrc"}, .line_comment = "-- ", .block_comment = .{ "--[[ ", " ]]" } },
    .{ .grammar = "toml", .name = "TOML", .extensions = &.{"toml"}, .filenames = &.{ "Cargo.lock", "Pipfile", "poetry.lock", "uv.lock", "pdm.lock", ".cargo/config" }, .line_comment = "# " },
    .{ .grammar = "yaml", .name = "YAML", .extensions = &.{ "yml", "yaml", "cff" }, .filenames = &.{ ".clang-format", "_clang-format", ".clang-tidy", ".clangd", ".yamllint" }, .line_comment = "# " },
    .{ .grammar = "html", .name = "HTML", .extensions = &.{ "html", "htm", "xhtml", "shtml" }, .block_comment = xml_comments },
    .{ .grammar = "css", .name = "CSS", .extensions = &.{"css"}, .block_comment = c_comments },
    .{ .grammar = "make", .name = "Makefile", .extensions = &.{ "mk", "mak" }, .filenames = &.{ "Makefile", "makefile", "GNUmakefile", "Makefile.*" }, .line_comment = "# " },
    .{ .grammar = "markdown", .name = "Markdown", .extensions = &.{ "md", "markdown", "mdown", "mkd" }, .block_comment = xml_comments },
    .{ .grammar = null, .name = "Org", .extensions = &.{"org"}, .line_comment = "# ", .custom_highlight = org_highlight.highlight },

    // ---- Applications and systems programming ----------------------------------
    .{ .grammar = "java", .name = "Java", .extensions = &.{ "java", "jav" }, .line_comment = "// ", .block_comment = c_comments, .query_last_wins = true },
    .{ .grammar = "kotlin", .name = "Kotlin", .extensions = &.{ "kt", "kts" }, .line_comment = "// ", .block_comment = c_comments, .query_last_wins = true },
    .{ .grammar = "scala", .name = "Scala", .extensions = &.{ "scala", "sc", "sbt", "mill" }, .line_comment = "// ", .block_comment = c_comments, .query_last_wins = true },
    .{ .grammar = "groovy", .name = "Groovy", .extensions = &.{ "groovy", "gradle", "gvy", "gy", "gsh" }, .filenames = &.{"Jenkinsfile"}, .line_comment = "// ", .block_comment = c_comments, .query_last_wins = true },
    .{ .grammar = "c_sharp", .name = "C#", .extensions = &.{ "cs", "csx", "cake" }, .line_comment = "// ", .block_comment = c_comments, .query_last_wins = true },
    .{ .grammar = "fsharp", .name = "F#", .extensions = &.{ "fs", "fsi", "fsx", "fsscript" }, .line_comment = "// ", .block_comment = ml_comments, .query_last_wins = true },
    .{ .grammar = "swift", .name = "Swift", .extensions = &.{"swift"}, .line_comment = "// ", .block_comment = c_comments, .query_last_wins = true },
    .{ .grammar = "objc", .name = "Objective-C", .extensions = &.{ "m", "mm" }, .line_comment = "// ", .block_comment = c_comments, .query_inherits = &.{"nvim/c"}, .query_last_wins = true },
    .{ .grammar = "dart", .name = "Dart", .extensions = &.{"dart"}, .line_comment = "// ", .block_comment = c_comments, .query_last_wins = true },
    .{ .grammar = "d", .name = "D", .extensions = &.{ "d", "di" }, .line_comment = "// ", .block_comment = c_comments, .query_last_wins = true },
    .{ .grammar = "nim", .name = "Nim", .extensions = &.{ "nim", "nims", "nimble" }, .line_comment = "# ", .block_comment = .{ "#[ ", " ]#" }, .query_last_wins = true, .indent_after_colon = true },
    .{ .grammar = "odin", .name = "Odin", .extensions = &.{"odin"}, .line_comment = "// ", .block_comment = c_comments, .query_last_wins = true },
    .{ .grammar = "hare", .name = "Hare", .extensions = &.{"ha"}, .line_comment = "// ", .query_last_wins = true },
    .{ .grammar = "vala", .name = "Vala", .extensions = &.{ "vala", "vapi" }, .line_comment = "// ", .block_comment = c_comments, .query_last_wins = true },
    .{ .grammar = "crystal", .name = "Crystal", .extensions = &.{"cr"}, .line_comment = "# ", .query_last_wins = true },
    .{ .grammar = "fortran", .name = "Fortran", .extensions = &.{ "f90", "f95", "f03", "f08", "f18", "f", "for", "ftn", "fpp" }, .line_comment = "! ", .query_last_wins = true },
    .{ .grammar = "ada", .name = "Ada", .extensions = &.{ "adb", "ads", "ada" }, .line_comment = "-- ", .query_last_wins = true },
    .{ .grammar = "pascal", .name = "Pascal", .extensions = &.{ "pas", "dpr", "lpr", "pascal" }, .line_comment = "// ", .block_comment = .{ "{ ", " }" }, .query_last_wins = true },
    .{ .grammar = "solidity", .name = "Solidity", .extensions = &.{"sol"}, .line_comment = "// ", .block_comment = c_comments, .query_last_wins = true },

    // ---- Scripting -----------------------------------------------------------------
    .{ .grammar = "ruby", .name = "Ruby", .extensions = &.{ "rb", "rake", "gemspec", "ru", "rbw", "jbuilder", "podspec", "thor", "irb" }, .filenames = &.{ "Gemfile", "Rakefile", "Guardfile", "Podfile", "Vagrantfile", "Brewfile", "Capfile", "Fastfile", "Appfile", "Berksfile", "Thorfile", "Dangerfile", "Steepfile", ".irbrc", ".pryrc" }, .line_comment = "# ", .query_last_wins = true },
    .{ .grammar = "php", .name = "PHP", .extensions = &.{ "php", "phtml", "php3", "php4", "php5", "php7", "php8", "phps", "phpt" }, .line_comment = "// ", .block_comment = c_comments, .query_inherits = &.{"php_only"}, .query_last_wins = true },
    .{ .grammar = "perl", .name = "Perl", .extensions = &.{ "pl", "pm", "t", "psgi", "cgi" }, .filenames = &.{"cpanfile"}, .line_comment = "# ", .query_last_wins = true },
    .{ .grammar = "tcl", .name = "Tcl", .extensions = &.{ "tcl", "tk", "tm", "itcl" }, .line_comment = "# ", .query_last_wins = true },
    .{ .grammar = "awk", .name = "AWK", .extensions = &.{ "awk", "gawk" }, .line_comment = "# ", .query_last_wins = true },
    .{ .grammar = "powershell", .name = "PowerShell", .extensions = &.{ "ps1", "psm1", "psd1" }, .line_comment = "# ", .block_comment = .{ "<# ", " #>" }, .query_last_wins = true },
    .{ .grammar = "fish", .name = "Fish", .extensions = &.{"fish"}, .line_comment = "# ", .query_last_wins = true },
    .{ .grammar = "nu", .name = "Nushell", .extensions = &.{"nu"}, .line_comment = "# ", .query_last_wins = true },
    .{ .grammar = "vim", .name = "Vim Script", .extensions = &.{"vim"}, .filenames = &.{ ".vimrc", "_vimrc", ".gvimrc", "_gvimrc", ".exrc", ".nvimrc" }, .line_comment = "\" ", .query_last_wins = true },
    .{ .grammar = "luau", .name = "Luau", .extensions = &.{"luau"}, .line_comment = "-- ", .block_comment = .{ "--[[ ", " ]]" }, .query_last_wins = true },
    .{ .grammar = "teal", .name = "Teal", .extensions = &.{"tl"}, .line_comment = "-- ", .block_comment = .{ "--[[ ", " ]]" }, .query_last_wins = true },
    .{ .grammar = "gdscript", .name = "GDScript", .extensions = &.{"gd"}, .line_comment = "# ", .query_last_wins = true, .indent_after_colon = true },
    .{ .grammar = "cython", .name = "Cython", .extensions = &.{ "pyx", "pxd", "pxi" }, .line_comment = "# ", .indent_after_colon = true },
    .{ .grammar = "starlark", .name = "Starlark", .extensions = &.{ "bzl", "star", "bazel", "sky" }, .filenames = &.{ "BUILD", "WORKSPACE", "BUCK", "Tiltfile" }, .line_comment = "# ", .query_last_wins = true, .indent_after_colon = true },

    // ---- Functional --------------------------------------------------------------
    .{ .grammar = "haskell", .name = "Haskell", .extensions = &.{ "hs", "hs-boot", "hsc" }, .line_comment = "-- ", .block_comment = haskell_comments, .query_last_wins = true },
    .{ .grammar = "purescript", .name = "PureScript", .extensions = &.{"purs"}, .line_comment = "-- ", .block_comment = haskell_comments, .query_last_wins = true },
    .{ .grammar = "elm", .name = "Elm", .extensions = &.{"elm"}, .line_comment = "-- ", .block_comment = haskell_comments, .query_last_wins = true },
    .{ .grammar = "ocaml", .name = "OCaml", .extensions = &.{"ml"}, .block_comment = ml_comments, .query_last_wins = true },
    .{ .grammar = "ocaml_interface", .name = "OCaml Interface", .extensions = &.{"mli"}, .block_comment = ml_comments, .query_last_wins = true },
    .{ .grammar = "dune", .name = "Dune", .filenames = &.{ "dune", "dune-project", "dune-workspace" }, .line_comment = "; " },
    .{ .grammar = "sml", .name = "Standard ML", .extensions = &.{ "sml", "sig", "fun" }, .block_comment = ml_comments },
    .{ .grammar = "rescript", .name = "ReScript", .extensions = &.{ "res", "resi" }, .line_comment = "// ", .block_comment = c_comments, .query_last_wins = true },
    .{ .grammar = "elixir", .name = "Elixir", .extensions = &.{ "ex", "exs" }, .filenames = &.{"mix.lock"}, .line_comment = "# ", .query_last_wins = true },
    .{ .grammar = "heex", .name = "HEEx", .extensions = &.{"heex"}, .block_comment = .{ "<%!-- ", " --%>" }, .query_last_wins = true },
    .{ .grammar = "erlang", .name = "Erlang", .extensions = &.{ "erl", "hrl", "escript" }, .filenames = &.{ "rebar.config", "rebar.lock" }, .line_comment = "% ", .query_last_wins = true },
    .{ .grammar = "gleam", .name = "Gleam", .extensions = &.{"gleam"}, .line_comment = "// ", .query_last_wins = true },
    .{ .grammar = "clojure", .name = "Clojure", .extensions = &.{ "clj", "cljs", "cljc", "cljd", "edn", "bb" }, .line_comment = ";; ", .query_last_wins = true },
    .{ .grammar = "commonlisp", .name = "Common Lisp", .extensions = &.{ "lisp", "lsp", "cl", "asd" }, .line_comment = ";; ", .block_comment = lisp_comments, .query_last_wins = true },
    .{ .grammar = "scheme", .name = "Scheme", .extensions = &.{ "scm", "ss", "sld", "sls", "sps" }, .line_comment = ";; ", .block_comment = lisp_comments, .query_last_wins = true },
    .{ .grammar = "racket", .name = "Racket", .extensions = &.{ "rkt", "rktl", "rktd", "scrbl" }, .line_comment = ";; ", .block_comment = lisp_comments, .query_last_wins = true },
    .{ .grammar = "elisp", .name = "Emacs Lisp", .extensions = &.{"el"}, .filenames = &.{ ".emacs", "_emacs", "Cask" }, .line_comment = ";; " },
    .{ .grammar = "fennel", .name = "Fennel", .extensions = &.{"fnl"}, .line_comment = ";; ", .query_last_wins = true },
    .{ .grammar = "janet_simple", .name = "Janet", .extensions = &.{ "janet", "jdn" }, .line_comment = "# ", .query_last_wins = true },

    // ---- Data science ------------------------------------------------------------
    .{ .grammar = "r", .name = "R", .extensions = &.{ "r", "rprofile" }, .filenames = &.{".Rprofile"}, .line_comment = "# ", .query_last_wins = true },
    .{ .grammar = "julia", .name = "Julia", .extensions = &.{"jl"}, .line_comment = "# ", .block_comment = .{ "#= ", " =#" }, .query_last_wins = true },
    .{ .grammar = "sql", .name = "SQL", .extensions = &.{ "sql", "ddl", "pgsql" }, .line_comment = "-- ", .block_comment = c_comments, .query_last_wins = true },

    // ---- Web -----------------------------------------------------------------------
    .{ .grammar = "vue", .name = "Vue", .extensions = &.{"vue"}, .block_comment = xml_comments, .query_inherits = &.{"html_tags"}, .query_last_wins = true },
    .{ .grammar = "svelte", .name = "Svelte", .extensions = &.{"svelte"}, .block_comment = xml_comments, .query_inherits = &.{ "html_tags", "nvim/html" }, .query_last_wins = true },
    .{ .grammar = "astro", .name = "Astro", .extensions = &.{"astro"}, .block_comment = xml_comments, .query_inherits = &.{"html_tags"}, .query_last_wins = true },
    .{ .grammar = "glimmer", .name = "Handlebars", .extensions = &.{ "hbs", "handlebars" }, .block_comment = .{ "{{!-- ", " --}}" }, .query_last_wins = true },
    .{ .grammar = "pug", .name = "Pug", .extensions = &.{ "pug", "jade" }, .line_comment = "//- ", .query_last_wins = true },
    .{ .grammar = "scss", .name = "SCSS", .extensions = &.{"scss"}, .line_comment = "// ", .block_comment = c_comments, .query_inherits = &.{"nvim/css"}, .query_last_wins = true },
    .{ .grammar = "less", .name = "Less", .extensions = &.{"less"}, .line_comment = "// ", .block_comment = c_comments, .query_inherits = &.{"nvim/css"}, .query_last_wins = true },
    .{ .grammar = "graphql", .name = "GraphQL", .extensions = &.{ "graphql", "graphqls", "gql" }, .line_comment = "# ", .query_last_wins = true },
    .{ .grammar = "prisma", .name = "Prisma", .extensions = &.{"prisma"}, .line_comment = "// ", .query_last_wins = true },
    .{ .grammar = "http", .name = "HTTP", .extensions = &.{"http"}, .line_comment = "# ", .query_last_wins = true },
    .{ .grammar = "templ", .name = "Templ", .extensions = &.{"templ"}, .line_comment = "// ", .block_comment = c_comments, .query_inherits = &.{"nvim/go"}, .query_last_wins = true },

    // ---- Templates: the host language's code is injected into them -------------
    .{ .grammar = "embedded_template", .name = "ERB", .extensions = &.{ "erb", "rhtml" }, .block_comment = .{ "<%# ", " %>" }, .query_last_wins = true },
    .{ .grammar = "eex", .name = "EEx", .extensions = &.{ "eex", "leex" }, .block_comment = .{ "<%!-- ", " --%>" }, .query_last_wins = true },
    .{ .grammar = "jinja", .name = "Jinja", .extensions = &.{ "jinja", "jinja2", "j2" }, .block_comment = .{ "{# ", " #}" }, .query_inherits = &.{"jinja_inline"}, .query_last_wins = true },
    .{ .grammar = "htmldjango", .name = "Django Template", .extensions = &.{"djhtml"}, .block_comment = .{ "{# ", " #}" }, .query_last_wins = true },
    .{ .grammar = "twig", .name = "Twig", .extensions = &.{"twig"}, .block_comment = .{ "{# ", " #}" }, .query_last_wins = true },
    .{ .grammar = "liquid", .name = "Liquid", .extensions = &.{"liquid"}, .block_comment = .{ "{% comment %}", "{% endcomment %}" }, .query_last_wins = true },
    .{ .grammar = "blade", .name = "Blade", .filenames = &.{"*.blade.php"}, .block_comment = .{ "{{-- ", " --}}" }, .query_inherits = &.{ "html_tags", "nvim/html" }, .query_last_wins = true },
    .{ .grammar = "razor", .name = "Razor", .extensions = &.{ "cshtml", "razor" }, .block_comment = .{ "@* ", " *@" }, .query_inherits = &.{"c_sharp"}, .query_last_wins = true },
    .{ .grammar = "angular", .name = "Angular Template", .filenames = &.{"*.component.html"}, .block_comment = xml_comments, .query_inherits = &.{"html_tags"}, .query_last_wins = true },
    .{ .grammar = "gotmpl", .name = "Go Template", .extensions = &.{ "gotmpl", "gohtml" }, .block_comment = .{ "{{/* ", " */}}" }, .query_last_wins = true },

    // ---- Markup and documents --------------------------------------------------
    .{ .grammar = "xml", .name = "XML", .extensions = &.{ "xml", "xsd", "xsl", "xslt", "svg", "plist", "csproj", "fsproj", "vbproj", "vcxproj", "props", "targets", "xaml", "rss", "atom", "wsdl", "xliff", "xlf", "kml", "gpx", "resx", "nuspec", "glade", "ui", "xib", "storyboard", "xul", "opml", "fxml", "mxml", "tmTheme", "tmLanguage", "tmPreferences", "tmSnippet", "sublime-snippet" }, .block_comment = xml_comments, .query_last_wins = true },
    .{ .grammar = "dtd", .name = "DTD", .extensions = &.{ "dtd", "ent" }, .block_comment = xml_comments, .query_last_wins = true },
    .{ .grammar = "latex", .name = "LaTeX", .extensions = &.{ "tex", "ltx", "latex", "sty", "cls", "dtx" }, .line_comment = "% ", .query_last_wins = true },
    .{ .grammar = "bibtex", .name = "BibTeX", .extensions = &.{ "bib", "bibtex" }, .line_comment = "% ", .query_last_wins = true },
    .{ .grammar = "typst", .name = "Typst", .extensions = &.{"typ"}, .line_comment = "// ", .block_comment = c_comments, .query_last_wins = true },
    .{ .grammar = "rst", .name = "reStructuredText", .extensions = &.{ "rst", "rest" }, .line_comment = ".. ", .query_last_wins = true },
    .{ .grammar = "dot", .name = "Graphviz (DOT)", .extensions = &.{ "dot", "gv" }, .line_comment = "// ", .block_comment = c_comments, .query_last_wins = true },
    .{ .grammar = "po", .name = "Gettext PO", .extensions = &.{ "po", "pot" }, .line_comment = "# ", .query_last_wins = true },

    // ---- Data and configuration ------------------------------------------------
    .{ .grammar = "json5", .name = "JSON5", .extensions = &.{"json5"}, .line_comment = "// ", .block_comment = c_comments, .query_last_wins = true },
    .{ .grammar = "jsonnet", .name = "Jsonnet", .extensions = &.{ "jsonnet", "libsonnet" }, .line_comment = "// ", .block_comment = c_comments, .query_last_wins = true },
    .{ .grammar = "csv", .name = "CSV", .extensions = &.{"csv"}, .query_inherits = &.{"tsv"}, .query_last_wins = true },
    .{ .grammar = "tsv", .name = "TSV", .extensions = &.{"tsv"}, .query_last_wins = true },
    .{ .grammar = "ini", .name = "INI", .extensions = &.{ "ini", "cfg", "service", "socket", "timer", "mount", "automount", "path", "slice", "target", "network", "netdev", "link" }, .filenames = &.{ ".flake8", ".pylintrc", ".coveragerc", ".npmrc" }, .line_comment = "; ", .query_last_wins = true },
    .{ .grammar = "properties", .name = "Java Properties", .extensions = &.{"properties"}, .line_comment = "# ", .query_last_wins = true },
    .{ .grammar = "editorconfig", .name = "EditorConfig", .filenames = &.{".editorconfig"}, .line_comment = "# ", .query_last_wins = true },
    .{ .grammar = "desktop", .name = "Desktop Entry", .extensions = &.{ "desktop", "directory" }, .line_comment = "# ", .query_last_wins = true },
    .{ .grammar = "kdl", .name = "KDL", .extensions = &.{"kdl"}, .line_comment = "// ", .block_comment = c_comments, .query_last_wins = true },
    .{ .grammar = "ron", .name = "RON", .extensions = &.{"ron"}, .line_comment = "// ", .block_comment = c_comments, .query_last_wins = true },
    .{ .grammar = "dhall", .name = "Dhall", .extensions = &.{"dhall"}, .line_comment = "-- ", .block_comment = haskell_comments, .query_last_wins = true },
    .{ .grammar = "nickel", .name = "Nickel", .extensions = &.{"ncl"}, .line_comment = "# ", .query_last_wins = true },
    .{ .grammar = "cue", .name = "CUE", .extensions = &.{"cue"}, .line_comment = "// ", .query_last_wins = true },
    .{ .grammar = "pkl", .name = "Pkl", .extensions = &.{"pkl"}, .filenames = &.{"PklProject"}, .line_comment = "// ", .block_comment = c_comments, .query_last_wins = true },
    .{ .grammar = "rego", .name = "Rego", .extensions = &.{"rego"}, .line_comment = "# ", .query_last_wins = true },
    .{ .grammar = "hcl", .name = "HCL", .extensions = &.{ "hcl", "nomad", "tfvars" }, .line_comment = "# ", .block_comment = c_comments, .query_last_wins = true },
    .{ .grammar = "terraform", .name = "Terraform", .extensions = &.{ "tf", "tofu" }, .line_comment = "# ", .block_comment = c_comments, .query_inherits = &.{"hcl"}, .query_last_wins = true },
    .{ .grammar = "bicep", .name = "Bicep", .extensions = &.{ "bicep", "bicepparam" }, .line_comment = "// ", .block_comment = c_comments, .query_last_wins = true },
    .{ .grammar = "nginx", .name = "Nginx", .extensions = &.{"nginx"}, .filenames = &.{ "nginx.conf", "nginx/*.conf", "nginx/conf.d/*.conf", "nginx/sites-available/*", "nginx/sites-enabled/*" }, .line_comment = "# ", .query_last_wins = true },
    .{ .grammar = "ssh_config", .name = "SSH Config", .filenames = &.{ ".ssh/config", "ssh_config", "sshd_config", ".ssh/config.d/*", "ssh/ssh_config.d/*.conf", "ssh/sshd_config.d/*.conf" }, .line_comment = "# ", .query_last_wins = true },
    .{ .grammar = "readline", .name = "Readline", .filenames = &.{ ".inputrc", "inputrc" }, .line_comment = "# ", .query_last_wins = true },
    .{ .grammar = "xresources", .name = "Xresources", .filenames = &.{ ".Xresources", ".Xdefaults", "Xresources" }, .line_comment = "! ", .query_last_wins = true },
    .{ .grammar = "hyprlang", .name = "Hyprlang", .extensions = &.{"hl"}, .filenames = &.{ "hyprland.conf", "hyprpaper.conf", "hypridle.conf", "hyprlock.conf", "hypr/*.conf" }, .line_comment = "# ", .query_last_wins = true },
    .{ .grammar = "kitty", .name = "Kitty Config", .filenames = &.{ "kitty.conf", "kitty/*.conf" }, .line_comment = "# ", .query_last_wins = true },
    .{ .grammar = "udev", .name = "udev Rules", .filenames = &.{"udev/rules.d/*.rules"}, .line_comment = "# ", .query_last_wins = true },
    .{ .grammar = "beancount", .name = "Beancount", .extensions = &.{ "beancount", "bean" }, .line_comment = "; ", .query_last_wins = true },
    .{ .grammar = "requirements", .name = "Pip Requirements", .filenames = &.{ "requirements*.txt", "constraints*.txt" }, .line_comment = "# ", .query_last_wins = true },

    // ---- Schemas and interface definitions ---------------------------------
    .{ .grammar = "proto", .name = "Protocol Buffers", .extensions = &.{"proto"}, .line_comment = "// ", .block_comment = c_comments, .query_last_wins = true },
    .{ .grammar = "textproto", .name = "Protocol Buffers Text", .extensions = &.{ "textproto", "txtpb", "pbtxt", "textpb", "prototxt" }, .line_comment = "# ", .query_last_wins = true },
    .{ .grammar = "thrift", .name = "Thrift", .extensions = &.{"thrift"}, .line_comment = "// ", .block_comment = c_comments, .query_last_wins = true },
    .{ .grammar = "capnp", .name = "Cap'n Proto", .extensions = &.{"capnp"}, .line_comment = "# ", .query_last_wins = true },

    // ---- Build systems and tooling ---------------------------------------------
    .{ .grammar = "dockerfile", .name = "Dockerfile", .extensions = &.{ "dockerfile", "containerfile" }, .filenames = &.{ "Dockerfile", "Containerfile", "Dockerfile.*", "Containerfile.*" }, .line_comment = "# ", .query_last_wins = true },
    .{ .grammar = "cmake", .name = "CMake", .extensions = &.{"cmake"}, .filenames = &.{"CMakeLists.txt"}, .line_comment = "# ", .block_comment = .{ "#[[ ", " ]]" }, .query_last_wins = true },
    .{ .grammar = "meson", .name = "Meson", .filenames = &.{ "meson.build", "meson.options", "meson_options.txt" }, .line_comment = "# ", .query_last_wins = true },
    .{ .grammar = "ninja", .name = "Ninja", .extensions = &.{"ninja"}, .line_comment = "# ", .query_last_wins = true },
    .{ .grammar = "just", .name = "Just", .extensions = &.{"just"}, .filenames = &.{ "justfile", "Justfile", ".justfile" }, .line_comment = "# ", .query_last_wins = true },
    .{ .grammar = "earthfile", .name = "Earthfile", .filenames = &.{"Earthfile"}, .line_comment = "# ", .query_last_wins = true },
    .{ .grammar = "kconfig", .name = "Kconfig", .filenames = &.{ "Kconfig", "Kconfig.*", "Config.in" }, .line_comment = "# ", .query_last_wins = true },
    .{ .grammar = "gn", .name = "GN", .extensions = &.{ "gn", "gni" }, .line_comment = "# ", .query_last_wins = true },
    .{ .grammar = "gomod", .name = "Go Module", .filenames = &.{"go.mod"}, .line_comment = "// ", .query_last_wins = true },
    .{ .grammar = "gowork", .name = "Go Workspace", .filenames = &.{"go.work"}, .line_comment = "// ", .query_last_wins = true },
    .{ .grammar = "gosum", .name = "Go Checksums", .filenames = &.{ "go.sum", "go.work.sum" }, .query_last_wins = true },
    .{ .grammar = "query", .name = "Tree-sitter Query", .filenames = &.{ "highlights.scm", "injections.scm", "locals.scm", "folds.scm", "indents.scm", "tags.scm", "textobjects.scm", "context.scm" }, .line_comment = "; ", .query_last_wins = true },

    // ---- Version control ---------------------------------------------------------
    .{ .grammar = "gitcommit", .name = "Git Commit", .filenames = &.{ "COMMIT_EDITMSG", "MERGE_MSG", "TAG_EDITMSG", "NOTES_EDITMSG", "SQUASH_MSG", "EDIT_DESCRIPTION" }, .line_comment = "# ", .query_last_wins = true },
    .{ .grammar = "git_rebase", .name = "Git Rebase", .filenames = &.{"git-rebase-todo"}, .line_comment = "# ", .query_last_wins = true },
    .{ .grammar = "git_config", .name = "Git Config", .extensions = &.{"gitconfig"}, .filenames = &.{ ".gitconfig", ".gitmodules", ".git/config", "git/config" }, .line_comment = "# ", .query_last_wins = true },
    .{ .grammar = "gitignore", .name = "Git Ignore", .extensions = &.{"gitignore"}, .filenames = &.{ ".gitignore", ".dockerignore", ".npmignore", ".prettierignore", ".eslintignore", ".stylelintignore", ".vscodeignore", ".ignore", ".rgignore", ".fdignore", ".git/info/exclude", "git/ignore" }, .line_comment = "# ", .query_last_wins = true },
    .{ .grammar = "gitattributes", .name = "Git Attributes", .extensions = &.{"gitattributes"}, .filenames = &.{ ".gitattributes", ".git/info/attributes", "git/attributes" }, .line_comment = "# ", .query_last_wins = true },
    .{ .grammar = "diff", .name = "Diff", .extensions = &.{ "diff", "patch", "rej" }, .query_last_wins = true },

    // ---- Hardware, shaders and low level ---------------------------------------
    .{ .grammar = "cuda", .name = "CUDA", .extensions = &.{ "cu", "cuh" }, .line_comment = "// ", .block_comment = c_comments, .query_inherits = &.{ "nvim/c", "nvim/cpp" }, .query_last_wins = true },
    .{ .grammar = "arduino", .name = "Arduino", .extensions = &.{"ino"}, .line_comment = "// ", .block_comment = c_comments, .query_inherits = &.{ "nvim/c", "nvim/cpp" }, .query_last_wins = true },
    .{ .grammar = "glsl", .name = "GLSL", .extensions = &.{ "glsl", "vert", "frag", "geom", "comp", "tesc", "tese", "mesh", "task", "rgen", "rchit", "rahit", "rmiss", "rint", "rcall", "vsh", "fsh", "gsh" }, .line_comment = "// ", .block_comment = c_comments, .query_inherits = &.{"nvim/c"}, .query_last_wins = true },
    .{ .grammar = "hlsl", .name = "HLSL", .extensions = &.{ "hlsl", "hlsli", "fx", "fxh" }, .line_comment = "// ", .block_comment = c_comments, .query_inherits = &.{ "nvim/c", "nvim/cpp" }, .query_last_wins = true },
    .{ .grammar = "wgsl", .name = "WGSL", .extensions = &.{"wgsl"}, .line_comment = "// ", .block_comment = c_comments, .query_last_wins = true },
    .{ .grammar = "systemverilog", .name = "SystemVerilog", .extensions = &.{ "sv", "svh", "v", "vh" }, .line_comment = "// ", .block_comment = c_comments, .query_last_wins = true },
    .{ .grammar = "vhdl", .name = "VHDL", .extensions = &.{ "vhd", "vhdl" }, .line_comment = "-- ", .query_last_wins = true },
    .{ .grammar = "devicetree", .name = "Device Tree", .extensions = &.{ "dts", "dtsi", "dtso", "overlay" }, .line_comment = "// ", .block_comment = c_comments, .query_last_wins = true },
    .{ .grammar = "linkerscript", .name = "Linker Script", .extensions = &.{ "ld", "lds" }, .block_comment = c_comments, .query_last_wins = true },
    .{ .grammar = "asm", .name = "Assembly", .extensions = &.{ "s", "S" }, .line_comment = "# ", .block_comment = c_comments, .query_last_wins = true },
    .{ .grammar = "nasm", .name = "NASM", .extensions = &.{ "asm", "nasm" }, .line_comment = "; ", .query_last_wins = true },
    .{ .grammar = "llvm", .name = "LLVM IR", .extensions = &.{"ll"}, .line_comment = "; ", .query_last_wins = true },
    .{ .grammar = "tablegen", .name = "TableGen", .extensions = &.{"td"}, .line_comment = "// ", .block_comment = c_comments, .query_last_wins = true },
};

/// The generated agenda buffer (not offered in "Set Syntax").
pub const org_agenda: LanguageConfig = .{ .grammar = null, .name = "Org Agenda", .custom_highlight = agenda.highlight };

/// The Find in Files results buffer.
pub const find_results: LanguageConfig = .{ .grammar = null, .name = "Find Results", .custom_highlight = search.highlight };

/// The Source Control view (Git: Show Status).
pub const git_status_list: LanguageConfig = .{ .grammar = null, .name = "Git Status", .custom_highlight = git_status.highlight };
/// The Log tab (Source Control: l).
pub const git_log: LanguageConfig = .{ .grammar = null, .name = "Git Log", .custom_highlight = git_status.logHighlight };

/// Languages that only occur injected into others (see injection.zig).
pub const injected_only = [_]LanguageConfig{
    .{ .grammar = "markdown_inline", .name = "Markdown Inline" },
    .{ .grammar = "jinja_inline", .name = "Jinja Inline", .query_last_wins = true },
    .{ .grammar = "php_only", .name = "PHP Code", .query_last_wins = true },
};

/// Names code fences use besides grammar names, language names and file
/// extensions.
const injection_aliases = [_][2][]const u8{
    .{ "shell", "bash" },
    .{ "shellscript", "bash" },
    .{ "golang", "go" },
    .{ "python3", "python" },
    .{ "csharp", "c_sharp" },
    .{ "objective-c", "objc" },
    .{ "objectivec", "objc" },
    .{ "docker", "dockerfile" },
    .{ "emacs-lisp", "elisp" },
    .{ "verilog", "systemverilog" },
    .{ "viml", "vim" },
    .{ "vimscript", "vim" },
    .{ "protobuf", "proto" },
    .{ "pwsh", "powershell" },
    .{ "postcss", "css" },
    .{ "javascriptreact", "javascript" },
};

/// The language an injection names: an injections query's ("javascript",
/// "markdown_inline") or a Markdown code fence's info string ("rust,ignore",
/// "Python", "ts"). Tries grammar names, aliases, language names and file
/// extensions, ignoring case.
pub fn forInjection(raw: []const u8) ?*const LanguageConfig {
    // Nix's injections name the language in a comment (`/* bash */`),
    // Pandoc's fences in braces (`{.python}`).
    var name = std.mem.trimEnd(u8, std.mem.trimStart(u8, raw, " \t\r\n/*#{."), " \t\r\n/*}");
    if (std.mem.indexOfAny(u8, name, " \t,{")) |i| name = name[0..i];
    if (name.len == 0) return null;
    for (&all) |*lang| if (lang.grammar) |g| if (std.ascii.eqlIgnoreCase(g, name)) return lang;
    for (&injected_only) |*lang| if (std.ascii.eqlIgnoreCase(lang.grammar.?, name)) return lang;
    for (injection_aliases) |alias| if (std.ascii.eqlIgnoreCase(alias[0], name)) {
        for (&all) |*lang| if (lang.grammar) |g| if (std.mem.eql(u8, g, alias[1])) return lang;
    };
    for (&all) |*lang| if (std.ascii.eqlIgnoreCase(lang.name, name)) return lang;
    for (&all) |*lang| for (lang.extensions) |e| if (std.ascii.eqlIgnoreCase(e, name)) return lang;
    return null;
}

pub fn forPath(path: []const u8) *const LanguageConfig {
    const base = std.fs.path.basename(path);
    for (&all) |*lang| {
        for (lang.filenames) |name| if (filenameMatches(path, name)) return lang;
    }
    const ext = std.fs.path.extension(base);
    if (ext.len > 1) {
        for (&all) |*lang| {
            for (lang.extensions) |e| if (std.ascii.eqlIgnoreCase(ext[1..], e)) return lang;
        }
    }
    return &plain_text;
}

/// Matches `pattern`'s components against the last components of `path`.
fn filenameMatches(path: []const u8, pattern: []const u8) bool {
    var pat_it = std.mem.splitBackwardsScalar(u8, pattern, '/');
    var path_it = std.mem.splitBackwardsScalar(u8, path, '/');
    while (pat_it.next()) |pat| {
        const comp = path_it.next() orelse return false;
        if (!globMatches(comp, pat)) return false;
    }
    return true;
}

/// `*` matches any run of characters; there is at most one in our patterns.
fn globMatches(name: []const u8, pat: []const u8) bool {
    const star = std.mem.indexOfScalar(u8, pat, '*') orelse return std.mem.eql(u8, name, pat);
    const prefix = pat[0..star];
    const suffix = pat[star + 1 ..];
    return name.len >= prefix.len + suffix.len and
        std.mem.startsWith(u8, name, prefix) and std.mem.endsWith(u8, name, suffix);
}

pub fn byName(name: []const u8) ?*const LanguageConfig {
    if (std.mem.eql(u8, name, plain_text.name)) return &plain_text;
    for (&all) |*lang| if (std.mem.eql(u8, lang.name, name)) return lang;
    return null;
}

test "forPath picks languages by extension, file name and path" {
    const expectName = struct {
        fn f(path: []const u8, name: []const u8) !void {
            try std.testing.expectEqualStrings(name, forPath(path).name);
        }
    }.f;
    try expectName("src/main.zig", "Zig");
    try expectName("Foo.JAVA", "Java");
    try expectName("lib/thing.rb", "Ruby");
    try expectName("/home/me/app/Gemfile", "Ruby");
    try expectName("Dockerfile", "Dockerfile");
    try expectName("docker/Dockerfile.prod", "Dockerfile");
    try expectName("build.Dockerfile", "Dockerfile");
    try expectName("CMakeLists.txt", "CMake");
    try expectName("notes.txt", "Plain Text");
    try expectName("requirements-dev.txt", "Pip Requirements");
    try expectName("Makefile.am", "Makefile");
    try expectName("flake.lock", "JSON");
    try expectName("Cargo.lock", "TOML");
    try expectName("main.tf", "Terraform");
    try expectName("prod.tfvars", "HCL");
    try expectName("repo/.git/config", "Git Config");
    try expectName("/home/me/.config/git/config", "Git Config");
    try expectName("/home/me/.config/cs-zig/config", "Plain Text");
    try expectName("/home/me/.ssh/config", "SSH Config");
    try expectName(".git/COMMIT_EDITMSG", "Git Commit");
    try expectName("/etc/udev/rules.d/99-usb.rules", "udev Rules");
    try expectName("/etc/polkit-1/rules.d/50-default.rules", "Plain Text");
    try expectName("/etc/nginx/sites-available/default", "Nginx");
    try expectName("queries/highlights.scm", "Tree-sitter Query");
    try expectName("lib/srfi.scm", "Scheme");
    try expectName("BUILD", "Starlark");
    try expectName("x.m", "Objective-C");
    try expectName("top.v", "SystemVerilog");
    try expectName("app/views/home/index.html.erb", "ERB");
    try expectName("resources/views/welcome.blade.php", "Blade");
    try expectName("src/app/app.component.html", "Angular Template");
    try expectName("index.html", "HTML");
    try expectName("README", "Plain Text");
}

test "globMatches" {
    try std.testing.expect(globMatches("Dockerfile.dev", "Dockerfile.*"));
    try std.testing.expect(!globMatches("Dockerfile", "Dockerfile.*"));
    try std.testing.expect(globMatches("requirements.txt", "requirements*.txt"));
    try std.testing.expect(!globMatches("requirements.tx", "requirements*.txt"));
    try std.testing.expect(globMatches("a", "*"));
    try std.testing.expect(globMatches("abc", "abc"));
}

test "every language name is unique" {
    for (all, 0..) |a, i| for (all[i + 1 ..]) |b| {
        if (std.mem.eql(u8, a.name, b.name)) {
            std.debug.print("duplicate language name: {s}\n", .{a.name});
            return error.DuplicateLanguage;
        }
    };
}

test "language servers name languages from the table" {
    const lsp = @import("../lsp/lsp.zig");
    for (lsp.configs) |cfg| for (cfg.languages) |name| {
        if (byName(name) == null) {
            std.debug.print("{s}: unknown language \"{s}\"\n", .{ cfg.name, name });
            return error.UnknownLanguage;
        }
    };
}

/// Captures that are deliberately left uncolored: nvim's spell-check markers
/// and "reset" captures, and the ones only used by predicates (Standard ML's
/// `@id` and `@vid` among them).
fn uncoloredCapture(name: []const u8) bool {
    const names = [_][]const u8{ "spell", "nospell", "none", "conceal", "error", "id", "vid" };
    for (names) |n| if (std.mem.eql(u8, name, n)) return true;
    return name.len > 0 and name[0] == '_';
}

/// Filter predicates syntax.zig evaluates; directives (`#set!` ...) don't
/// affect which nodes match.
fn supportedPredicate(name: []const u8) bool {
    const names = [_][]const u8{ "eq?", "not-eq?", "match?", "not-match?", "lua-match?", "not-lua-match?", "any-of?", "not-any-of?", "has-ancestor?", "not-has-ancestor?", "has-parent?", "not-has-parent?", "is?", "is-not?" };
    for (names) |n| if (std.mem.eql(u8, name, n)) return true;
    return name.len > 0 and name[name.len - 1] == '!';
}

fn isRegexPredicate(name: []const u8) bool {
    const names = [_][]const u8{ "match?", "not-match?", "lua-match?", "not-lua-match?" };
    for (names) |n| if (std.mem.eql(u8, name, n)) return true;
    return false;
}

fn appendQueryFile(gpa: std.mem.Allocator, out: *std.ArrayList(u8), dir: []const u8, grammar: []const u8, file: []const u8) !void {
    var path_buf: [std.fs.max_path_bytes]u8 = undefined;
    const path = try std.fmt.bufPrintSentinel(&path_buf, "{s}/{s}/queries/{s}", .{ dir, grammar, file }, 0);
    const f = std.c.fopen(path, "rb") orelse return;
    defer _ = std.c.fclose(f);
    var buf: [16384]u8 = undefined;
    while (true) {
        const n = std.c.fread(&buf, 1, buf.len, f);
        if (n == 0) break;
        try out.appendSlice(gpa, buf[0..n]);
    }
    try out.append(gpa, '\n');
}

/// Compiles a whole query (`file` of the grammar and those it inherits) again
/// to say where its first bad pattern is.
fn reportBadPattern(gpa: std.mem.Allocator, dir: []const u8, lang: *const LanguageConfig, ts: *const @import("c").TSLanguage, file: []const u8) !void {
    const c = @import("c");
    var source: std.ArrayList(u8) = .empty;
    defer source.deinit(gpa);
    for (lang.query_inherits) |parent| try appendQueryFile(gpa, &source, dir, parent, file);
    try appendQueryFile(gpa, &source, dir, lang.grammar.?, file);
    if (std.mem.eql(u8, file, "highlights.scm")) for (lang.query_extends) |extra| try appendQueryFile(gpa, &source, dir, extra, file);
    var err_offset: u32 = 0;
    var err_type: c.TSQueryError = c.TSQueryErrorNone;
    if (c.ts_query_new(ts, source.items.ptr, @intCast(source.items.len), &err_offset, &err_type)) |whole| {
        c.ts_query_delete(whole);
    } else {
        const line_start = if (std.mem.lastIndexOfScalar(u8, source.items[0..err_offset], '\n')) |i| i + 1 else 0;
        const line_end = std.mem.indexOfScalarPos(u8, source.items, err_offset, '\n') orelse source.items.len;
        const line_no = std.mem.count(u8, source.items[0..err_offset], "\n") + 1;
        std.debug.print("{s}: {s} error {d} on line {d}: {s}\n", .{ lang.name, file, err_type, line_no, source.items[line_start..line_end] });
    }
}

/// Reports predicates the registry ignores and regexes it can't compile.
fn checkPredicates(name: []const u8, q: *@import("c").TSQuery, predicates: []const []const syntax_mod.Predicate) usize {
    const c = @import("c");
    var problems: usize = 0;
    for (0..c.ts_query_pattern_count(q)) |pattern| {
        var step_count: u32 = 0;
        const steps = c.ts_query_predicates_for_pattern(q, @intCast(pattern), &step_count);
        var regexes: usize = 0;
        var i: usize = 0;
        while (i < step_count) {
            var end = i;
            while (end < step_count and steps[end].type != c.TSQueryPredicateStepTypeDone) end += 1;
            defer i = end + 1;
            if (end == i or steps[i].type != c.TSQueryPredicateStepTypeString) continue;
            var len: u32 = 0;
            const ptr = c.ts_query_string_value_for_id(q, steps[i].value_id, &len);
            const predicate = ptr[0..len];
            if (isRegexPredicate(predicate)) {
                regexes += 1;
            } else if (!supportedPredicate(predicate)) {
                std.debug.print("{s}: unsupported predicate #{s}\n", .{ name, predicate });
                problems += 1;
            }
        }
        var compiled: usize = 0;
        for (predicates[pattern]) |p| {
            if (p.regex != null) compiled += 1;
        }
        if (compiled != regexes) {
            std.debug.print("{s}: {d} regex(es) in pattern {d} don't compile\n", .{ name, regexes - compiled, pattern });
            problems += 1;
        }
    }
    return problems;
}

/// Languages injections name that no grammar here provides: doc comments,
/// format strings and other mini-languages, mostly.
const not_shipped = [_][]const u8{ "comment", "regex", "jsdoc", "luadoc", "luap", "printf", "doxygen", "javadoc", "phpdoc", "rbs", "re2c", "re", "pod", "nim_format_string", "haskell_persistent", "surface", "eex", "coffeescript" };

fn checkInjections(registry: *syntax_mod.Registry, lang: *const LanguageConfig, loaded: *syntax_mod.Language, iq: *const injection.Query) !usize {
    var problems: usize = 0;
    if (iq.dropped_patterns > 0) {
        try reportBadPattern(std.testing.allocator, registry.grammar_dir, lang, loaded.ts, "injections.scm");
        std.debug.print("{s}: {d} injection pattern(s) dropped\n", .{ lang.name, iq.dropped_patterns });
        problems += 1;
    }
    if (iq.content_capture == null) {
        std.debug.print("{s}: injections query has no @injection.content\n", .{lang.name});
        problems += 1;
    }
    for (iq.patterns) |p| {
        const name = p.language orelse continue;
        if (p.config) |config| {
            if (registry.get(config) == null) {
                std.debug.print("{s}: injects {s}, whose grammar doesn't load\n", .{ lang.name, name });
                problems += 1;
            }
        } else for (not_shipped) |n| {
            if (std.mem.eql(u8, n, name)) break;
        } else {
            std.debug.print("{s}: injects unknown language \"{s}\"\n", .{ lang.name, name });
            problems += 1;
        }
    }
    return problems + checkPredicates(lang.name, iq.query, iq.predicates);
}

// Needs the grammar farm ($CS_ZIG_GRAMMARS, set by `nix develop`); skipped
// without it. Checks what the registry would otherwise silently work around:
// query patterns it drops, regexes it can't compile, predicates it ignores,
// captures the theme has no color for and languages injected that don't load.
test "every grammar loads and its highlight and injection queries compile cleanly" {
    const c = @import("c");
    const gpa = std.testing.allocator;
    const theme = theme_mod.build(theme_mod.schemeByName("Mariana").?);
    var registry = syntax_mod.Registry.init(gpa, &theme);
    defer registry.deinit();
    var dir_buf: [std.fs.max_path_bytes]u8 = undefined;
    const dir_z = std.fmt.bufPrintSentinel(&dir_buf, "{s}", .{registry.grammar_dir}, 0) catch return error.SkipZigTest;
    if (registry.grammar_dir.len == 0 or std.c.access(dir_z, std.c.F_OK) != 0) return error.SkipZigTest;

    var problems: usize = 0;
    for ([_][]const LanguageConfig{ &all, &injected_only }) |table| for (table) |*lang| {
        const grammar = lang.grammar orelse continue;
        const loaded = registry.get(lang) orelse {
            std.debug.print("{s}: grammar '{s}' failed to load\n", .{ lang.name, grammar });
            problems += 1;
            continue;
        };
        const parser = c.ts_parser_new().?;
        defer c.ts_parser_delete(parser);
        if (!c.ts_parser_set_language(parser, loaded.ts)) {
            std.debug.print("{s}: grammar ABI {d} is incompatible\n", .{ lang.name, c.ts_language_abi_version(loaded.ts) });
            problems += 1;
        }
        if (loaded.injections) |iq| problems += try checkInjections(&registry, lang, loaded, iq);

        const q = loaded.query orelse {
            std.debug.print("{s}: no highlights query, or it doesn't compile\n", .{lang.name});
            problems += 1;
            continue;
        };
        if (loaded.dropped_patterns > 0) {
            try reportBadPattern(gpa, registry.grammar_dir, lang, loaded.ts, "highlights.scm");
            std.debug.print("{s}: {d} highlight pattern(s) dropped\n", .{ lang.name, loaded.dropped_patterns });
            problems += 1;
        }

        for (0..c.ts_query_capture_count(q)) |i| {
            var len: u32 = 0;
            const ptr = c.ts_query_capture_name_for_id(q, @intCast(i), &len);
            const name = ptr[0..len];
            if (theme_mod.colorForCapture(&theme.syntax, name) == null and !uncoloredCapture(name)) {
                std.debug.print("{s}: capture @{s} has no color\n", .{ lang.name, name });
                problems += 1;
            }
        }
        problems += checkPredicates(lang.name, q, loaded.predicates);
    };
    try std.testing.expectEqual(@as(usize, 0), problems);
}

test "forInjection resolves query languages and code fence info strings" {
    const expectGrammar = struct {
        fn f(name: []const u8, grammar: ?[]const u8) !void {
            const got = if (forInjection(name)) |l| l.grammar else null;
            if (grammar) |g| {
                try std.testing.expectEqualStrings(g, got orelse "(none)");
            } else try std.testing.expectEqual(null, got);
        }
    }.f;
    try expectGrammar("javascript", "javascript");
    try expectGrammar("markdown_inline", "markdown_inline");
    try expectGrammar("ts", "typescript");
    try expectGrammar("jsx", "javascript");
    try expectGrammar("Python", "python");
    try expectGrammar("rust,ignore", "rust");
    try expectGrammar("{.python}", "python");
    try expectGrammar("sh", "bash");
    try expectGrammar("shell", "bash");
    try expectGrammar("c++", "cpp");
    try expectGrammar("C#", "c_sharp");
    try expectGrammar("/* bash */", "bash");
    try expectGrammar("# bash", "bash");
    try expectGrammar("yml", "yaml");
    try expectGrammar("comment", null);
    try expectGrammar("text", null);
    try expectGrammar("", null);
}
