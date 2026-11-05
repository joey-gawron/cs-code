//! Language Server Protocol client (a small version of Zed's LspStore).
//!
//! One server process per configured language, started on first use. A
//! reader thread per server frames JSON-RPC messages and posts them to the
//! main thread through the executor. Documents sync incrementally when the
//! server negotiates UTF-8 positions, otherwise with debounced full text.
//!
//! What the servers are doing is kept for the Language Servers panel: each
//! server's state and work-done progress (`statusList`, `statusFor`), and a
//! log of starts, exits, failed requests, `window/logMessage` and the
//! servers' stderr (`log`).

const std = @import("std");
const protocol = @import("protocol.zig");
const signature = @import("signature.zig");
const buffer_mod = @import("../text/buffer.zig");
const Executor = @import("../../framework/util/executor.zig").Executor;
const platform = @import("../../framework/platform/platform.zig");
const vfs = @import("../remote/vfs.zig");
const rpath = @import("../remote/path.zig");

const Allocator = std.mem.Allocator;
const Io = std.Io;
const Buffer = buffer_mod.Buffer;
const Value = std.json.Value;
const Encoding = protocol.Encoding;
const Position = protocol.Position;
const log = std.log.scoped(.lsp);

pub const Config = struct {
    name: []const u8,
    /// Language names (as in languages.zig) this server handles.
    languages: []const []const u8,
    argv: []const []const u8,
};

pub const configs = [_]Config{
    .{ .name = "zls", .languages = &.{"Zig"}, .argv = &.{"zls"} },
    .{ .name = "clangd", .languages = &.{ "C", "C++", "Objective-C", "CUDA" }, .argv = &.{ "clangd", "--background-index" } },
    .{ .name = "rust-analyzer", .languages = &.{"Rust"}, .argv = &.{"rust-analyzer"} },
    .{ .name = "pyright", .languages = &.{"Python"}, .argv = &.{ "pyright-langserver", "--stdio" } },
    .{ .name = "gopls", .languages = &.{"Go"}, .argv = &.{"gopls"} },
    .{ .name = "nil", .languages = &.{"Nix"}, .argv = &.{"nil"} },
    .{ .name = "bash-language-server", .languages = &.{"Shell Script (Bash)"}, .argv = &.{ "bash-language-server", "start" } },
    .{ .name = "typescript-language-server", .languages = &.{ "JavaScript", "TypeScript", "TypeScriptReact" }, .argv = &.{ "typescript-language-server", "--stdio" } },
    .{ .name = "lua-language-server", .languages = &.{"Lua"}, .argv = &.{"lua-language-server"} },
    .{ .name = "taplo", .languages = &.{"TOML"}, .argv = &.{ "taplo", "lsp", "stdio" } },
    .{ .name = "yaml-language-server", .languages = &.{"YAML"}, .argv = &.{ "yaml-language-server", "--stdio" } },
    .{ .name = "vscode-json-language-server", .languages = &.{"JSON"}, .argv = &.{ "vscode-json-language-server", "--stdio" } },
    .{ .name = "vscode-css-language-server", .languages = &.{ "CSS", "SCSS", "Less" }, .argv = &.{ "vscode-css-language-server", "--stdio" } },
    .{ .name = "vscode-html-language-server", .languages = &.{"HTML"}, .argv = &.{ "vscode-html-language-server", "--stdio" } },
    .{ .name = "solargraph", .languages = &.{"Ruby"}, .argv = &.{ "solargraph", "stdio" } },
    .{ .name = "perlnavigator", .languages = &.{"Perl"}, .argv = &.{ "perlnavigator", "--stdio" } },
    .{ .name = "elp", .languages = &.{"Erlang"}, .argv = &.{ "elp", "server" } },
    .{ .name = "gleam", .languages = &.{"Gleam"}, .argv = &.{ "gleam", "lsp" } },
    .{ .name = "elm-language-server", .languages = &.{"Elm"}, .argv = &.{"elm-language-server"} },
    .{ .name = "rescript-language-server", .languages = &.{"ReScript"}, .argv = &.{ "rescript-language-server", "--stdio" } },
    .{ .name = "clojure-lsp", .languages = &.{"Clojure"}, .argv = &.{"clojure-lsp"} },
    .{ .name = "fennel-ls", .languages = &.{"Fennel"}, .argv = &.{"fennel-ls"} },
    .{ .name = "luau-lsp", .languages = &.{"Luau"}, .argv = &.{ "luau-lsp", "lsp" } },
    .{ .name = "nimlangserver", .languages = &.{"Nim"}, .argv = &.{"nimlangserver"} },
    .{ .name = "serve-d", .languages = &.{"D"}, .argv = &.{"serve-d"} },
    .{ .name = "fortls", .languages = &.{"Fortran"}, .argv = &.{"fortls"} },
    .{ .name = "solc", .languages = &.{"Solidity"}, .argv = &.{ "solc", "--lsp" } },
    .{ .name = "starpls", .languages = &.{"Starlark"}, .argv = &.{"starpls"} },
    .{ .name = "vim-language-server", .languages = &.{"Vim Script"}, .argv = &.{ "vim-language-server", "--stdio" } },
    .{ .name = "svelteserver", .languages = &.{"Svelte"}, .argv = &.{ "svelteserver", "--stdio" } },
    .{ .name = "graphql-lsp", .languages = &.{"GraphQL"}, .argv = &.{ "graphql-lsp", "server", "-m", "stream" } },
    .{ .name = "prisma-language-server", .languages = &.{"Prisma"}, .argv = &.{ "prisma-language-server", "--stdio" } },
    .{ .name = "templ", .languages = &.{"Templ"}, .argv = &.{ "templ", "lsp" } },
    .{ .name = "sqls", .languages = &.{"SQL"}, .argv = &.{"sqls"} },
    .{ .name = "texlab", .languages = &.{ "LaTeX", "BibTeX" }, .argv = &.{"texlab"} },
    .{ .name = "marksman", .languages = &.{"Markdown"}, .argv = &.{ "marksman", "server" } },
    .{ .name = "lemminx", .languages = &.{ "XML", "DTD" }, .argv = &.{"lemminx"} },
    .{ .name = "jsonnet-language-server", .languages = &.{"Jsonnet"}, .argv = &.{"jsonnet-language-server"} },
    .{ .name = "dhall-lsp-server", .languages = &.{"Dhall"}, .argv = &.{"dhall-lsp-server"} },
    .{ .name = "nls", .languages = &.{"Nickel"}, .argv = &.{"nls"} },
    .{ .name = "cuelsp", .languages = &.{"CUE"}, .argv = &.{"cuelsp"} },
    .{ .name = "regols", .languages = &.{"Rego"}, .argv = &.{"regols"} },
    .{ .name = "terraform-ls", .languages = &.{"Terraform"}, .argv = &.{ "terraform-ls", "serve" } },
    .{ .name = "nginx-language-server", .languages = &.{"Nginx"}, .argv = &.{"nginx-language-server"} },
    .{ .name = "hyprls", .languages = &.{"Hyprlang"}, .argv = &.{"hyprls"} },
    .{ .name = "beancount-language-server", .languages = &.{"Beancount"}, .argv = &.{"beancount-language-server"} },
    .{ .name = "protols", .languages = &.{"Protocol Buffers"}, .argv = &.{"protols"} },
    .{ .name = "docker-langserver", .languages = &.{"Dockerfile"}, .argv = &.{ "docker-langserver", "--stdio" } },
    .{ .name = "neocmakelsp", .languages = &.{"CMake"}, .argv = &.{ "neocmakelsp", "stdio" } },
    .{ .name = "mesonlsp", .languages = &.{"Meson"}, .argv = &.{ "mesonlsp", "--lsp" } },
    .{ .name = "just-lsp", .languages = &.{"Just"}, .argv = &.{"just-lsp"} },
    .{ .name = "glsl_analyzer", .languages = &.{"GLSL"}, .argv = &.{"glsl_analyzer"} },
    .{ .name = "wgsl-analyzer", .languages = &.{"WGSL"}, .argv = &.{"wgsl-analyzer"} },
    .{ .name = "verible-verilog-ls", .languages = &.{"SystemVerilog"}, .argv = &.{"verible-verilog-ls"} },
    .{ .name = "vhdl_ls", .languages = &.{"VHDL"}, .argv = &.{"vhdl_ls"} },
    // Not in the dev shell: these need their language's toolchain (a JDK,
    // .NET, GHC, Swift, ...) or are too big for it. Used when installed.
    .{ .name = "jdtls", .languages = &.{"Java"}, .argv = &.{"jdtls"} },
    .{ .name = "kotlin-language-server", .languages = &.{"Kotlin"}, .argv = &.{"kotlin-language-server"} },
    .{ .name = "metals", .languages = &.{"Scala"}, .argv = &.{"metals"} },
    .{ .name = "groovy-language-server", .languages = &.{"Groovy"}, .argv = &.{"groovy-language-server"} },
    .{ .name = "csharp-ls", .languages = &.{"C#"}, .argv = &.{"csharp-ls"} },
    .{ .name = "fsautocomplete", .languages = &.{"F#"}, .argv = &.{"fsautocomplete"} },
    .{ .name = "sourcekit-lsp", .languages = &.{"Swift"}, .argv = &.{"sourcekit-lsp"} },
    .{ .name = "dart", .languages = &.{"Dart"}, .argv = &.{ "dart", "language-server", "--protocol=lsp" } },
    .{ .name = "haskell-language-server", .languages = &.{"Haskell"}, .argv = &.{ "haskell-language-server-wrapper", "--lsp" } },
    .{ .name = "purescript-language-server", .languages = &.{"PureScript"}, .argv = &.{ "purescript-language-server", "--stdio" } },
    .{ .name = "ocamllsp", .languages = &.{ "OCaml", "OCaml Interface" }, .argv = &.{"ocamllsp"} },
    .{ .name = "elixir-ls", .languages = &.{"Elixir"}, .argv = &.{"elixir-ls"} },
    .{ .name = "phpactor", .languages = &.{"PHP"}, .argv = &.{ "phpactor", "language-server" } },
    .{ .name = "languageserver (R)", .languages = &.{"R"}, .argv = &.{ "R", "--no-echo", "-e", "languageserver::run()" } },
    .{ .name = "LanguageServer.jl", .languages = &.{"Julia"}, .argv = &.{ "julia", "--startup-file=no", "--history-file=no", "-e", "using LanguageServer; runserver()" } },
    .{ .name = "ols", .languages = &.{"Odin"}, .argv = &.{"ols"} },
    .{ .name = "crystalline", .languages = &.{"Crystal"}, .argv = &.{"crystalline"} },
    .{ .name = "vala-language-server", .languages = &.{"Vala"}, .argv = &.{"vala-language-server"} },
    .{ .name = "tinymist", .languages = &.{"Typst"}, .argv = &.{"tinymist"} },
    .{ .name = "fish-lsp", .languages = &.{"Fish"}, .argv = &.{ "fish-lsp", "start" } },
    .{ .name = "nu", .languages = &.{"Nushell"}, .argv = &.{ "nu", "--lsp" } },
    .{ .name = "awk-language-server", .languages = &.{"AWK"}, .argv = &.{"awk-language-server"} },
};

fn languageId(name: []const u8) []const u8 {
    const map = [_][2][]const u8{
        .{ "Zig", "zig" },                         .{ "C", "c" },                             .{ "C++", "cpp" },
        .{ "Rust", "rust" },                       .{ "Python", "python" },                   .{ "Go", "go" },
        .{ "Nix", "nix" },                         .{ "Shell Script (Bash)", "shellscript" }, .{ "JavaScript", "javascript" },
        .{ "TypeScript", "typescript" },           .{ "TypeScriptReact", "typescriptreact" }, .{ "Lua", "lua" },
        .{ "TOML", "toml" },                       .{ "YAML", "yaml" },                       .{ "JSON", "json" },
        .{ "CSS", "css" },                         .{ "HTML", "html" },
        // VS Code's identifiers, which servers expect.
                              .{ "Objective-C", "objective-c" },
        .{ "CUDA", "cuda-cpp" },                   .{ "SCSS", "scss" },                       .{ "Less", "less" },
        .{ "Ruby", "ruby" },                       .{ "Perl", "perl" },                       .{ "Erlang", "erlang" },
        .{ "Gleam", "gleam" },                     .{ "Elm", "elm" },                         .{ "ReScript", "rescript" },
        .{ "Clojure", "clojure" },                 .{ "Fennel", "fennel" },                   .{ "Luau", "luau" },
        .{ "Nim", "nim" },                         .{ "D", "d" },                             .{ "Fortran", "fortran" },
        .{ "Solidity", "solidity" },               .{ "Starlark", "starlark" },               .{ "Vim Script", "vim" },
        .{ "Svelte", "svelte" },                   .{ "GraphQL", "graphql" },                 .{ "Prisma", "prisma" },
        .{ "Templ", "templ" },                     .{ "SQL", "sql" },                         .{ "LaTeX", "latex" },
        .{ "BibTeX", "bibtex" },                   .{ "Markdown", "markdown" },               .{ "XML", "xml" },
        .{ "DTD", "dtd" },                         .{ "Jsonnet", "jsonnet" },                 .{ "Dhall", "dhall" },
        .{ "Nickel", "nickel" },                   .{ "CUE", "cue" },                         .{ "Rego", "rego" },
        .{ "Terraform", "terraform" },             .{ "Nginx", "nginx" },                     .{ "Hyprlang", "hyprlang" },
        .{ "Beancount", "beancount" },             .{ "Protocol Buffers", "proto" },          .{ "Dockerfile", "dockerfile" },
        .{ "CMake", "cmake" },                     .{ "Meson", "meson" },                     .{ "Just", "just" },
        .{ "GLSL", "glsl" },                       .{ "WGSL", "wgsl" },                       .{ "SystemVerilog", "systemverilog" },
        .{ "VHDL", "vhdl" },                       .{ "Java", "java" },                       .{ "Kotlin", "kotlin" },
        .{ "Scala", "scala" },                     .{ "Groovy", "groovy" },                   .{ "C#", "csharp" },
        .{ "F#", "fsharp" },                       .{ "Swift", "swift" },                     .{ "Dart", "dart" },
        .{ "Haskell", "haskell" },                 .{ "PureScript", "purescript" },           .{ "OCaml", "ocaml" },
        .{ "OCaml Interface", "ocaml.interface" }, .{ "Elixir", "elixir" },                   .{ "PHP", "php" },
        .{ "R", "r" },                             .{ "Julia", "julia" },                     .{ "Odin", "odin" },
        .{ "Crystal", "crystal" },                 .{ "Vala", "vala" },                       .{ "Typst", "typst" },
        .{ "Fish", "fish" },                       .{ "Nushell", "nushell" },                 .{ "AWK", "awk" },
    };
    for (map) |m| if (std.mem.eql(u8, m[0], name)) return m[1];
    return "plaintext";
}

pub fn configFor(language: []const u8) ?*const Config {
    for (&configs) |*cfg| {
        for (cfg.languages) |l| if (std.mem.eql(u8, l, language)) return cfg;
    }
    return null;
}

// ---- Results delivered to the UI -------------------------------------------------

pub const Location = struct {
    path: []const u8,
    range: protocol.Range,
    encoding: Encoding,
};

/// Where a goto request looks: textDocument/definition, /implementation
/// or /typeDefinition.
pub const Goto = enum {
    definition,
    implementation,
    type_definition,

    fn method(self: Goto) []const u8 {
        return switch (self) {
            .definition => "textDocument/definition",
            .implementation => "textDocument/implementation",
            .type_definition => "textDocument/typeDefinition",
        };
    }
};

pub const CompletionItem = struct {
    label: []const u8,
    detail: []const u8,
    kind: u8,
    insert_text: []const u8,
    filter_text: []const u8,
    sort_text: []const u8,
    /// Replacement range (offsets), if the server provided a textEdit.
    edit_start: ?usize = null,
    edit_end: ?usize = null,
};

pub const TextEdit = struct {
    range: protocol.Range,
    text: []const u8,
};

pub const FileEdit = struct {
    path: []const u8,
    edits: []TextEdit,
};

/// One entry of a code action list: a CodeAction or a bare Command.
pub const CodeAction = struct {
    title: []const u8,
    /// "quickfix", "refactor.extract", ... ("" when the server gave none).
    kind: []const u8 = "",
    preferred: bool = false,
    /// Why it can't run now (the server's `disabled.reason`), if it can't.
    disabled: ?[]const u8 = null,
    /// The action as the server sent it, JSON, to resolve or run it later.
    json: []const u8,
};

pub const Result = union(enum) {
    hover: struct { buffer: *Buffer, offset: usize, text: []const u8 },
    /// The signature under the caret's call; null closes the popup.
    signature_help: struct { buffer: *Buffer, offset: usize, signature: ?signature.Signature },
    goto: struct { what: Goto, locations: []Location },
    references: struct { locations: []Location },
    completion: struct { buffer: *Buffer, offset: usize, version: u64, items: []CompletionItem },
    workspace_edit: struct { files: []FileEdit, encoding: Encoding },
    formatting: struct { buffer: *Buffer, version: u64, edits: []TextEdit, encoding: Encoding },
    /// The actions for a range, from `codeActions`; strings live until the
    /// handler returns.
    code_actions: struct { buffer: *Buffer, actions: []CodeAction },
    diagnostics_changed,
    /// A server's state or progress changed, or the log grew.
    status_changed,
    message: []const u8,
};

pub const Sink = struct {
    ctx: *anyopaque,
    onResult: *const fn (ctx: *anyopaque, result: Result) void,
};

// ---- Log ------------------------------------------------------------------------

pub const LogLevel = enum { err, warning, info, log, stderr };

pub const LogEntry = struct {
    /// One more than the previous entry's (never reused, even after a clear).
    seq: u64,
    /// Wall-clock time, ms since the epoch.
    time_ms: i64,
    /// The server's config name; null for the client's own notes.
    server: ?[]const u8,
    level: LogLevel,
    /// One line or several; no control characters other than '\n' and '\t'.
    text: []u8,
};

/// Past this the oldest tenth of the log is dropped.
const max_log_entries = 5000;
const max_log_text = 4096;

/// Keeps `text` printable: drops ANSI escape sequences (servers that color
/// their stderr), '\r' and other control characters, and caps its length
/// at a UTF-8 boundary. Returns the shortened slice of `text`.
fn sanitizeLog(text: []u8) []u8 {
    var out: usize = 0;
    var i: usize = 0;
    while (i < text.len) {
        const ch = text[i];
        if (ch == 0x1b) {
            // CSI (ESC [ ... final byte in @..~) or a two-byte escape.
            i += 1;
            if (i < text.len and text[i] == '[') {
                i += 1;
                while (i < text.len and (text[i] < 0x40 or text[i] > 0x7e)) i += 1;
            }
            i += 1;
            continue;
        }
        i += 1;
        if (ch < 0x20 and ch != '\n' and ch != '\t') continue;
        if (ch == 0x7f) continue;
        text[out] = ch;
        out += 1;
    }
    var n = std.mem.trimEnd(u8, text[0..out], " \t\n").len;
    if (n > max_log_text) {
        n = max_log_text;
        while (n > 0 and text[n] & 0xC0 == 0x80) n -= 1;
    }
    return text[0..n];
}

fn wallMs() i64 {
    var ts: std.c.timespec = undefined;
    _ = std.c.clock_gettime(.REALTIME, &ts);
    return @as(i64, ts.sec) * 1000 + @divTrunc(@as(i64, ts.nsec), 1_000_000);
}

// ---- Servers --------------------------------------------------------------------

const SyncKind = enum { none, full, incremental };

pub const ServerState = enum {
    /// Started; waiting for the initialize response.
    starting,
    ready,
    /// The process ended (or was stopped).
    exited,
    /// It couldn't be started, or rejected `initialize`.
    failed,
};

/// `$/progress` work the server reported as begun and not yet ended.
pub const Progress = struct {
    token: []u8,
    title: []u8,
    message: []u8,
    percentage: ?u8 = null,
};

/// Serializes as `{}` (an empty anonymous struct would become `[]`).
const Empty = struct {};

pub const Server = struct {
    lsp: *Lsp,
    config: *const Config,
    child: std.process.Child,
    in_fd: std.c.fd_t,
    out_fd: std.c.fd_t,
    /// A local server's stderr, read by the reader thread into the log.
    err_fd: ?std.c.fd_t = null,
    reader: ?std.Thread = null,
    /// A server on a remote host (the root is ssh://...): `in_fd`/`out_fd`
    /// are local pipes to it and `child` is an empty placeholder.
    remote: ?*vfs.Process = null,
    initialized: bool = false,
    dead: bool = false,
    /// `stopServer` ended it (a restart or shutdown), so its exit is expected.
    stopping: bool = false,
    /// The initialize request failed.
    init_failed: bool = false,
    /// When it was started (platform.nowMs), to time the initialize.
    started_ms: i64 = 0,
    /// The version in the initialize response's serverInfo ("0.14.0").
    version_buf: [48]u8 = undefined,
    version_len: usize = 0,
    progress: std.ArrayList(Progress) = .empty,
    encoding: Encoding = .utf16,
    sync: SyncKind = .full,
    trigger_chars: [32]u8 = undefined,
    n_trigger: usize = 0,
    hover: bool = false,
    signature_help: bool = false,
    /// Characters that open or move the signature popup ("(" and ",").
    sig_trigger_chars: [8]u8 = undefined,
    n_sig_trigger: usize = 0,
    definition: bool = false,
    references: bool = false,
    rename: bool = false,
    formatting: bool = false,
    code_action: bool = false,
    code_action_resolve: bool = false,
    completion: bool = false,
    /// Messages waiting for the initialize response.
    queued: std.ArrayList([]u8) = .empty,

    pub fn isTrigger(self: *const Server, ch: u8) bool {
        return std.mem.indexOfScalar(u8, self.trigger_chars[0..self.n_trigger], ch) != null;
    }

    pub fn isSignatureTrigger(self: *const Server, ch: u8) bool {
        return std.mem.indexOfScalar(u8, self.sig_trigger_chars[0..self.n_sig_trigger], ch) != null;
    }

    pub fn state(self: *const Server) ServerState {
        if (self.dead) return .exited;
        if (self.init_failed) return .failed;
        return if (self.initialized) .ready else .starting;
    }

    /// The version the server reported, if it did.
    pub fn version(self: *const Server) []const u8 {
        return self.version_buf[0..self.version_len];
    }

    /// The local process id (null for a server on a remote host).
    pub fn pid(self: *const Server) ?i64 {
        if (self.remote != null) return null;
        return if (self.child.id) |id| @intCast(id) else null;
    }

    /// The most recently begun work still in progress.
    pub fn currentProgress(self: *const Server) ?*const Progress {
        if (self.progress.items.len == 0) return null;
        return &self.progress.items[self.progress.items.len - 1];
    }

    /// How many open documents the server has.
    pub fn docCount(self: *const Server) usize {
        var n: usize = 0;
        for (self.lsp.docs.items) |d| if (d.server == self) {
            n += 1;
        };
        return n;
    }

    fn clearProgress(self: *Server) void {
        const gpa = self.lsp.gpa;
        for (self.progress.items) |p| freeProgress(gpa, p);
        self.progress.clearRetainingCapacity();
    }

    fn sendRaw(self: *Server, body: []const u8) void {
        if (self.dead) return;
        var header: [64]u8 = undefined;
        const h = std.fmt.bufPrint(&header, "Content-Length: {d}\r\n\r\n", .{body.len}) catch return;
        writeAll(self.in_fd, h) catch {
            self.dead = true;
            return;
        };
        writeAll(self.in_fd, body) catch {
            self.dead = true;
        };
    }

    fn send(self: *Server, body: []u8, force: bool) void {
        if (!self.initialized and !force) {
            self.queued.append(self.lsp.gpa, body) catch self.lsp.gpa.free(body);
            return;
        }
        defer self.lsp.gpa.free(body);
        self.sendRaw(body);
    }

    /// Reads stdout until EOF (its end is the server's end) and, alongside,
    /// stderr, which goes to the log a batch of lines at a time.
    fn readerLoop(self: *Server) void {
        const gpa = self.lsp.gpa;
        var buf: std.ArrayList(u8) = .empty;
        defer buf.deinit(gpa);
        var err_buf: std.ArrayList(u8) = .empty;
        defer err_buf.deinit(gpa);
        var tmp: [65536]u8 = undefined;
        // A negative fd is skipped by poll (no stderr, or it reached EOF).
        var fds = [2]std.c.pollfd{
            .{ .fd = self.out_fd, .events = std.c.POLL.IN, .revents = 0 },
            .{ .fd = self.err_fd orelse -1, .events = std.c.POLL.IN, .revents = 0 },
        };
        outer: while (true) {
            if (fds[1].fd >= 0) {
                const rc = std.c.poll(&fds, fds.len, -1);
                if (rc < 0) {
                    if (std.c.errno(rc) == .INTR) continue;
                    break;
                }
                if (fds[1].revents != 0) self.readStderr(&fds[1], &err_buf, &tmp);
                if (fds[0].revents == 0) continue;
            }
            const n = std.c.read(self.out_fd, &tmp, tmp.len);
            if (n <= 0) {
                // A crash's last words are in stderr already: take what's
                // there without waiting (children may keep it open).
                var rounds: usize = 0;
                while (fds[1].fd >= 0 and rounds < 64) : (rounds += 1) {
                    if (std.c.poll(fds[1..].ptr, 1, 10) <= 0) break;
                    self.readStderr(&fds[1], &err_buf, &tmp);
                }
                self.postStderr(&err_buf, true);
                break;
            }
            buf.appendSlice(gpa, tmp[0..@intCast(n)]) catch break;
            while (true) {
                const header_end = std.mem.indexOf(u8, buf.items, "\r\n\r\n") orelse break;
                var len: ?usize = null;
                var lines = std.mem.splitSequence(u8, buf.items[0..header_end], "\r\n");
                while (lines.next()) |line| {
                    if (std.ascii.startsWithIgnoreCase(line, "content-length:")) {
                        len = std.fmt.parseInt(usize, std.mem.trim(u8, line[15..], " "), 10) catch null;
                    }
                }
                const body_len = len orelse break :outer;
                const total = header_end + 4 + body_len;
                if (buf.items.len < total) break;
                const body = buf.items[header_end + 4 .. total];
                if (std.json.parseFromSlice(Value, gpa, body, .{})) |parsed| {
                    self.lsp.post(self, parsed);
                } else |err| log.warn("{s}: bad JSON: {t}", .{ self.config.name, err });
                std.mem.copyForwards(u8, buf.items, buf.items[total..]);
                buf.shrinkRetainingCapacity(buf.items.len - total);
            }
        }
        self.lsp.post(self, null);
    }

    fn readStderr(self: *Server, pfd: *std.c.pollfd, err_buf: *std.ArrayList(u8), tmp: *[65536]u8) void {
        const n = std.c.read(pfd.fd, tmp, tmp.len);
        if (n <= 0) {
            pfd.fd = -1;
            return;
        }
        err_buf.appendSlice(self.lsp.gpa, tmp[0..@intCast(n)]) catch return;
        self.postStderr(err_buf, false);
    }

    /// Hands the complete lines of `err_buf` (all of it at EOF, or when one
    /// line grows huge) to the main thread.
    fn postStderr(self: *Server, err_buf: *std.ArrayList(u8), eof: bool) void {
        const gpa = self.lsp.gpa;
        const end = if (eof or err_buf.items.len > 1 << 16)
            err_buf.items.len
        else if (std.mem.lastIndexOfScalar(u8, err_buf.items, '\n')) |i| i + 1 else 0;
        if (end == 0) return;
        const text = gpa.dupe(u8, err_buf.items[0..end]) catch return;
        std.mem.copyForwards(u8, err_buf.items, err_buf.items[end..]);
        err_buf.shrinkRetainingCapacity(err_buf.items.len - end);
        const chunk = gpa.create(StderrChunk) catch return gpa.free(text);
        chunk.* = .{ .server = self, .text = text };
        self.lsp.executor.post(.{ .ctx = chunk, .run = runStderr });
    }
};

/// Lines a server wrote to stderr, on their way to the main thread.
const StderrChunk = struct {
    server: *Server,
    text: []u8,
};

fn runStderr(ctx: *anyopaque) void {
    const chunk: *StderrChunk = @ptrCast(@alignCast(ctx));
    const self = chunk.server.lsp;
    defer {
        self.gpa.free(chunk.text);
        self.gpa.destroy(chunk);
    }
    if (self.shutting_down) return;
    var it = std.mem.splitScalar(u8, chunk.text, '\n');
    while (it.next()) |line| {
        if (std.mem.trim(u8, line, " \t\r").len == 0) continue;
        self.addLog(chunk.server.config, .stderr, "{s}", .{line});
    }
}

fn freeProgress(gpa: Allocator, p: Progress) void {
    gpa.free(p.token);
    gpa.free(p.title);
    gpa.free(p.message);
}

fn writeAll(fd: std.c.fd_t, bytes: []const u8) !void {
    var off: usize = 0;
    while (off < bytes.len) {
        const n = std.c.write(fd, bytes[off..].ptr, bytes.len - off);
        if (n <= 0) return error.WriteFailed;
        off += @intCast(n);
    }
}

// ---- Documents ------------------------------------------------------------------

const PendingChange = struct {
    start: Position,
    end: Position,
    text: []u8,
};

const Doc = struct {
    lsp: *Lsp,
    buffer: *Buffer,
    server: *Server,
    uri: []u8,
    version: i32 = 1,
    pending: std.ArrayList(PendingChange) = .empty,
    full_dirty: bool = false,
    scratch: std.ArrayList(u8) = .empty,

    fn onChange(ctx: *anyopaque, buffer: *Buffer, change: buffer_mod.Change) void {
        const self: *Doc = @ptrCast(@alignCast(ctx));
        const gpa = self.lsp.gpa;
        if (self.server.sync == .incremental and self.server.encoding == .utf8 and !self.full_dirty) {
            const text = buffer.textRange(gpa, change.start, change.start + change.new_len) catch {
                self.full_dirty = true;
                return;
            };
            self.pending.append(gpa, .{
                .start = .{ .line = change.start_point.row, .character = change.start_point.column },
                .end = .{ .line = change.old_end_point.row, .character = change.old_end_point.column },
                .text = text,
            }) catch {
                gpa.free(text);
                self.full_dirty = true;
            };
        } else {
            self.full_dirty = true;
        }
    }

    fn clearPending(self: *Doc) void {
        for (self.pending.items) |p| self.lsp.gpa.free(p.text);
        self.pending.clearRetainingCapacity();
    }
};

const RequestKind = enum { initialize, hover, signature_help, goto, references, completion, rename, formatting, code_action, code_action_resolve, execute_command, shutdown };

const Pending = struct {
    server: *Server,
    kind: RequestKind,
    goto: Goto = .definition,
    buffer: ?*Buffer = null,
    offset: usize = 0,
    version: u64 = 0,
};

const Incoming = struct {
    lsp: *Lsp,
    server: *Server,
    parsed: ?std.json.Parsed(Value),
};

pub const Lsp = struct {
    gpa: Allocator,
    io: Io,
    executor: *Executor,
    sink: Sink,
    root: ?[]u8 = null,
    /// Every server started, oldest first; a restart leaves the old ones
    /// here, exited (see `statusList`).
    servers: std.ArrayList(*Server) = .empty,
    /// Configs that failed to start, and why (not retried until restart).
    failed: std.StringArrayHashMapUnmanaged(anyerror) = .empty,
    docs: std.ArrayList(*Doc) = .empty,
    log: std.ArrayList(LogEntry) = .empty,
    next_log_seq: u64 = 1,
    /// Changes when entries are dropped or cleared (views of the log rebuild).
    log_epoch: u32 = 0,
    pending: std.AutoHashMapUnmanaged(i64, Pending) = .empty,
    next_id: i64 = 1,
    shutting_down: bool = false,
    last_flush: i64 = 0,
    /// settings.json "lsp": { "<server>": { "settings": { "a.b": 1, ... } } },
    /// answered to workspace/configuration on top of built-in defaults.
    user_config: ?std.json.Parsed(Value) = null,

    pub fn init(gpa: Allocator, io: Io, executor: *Executor, sink: Sink) Lsp {
        return .{ .gpa = gpa, .io = io, .executor = executor, .sink = sink };
    }

    pub fn deinit(self: *Lsp) void {
        self.shutting_down = true;
        for (self.servers.items) |s| self.stopServer(s);
        // Drop messages the readers posted before exiting.
        self.executor.drain();
        for (self.servers.items) |s| {
            for (s.queued.items) |q| self.gpa.free(q);
            s.queued.deinit(self.gpa);
            s.clearProgress();
            s.progress.deinit(self.gpa);
            self.gpa.destroy(s);
        }
        self.servers.deinit(self.gpa);
        for (self.docs.items) |d| self.freeDoc(d);
        self.docs.deinit(self.gpa);
        self.pending.deinit(self.gpa);
        self.failed.deinit(self.gpa);
        for (self.log.items) |e| self.gpa.free(e.text);
        self.log.deinit(self.gpa);
        if (self.root) |r| self.gpa.free(r);
        if (self.user_config) |*u| u.deinit();
    }

    // ---- Status and log ---------------------------------------------------

    pub const Status = struct {
        name: []const u8,
        state: ServerState,
        /// The running (or exited) server; null if it never started.
        server: ?*Server = null,
        /// Why it couldn't be started.
        err: ?anyerror = null,
    };

    /// The newest server of each config that was started, in start order,
    /// then the configs that failed to start.
    pub fn statusList(self: *Lsp, gpa: Allocator, out: *std.ArrayList(Status)) !void {
        for (self.servers.items, 0..) |s, i| {
            const newer = for (self.servers.items[i + 1 ..]) |later| {
                if (later.config == s.config) break true;
            } else false;
            if (newer or self.failed.contains(s.config.name)) continue;
            try out.append(gpa, .{ .name = s.config.name, .state = s.state(), .server = s });
        }
        var it = self.failed.iterator();
        while (it.next()) |e| try out.append(gpa, .{ .name = e.key_ptr.*, .state = .failed, .err = e.value_ptr.* });
    }

    /// The status of the server for `buffer`, or null if its language has
    /// none (or it can't have one: no path yet, or on another machine).
    pub fn statusFor(self: *Lsp, buffer: *const Buffer) ?Status {
        // docFor skips exited servers; an exit is worth showing.
        for (self.docs.items) |d| if (d.buffer == buffer) return .{ .name = d.server.config.name, .state = d.server.state(), .server = d.server };
        const config = configFor(buffer.language.name) orelse return null;
        if (self.failed.get(config.name)) |err| return .{ .name = config.name, .state = .failed, .err = err };
        return null;
    }

    fn addLog(self: *Lsp, server: ?*const Config, level: LogLevel, comptime fmt: []const u8, args: anytype) void {
        const text = std.fmt.allocPrint(self.gpa, fmt, args) catch return;
        const clean = sanitizeLog(text);
        if (clean.len == 0) return self.gpa.free(text);
        if (self.log.items.len >= max_log_entries) {
            const drop = max_log_entries / 10;
            for (self.log.items[0..drop]) |e| self.gpa.free(e.text);
            self.log.replaceRangeAssumeCapacity(0, drop, &.{});
            self.log_epoch +%= 1;
        }
        // `clean` is a prefix of `text`: copy it out unless that's all of it.
        const owned = if (clean.len == text.len) text else blk: {
            defer self.gpa.free(text);
            break :blk self.gpa.dupe(u8, clean) catch return;
        };
        self.log.append(self.gpa, .{
            .seq = self.next_log_seq,
            .time_ms = wallMs(),
            .server = if (server) |s| s.name else null,
            .level = level,
            .text = owned,
        }) catch return self.gpa.free(owned);
        self.next_log_seq += 1;
        self.changed();
    }

    pub fn clearLog(self: *Lsp) void {
        for (self.log.items) |e| self.gpa.free(e.text);
        self.log.clearRetainingCapacity();
        self.log_epoch +%= 1;
        self.changed();
    }

    /// Tells the UI to repaint (state, progress or the log changed).
    fn changed(self: *Lsp) void {
        if (!self.shutting_down) self.sink.onResult(self.sink.ctx, .status_changed);
    }

    /// Replaces the per-server settings (JSON text of settings.json's "lsp"
    /// object, or null) and tells running servers to re-read them.
    pub fn setConfiguration(self: *Lsp, json: ?[]const u8) void {
        if (self.user_config) |*u| u.deinit();
        self.user_config = null;
        if (json) |j| self.user_config = std.json.parseFromSlice(Value, self.gpa, j, .{}) catch null;
        for (self.servers.items) |s| if (s.initialized and !s.dead) {
            self.notify(s, "workspace/didChangeConfiguration", .{ .settings = Empty{} });
        };
    }

    /// The configuration tree for a server: defaults, then the user's
    /// settings (dotted keys like "zls.enable_build_on_save" become nested).
    fn settingsTree(self: *Lsp, alloc: Allocator, server: *Server) !Value {
        var root: Value = .{ .object = .empty };
        if (std.mem.eql(u8, server.config.name, "zls")) {
            // Type errors only come from build-on-save; the "check" step is
            // the fast compile-without-install convention zls documents.
            try setPath(alloc, &root, "zls.enable_build_on_save", .{ .bool = true });
            if (self.rootHasStep(alloc, "check")) try setPath(alloc, &root, "zls.build_on_save_step", .{ .string = "check" });
        }
        const user = (self.user_config orelse return root).value;
        if (user != .object) return root;
        const entry = user.object.get(server.config.name) orelse return root;
        const settings = if (entry == .object and entry.object.get("settings") != null) entry.object.get("settings").? else entry;
        if (settings != .object) return root;
        var it = settings.object.iterator();
        while (it.next()) |e| try setPath(alloc, &root, e.key_ptr.*, e.value_ptr.*);
        return root;
    }

    fn rootHasStep(self: *Lsp, alloc: Allocator, step: []const u8) bool {
        const root = self.root orelse return false;
        const path = std.fs.path.join(alloc, &.{ root, "build.zig" }) catch return false;
        const text = vfs.readFile(alloc, self.io, path, 4 << 20) catch return false;
        const needle = std.fmt.allocPrint(alloc, "b.step(\"{s}\"", .{step}) catch return false;
        return std.mem.indexOf(u8, text, needle) != null;
    }

    /// Sets `dotted` ("a.b.c") in a nested object tree, merging objects.
    fn setPath(alloc: Allocator, root: *Value, dotted: []const u8, v: Value) !void {
        var node = root;
        var it = std.mem.splitScalar(u8, dotted, '.');
        while (it.next()) |part| {
            if (node.* != .object) node.* = .{ .object = .empty };
            if (it.peek() == null) {
                if (v == .object) {
                    var sub = v.object.iterator();
                    while (sub.next()) |e| {
                        const key = try std.fmt.allocPrint(alloc, "{s}.{s}", .{ part, e.key_ptr.* });
                        try setPath(alloc, node, key, e.value_ptr.*);
                    }
                } else try node.object.put(alloc, part, v);
                return;
            }
            const gop = try node.object.getOrPut(alloc, part);
            if (!gop.found_existing) gop.value_ptr.* = .{ .object = .empty };
            node = gop.value_ptr;
        }
    }

    fn lookupSection(root: Value, section: ?[]const u8) Value {
        const sec = section orelse return root;
        var node = root;
        var it = std.mem.splitScalar(u8, sec, '.');
        while (it.next()) |part| {
            if (node != .object) return .null;
            node = node.object.get(part) orelse return .null;
        }
        return node;
    }

    fn stopServer(self: *Lsp, s: *Server) void {
        if (!s.dead) self.addLog(s.config, .info, "Stopping {s}", .{s.config.name});
        if (!s.dead and s.initialized) {
            self.request(s, .shutdown, "shutdown", null, .{ .server = s, .kind = .shutdown }) catch {};
            self.notify(s, "exit", null);
        }
        s.dead = true;
        s.stopping = true;
        s.clearProgress();
        // Keep stdout and stderr open until the reader thread has seen EOF.
        const out = s.child.stdout;
        const err = s.child.stderr;
        s.child.stdout = null;
        s.child.stderr = null;
        s.child.kill(self.io);
        if (s.remote) |p| p.kill();
        if (s.reader) |t| t.join();
        s.reader = null;
        if (out) |f| _ = std.c.close(f.handle);
        if (err) |f| _ = std.c.close(f.handle);
        s.err_fd = null;
        if (s.remote) |p| p.destroy();
        s.remote = null;
    }

    fn freeDoc(self: *Lsp, d: *Doc) void {
        d.buffer.removeListener(d);
        d.clearPending();
        d.pending.deinit(self.gpa);
        d.scratch.deinit(self.gpa);
        self.gpa.free(d.uri);
        self.gpa.destroy(d);
    }

    /// A remote server's paths are its host's: put the ssh://host prefix back.
    fn localPath(self: *Lsp, alloc: Allocator, path: []const u8) []const u8 {
        const prefix = rpath.authority(self.root orelse return path) orelse return path;
        return std.fmt.allocPrint(alloc, "{s}{s}", .{ prefix, path }) catch path;
    }

    pub fn setRoot(self: *Lsp, root: []const u8) void {
        if (self.root) |r| self.gpa.free(r);
        self.root = self.gpa.dupe(u8, root) catch null;
    }

    /// Thread-safe: hand a parsed message (or null = server exited) to the main thread.
    fn post(self: *Lsp, server: *Server, parsed: ?std.json.Parsed(Value)) void {
        const incoming = self.gpa.create(Incoming) catch {
            if (parsed) |p| p.deinit();
            return;
        };
        incoming.* = .{ .lsp = self, .server = server, .parsed = parsed };
        self.executor.post(.{ .ctx = incoming, .run = runIncoming });
    }

    fn runIncoming(ctx: *anyopaque) void {
        const incoming: *Incoming = @ptrCast(@alignCast(ctx));
        const self = incoming.lsp;
        defer self.gpa.destroy(incoming);
        if (incoming.parsed) |*p| {
            defer p.deinit();
            if (!self.shutting_down) self.handleMessage(incoming.server, p.value);
        } else {
            const s = incoming.server;
            s.dead = true;
            s.clearProgress();
            // A stop (restart) logged itself; anything else is news.
            if (self.shutting_down or s.stopping) return self.changed();
            self.addLog(s.config, .err, "{s} exited", .{s.config.name});
            var buf: [96]u8 = undefined;
            const msg = std.fmt.bufPrint(&buf, "Language server {s} exited", .{s.config.name}) catch "Language server exited";
            self.sink.onResult(self.sink.ctx, .{ .message = msg });
        }
    }

    // ---- Sending ----------------------------------------------------------

    fn request(self: *Lsp, server: *Server, kind: RequestKind, method: []const u8, params: anytype, pending: Pending) !void {
        const id = self.next_id;
        self.next_id += 1;
        const body = try std.json.Stringify.valueAlloc(self.gpa, .{ .jsonrpc = "2.0", .id = id, .method = method, .params = params }, .{});
        var p = pending;
        p.server = server;
        p.kind = kind;
        try self.pending.put(self.gpa, id, p);
        server.send(body, kind == .initialize or kind == .shutdown);
    }

    fn notify(self: *Lsp, server: *Server, method: []const u8, params: anytype) void {
        const body = std.json.Stringify.valueAlloc(self.gpa, .{ .jsonrpc = "2.0", .method = method, .params = params }, .{}) catch return;
        server.send(body, false);
    }

    fn respond(self: *Lsp, server: *Server, id: Value, result: anytype) void {
        const body = std.json.Stringify.valueAlloc(self.gpa, .{ .jsonrpc = "2.0", .id = id, .result = result }, .{}) catch return;
        server.send(body, true);
    }

    // ---- Servers ----------------------------------------------------------

    fn serverFor(self: *Lsp, config: *const Config) ?*Server {
        // deinit's drain can run code that opens buffers; it frees servers
        // without stopping them, so none may start by then.
        if (self.shutting_down) return null;
        for (self.servers.items) |s| if (s.config == config and !s.dead) return s;
        if (self.failed.contains(config.name)) return null;
        return self.startServer(config) catch |err| {
            log.debug("{s} unavailable: {t}", .{ config.name, err });
            if (err == error.FileNotFound) {
                self.addLog(config, .err, "Unable to start {s}: {s} isn't installed (not found on PATH)", .{ config.name, config.argv[0] });
            } else self.addLog(config, .err, "Unable to start {s}: {t}", .{ config.name, err });
            self.failed.put(self.gpa, config.name, err) catch {};
            return null;
        };
    }

    fn startServer(self: *Lsp, config: *const Config) !*Server {
        const root = self.root orelse ".";
        // With a remote root the server runs on that host (jiffy-edit-server
        // proxies its stdio) and sees the host's own paths.
        const remote_root = rpath.parse(root);
        {
            var cmd: std.Io.Writer.Allocating = .init(self.gpa);
            defer cmd.deinit();
            for (config.argv, 0..) |arg, i| cmd.writer.print("{s}{s}", .{ if (i > 0) " " else "", arg }) catch {};
            self.addLog(config, .info, "Starting {s} in {s}", .{ cmd.written(), root });
        }
        const proc: ?*vfs.Process = if (remote_root != null) try vfs.spawnRemote(config.argv, root) else null;
        errdefer if (proc) |p| {
            p.kill();
            p.destroy();
        };
        var child: std.process.Child = if (proc != null)
            .{ .id = null, .thread_handle = {}, .stdin = null, .stdout = null, .stderr = null, .request_resource_usage_statistics = false }
        else
            try std.process.spawn(self.io, .{
                .argv = config.argv,
                .cwd = .{ .path = root },
                .stdin = .pipe,
                .stdout = .pipe,
                // Read into the log by the reader thread.
                .stderr = .pipe,
            });
        errdefer child.kill(self.io);
        const s = try self.gpa.create(Server);
        s.* = .{
            .lsp = self,
            .config = config,
            .child = child,
            .in_fd = if (proc) |p| p.stdin_fd else child.stdin.?.handle,
            .out_fd = if (proc) |p| p.stdout_fd else child.stdout.?.handle,
            .err_fd = if (child.stderr) |f| f.handle else null,
            .remote = proc,
            .started_ms = platform.nowMs(),
        };
        try self.servers.append(self.gpa, s);
        s.reader = try std.Thread.spawn(.{}, Server.readerLoop, .{s});

        const root_uri = try protocol.pathToUri(self.gpa, root);
        defer self.gpa.free(root_uri);
        const Folder = struct { uri: []const u8, name: []const u8 };
        try self.request(s, .initialize, "initialize", .{
            // A remote server watches the remote jiffy-edit-server's pid.
            .processId = if (remote_root != null) vfs.serverPid(root) else @as(?i64, std.c.getpid()),
            .clientInfo = .{ .name = "cs-zig", .version = "0.1.0" },
            .rootUri = root_uri,
            .rootPath = if (remote_root) |r| r.path else root,
            .workspaceFolders = &[_]Folder{.{ .uri = root_uri, .name = std.fs.path.basename(root) }},
            .capabilities = .{
                .general = .{ .positionEncodings = &[_][]const u8{ "utf-8", "utf-16" } },
                .textDocument = .{
                    .synchronization = .{ .didSave = true, .dynamicRegistration = false },
                    .hover = .{ .contentFormat = &[_][]const u8{ "plaintext", "markdown" } },
                    .signatureHelp = .{ .signatureInformation = .{
                        .documentationFormat = &[_][]const u8{ "plaintext", "markdown" },
                        .parameterInformation = .{ .labelOffsetSupport = true },
                        .activeParameterSupport = true,
                    } },
                    .completion = .{ .completionItem = .{ .snippetSupport = false, .documentationFormat = &[_][]const u8{ "plaintext", "markdown" } } },
                    .definition = .{ .linkSupport = true },
                    .implementation = .{ .linkSupport = true },
                    .typeDefinition = .{ .linkSupport = true },
                    .references = Empty{},
                    .rename = .{ .prepareSupport = false },
                    .formatting = Empty{},
                    .codeAction = .{
                        .codeActionLiteralSupport = .{ .codeActionKind = .{ .valueSet = &[_][]const u8{ "", "quickfix", "refactor", "refactor.extract", "refactor.inline", "refactor.rewrite", "source", "source.organizeImports", "source.fixAll" } } },
                        .isPreferredSupport = true,
                        .disabledSupport = true,
                        .dataSupport = true,
                        .resolveSupport = .{ .properties = &[_][]const u8{"edit"} },
                    },
                    .publishDiagnostics = .{ .relatedInformation = false },
                },
                .workspace = .{
                    .workspaceFolders = true,
                    .configuration = true,
                    .applyEdit = true,
                    .workspaceEdit = .{ .documentChanges = true },
                    .executeCommand = Empty{},
                },
                // $/progress: shown in the status bar and the Language Servers panel.
                .window = .{ .workDoneProgress = true },
            },
        }, .{ .server = s, .kind = .initialize });
        log.debug("started {s} in {s}", .{ config.name, root });
        if (s.pid()) |id| self.addLog(config, .info, "Started {s} (pid {d})", .{ config.name, id });
        return s;
    }

    pub fn restartAll(self: *Lsp) void {
        if (self.servers.items.len > 0 or self.failed.count() > 0) self.addLog(null, .info, "Restarting language servers", .{});
        for (self.servers.items) |s| self.stopServer(s);
        self.failed.clearRetainingCapacity();
        // Docs re-open lazily: drop them; the UI re-registers open buffers.
        for (self.docs.items) |d| self.freeDoc(d);
        self.docs.clearRetainingCapacity();
    }

    // ---- Documents --------------------------------------------------------

    fn docFor(self: *Lsp, buffer: *const Buffer) ?*Doc {
        for (self.docs.items) |d| if (d.buffer == buffer) {
            if (d.server.dead) return null;
            return d;
        };
        return null;
    }

    pub fn hasServer(self: *Lsp, buffer: *const Buffer) bool {
        return self.docFor(buffer) != null;
    }

    pub fn serverName(self: *Lsp, buffer: *const Buffer) ?[]const u8 {
        const d = self.docFor(buffer) orelse return null;
        return d.server.config.name;
    }

    pub fn isSignatureTriggerChar(self: *Lsp, buffer: *const Buffer, ch: u8) bool {
        const d = self.docFor(buffer) orelse return false;
        return d.server.isSignatureTrigger(ch);
    }

    pub fn isTriggerChar(self: *Lsp, buffer: *const Buffer, ch: u8) bool {
        const d = self.docFor(buffer) orelse return false;
        return d.server.isTrigger(ch);
    }

    /// Registers a buffer with its language server (starting it if needed).
    pub fn openBuffer(self: *Lsp, buffer: *Buffer) void {
        const path = buffer.path orelse return;
        if (self.docFor(buffer) != null) return;
        const config = configFor(buffer.language.name) orelse return;
        // Servers run where the root is; a file on another machine gets none.
        if (self.root) |r| if (!rpath.sameMachine(r, path)) return;
        const server = self.serverFor(config) orelse return;
        const doc = self.gpa.create(Doc) catch return;
        doc.* = .{ .lsp = self, .buffer = buffer, .server = server, .uri = protocol.pathToUri(self.gpa, path) catch {
            self.gpa.destroy(doc);
            return;
        } };
        self.docs.append(self.gpa, doc) catch return;
        buffer.addListener(.{ .ctx = doc, .onChange = Doc.onChange }) catch {};
        const text = buffer.rope.toOwnedSlice(self.gpa) catch return;
        defer self.gpa.free(text);
        self.notify(server, "textDocument/didOpen", .{ .textDocument = .{
            .uri = doc.uri,
            .languageId = languageId(buffer.language.name),
            .version = doc.version,
            .text = text,
        } });
    }

    pub fn closeBuffer(self: *Lsp, buffer: *Buffer) void {
        for (self.docs.items, 0..) |d, i| if (d.buffer == buffer) {
            self.notify(d.server, "textDocument/didClose", .{ .textDocument = .{ .uri = d.uri } });
            _ = self.docs.swapRemove(i);
            self.freeDoc(d);
            return;
        };
    }

    pub fn savedBuffer(self: *Lsp, buffer: *Buffer) void {
        self.flushDoc(self.docFor(buffer) orelse return);
        const d = self.docFor(buffer).?;
        self.notify(d.server, "textDocument/didSave", .{ .textDocument = .{ .uri = d.uri } });
    }

    /// Sends pending didChange notifications (call every frame).
    pub fn flush(self: *Lsp) void {
        for (self.docs.items) |d| self.flushDoc(d);
    }

    fn flushDoc(self: *Lsp, d: *Doc) void {
        if (d.server.dead or (!d.full_dirty and d.pending.items.len == 0)) return;
        d.version += 1;
        const Change = struct { range: struct { start: Position, end: Position }, text: []const u8 };
        if (d.full_dirty) {
            const text = d.buffer.rope.toOwnedSlice(self.gpa) catch return;
            defer self.gpa.free(text);
            const FullChange = struct { text: []const u8 };
            self.notify(d.server, "textDocument/didChange", .{
                .textDocument = .{ .uri = d.uri, .version = d.version },
                .contentChanges = &[_]FullChange{.{ .text = text }},
            });
        } else {
            const changes = self.gpa.alloc(Change, d.pending.items.len) catch return;
            defer self.gpa.free(changes);
            for (d.pending.items, changes) |p, *ch| ch.* = .{ .range = .{ .start = p.start, .end = p.end }, .text = p.text };
            self.notify(d.server, "textDocument/didChange", .{
                .textDocument = .{ .uri = d.uri, .version = d.version },
                .contentChanges = changes,
            });
        }
        d.clearPending();
        d.full_dirty = false;
    }

    fn positionParams(d: *Doc, offset: usize) struct { textDocument: struct { uri: []const u8 }, position: Position } {
        return .{
            .textDocument = .{ .uri = d.uri },
            .position = protocol.offsetToPosition(d.buffer, offset, d.server.encoding, &d.scratch),
        };
    }

    // ---- Requests ---------------------------------------------------------

    pub fn hover(self: *Lsp, buffer: *Buffer, offset: usize) bool {
        const d = self.docFor(buffer) orelse return false;
        self.flushDoc(d);
        self.request(d.server, .hover, "textDocument/hover", positionParams(d, offset), .{ .server = d.server, .kind = .hover, .buffer = buffer, .offset = offset }) catch return false;
        return true;
    }

    /// The signature of the call around `offset`; the answer is a
    /// `.signature_help` result.
    pub fn signatureHelp(self: *Lsp, buffer: *Buffer, offset: usize, trigger: ?u8) bool {
        const d = self.docFor(buffer) orelse return false;
        if (!d.server.signature_help and d.server.initialized) return false;
        self.flushDoc(d);
        const pp = positionParams(d, offset);
        var trig_buf: [1]u8 = .{trigger orelse 0};
        const Context = struct { triggerKind: u8, triggerCharacter: ?[]const u8, isRetrigger: bool };
        self.request(d.server, .signature_help, "textDocument/signatureHelp", .{
            .textDocument = pp.textDocument,
            .position = pp.position,
            .context = Context{ .triggerKind = if (trigger != null) 2 else 1, .triggerCharacter = if (trigger != null) &trig_buf else null, .isRetrigger = false },
        }, .{ .server = d.server, .kind = .signature_help, .buffer = buffer, .offset = offset }) catch return false;
        return true;
    }

    /// Where the symbol at `offset` is defined, implemented or has its type
    /// defined; the answer is a `.goto` result.
    pub fn goto(self: *Lsp, buffer: *Buffer, offset: usize, what: Goto) bool {
        const d = self.docFor(buffer) orelse return false;
        self.flushDoc(d);
        self.request(d.server, .goto, what.method(), positionParams(d, offset), .{ .server = d.server, .kind = .goto, .goto = what }) catch return false;
        return true;
    }

    pub fn references(self: *Lsp, buffer: *Buffer, offset: usize) bool {
        const d = self.docFor(buffer) orelse return false;
        self.flushDoc(d);
        const pp = positionParams(d, offset);
        self.request(d.server, .references, "textDocument/references", .{
            .textDocument = pp.textDocument,
            .position = pp.position,
            .context = .{ .includeDeclaration = true },
        }, .{ .server = d.server, .kind = .references }) catch return false;
        return true;
    }

    pub fn rename(self: *Lsp, buffer: *Buffer, offset: usize, new_name: []const u8) bool {
        const d = self.docFor(buffer) orelse return false;
        self.flushDoc(d);
        const pp = positionParams(d, offset);
        self.request(d.server, .rename, "textDocument/rename", .{
            .textDocument = pp.textDocument,
            .position = pp.position,
            .newName = new_name,
        }, .{ .server = d.server, .kind = .rename }) catch return false;
        return true;
    }

    /// The code actions for `start`..`end` (fixes for the diagnostics
    /// there, refactorings); the answer is a `.code_actions` result.
    pub fn codeActions(self: *Lsp, buffer: *Buffer, start: usize, end: usize) bool {
        const d = self.docFor(buffer) orelse return false;
        if (!d.server.code_action) return false;
        self.flushDoc(d);
        const Diag = struct { range: protocol.Range, severity: u8, message: []const u8, source: []const u8 };
        var diags: std.ArrayList(Diag) = .empty;
        defer diags.deinit(self.gpa);
        // The diagnostics touching the range, as the server sent them (the
        // range, message and source are what servers match fixes by).
        for (buffer.diagnostics.items) |diag| {
            if (diag.end < start or diag.start > end) continue;
            diags.append(self.gpa, .{
                .range = .{
                    .start = protocol.offsetToPosition(buffer, diag.start, d.server.encoding, &d.scratch),
                    .end = protocol.offsetToPosition(buffer, diag.end, d.server.encoding, &d.scratch),
                },
                .severity = @intFromEnum(diag.severity),
                .message = diag.message,
                .source = diag.source,
            }) catch break;
        }
        self.request(d.server, .code_action, "textDocument/codeAction", .{
            .textDocument = .{ .uri = d.uri },
            .range = protocol.Range{
                .start = protocol.offsetToPosition(buffer, start, d.server.encoding, &d.scratch),
                .end = protocol.offsetToPosition(buffer, end, d.server.encoding, &d.scratch),
            },
            .context = .{ .diagnostics = diags.items, .triggerKind = 1 },
        }, .{ .server = d.server, .kind = .code_action, .buffer = buffer }) catch return false;
        return true;
    }

    /// Runs an action from a `.code_actions` result: applies its edit (as
    /// a `.workspace_edit` result), resolving it first when the server sends
    /// edits lazily, and runs its command.
    pub fn runCodeAction(self: *Lsp, buffer: *Buffer, json: []const u8) bool {
        const d = self.docFor(buffer) orelse return false;
        const parsed = std.json.parseFromSlice(Value, self.gpa, json, .{}) catch return false;
        defer parsed.deinit();
        const action = parsed.value;
        // A bare Command: {title, command: "name", arguments}.
        if (protocol.get(action, "command")) |c| if (c == .string) {
            self.executeCommand(d.server, action);
            return true;
        };
        if (protocol.get(action, "edit") == null and d.server.code_action_resolve) {
            self.request(d.server, .code_action_resolve, "codeAction/resolve", action, .{ .server = d.server, .kind = .code_action_resolve }) catch return false;
            return true;
        }
        self.applyCodeAction(d.server, action);
        return true;
    }

    fn applyCodeAction(self: *Lsp, server: *Server, action: Value) void {
        if (protocol.get(action, "edit")) |e| self.emitWorkspaceEdit(server, e);
        if (protocol.get(action, "command")) |c| if (c == .object) self.executeCommand(server, c);
    }

    fn executeCommand(self: *Lsp, server: *Server, command: Value) void {
        const name = protocol.getStr(command, "command") orelse return;
        const args = protocol.get(command, "arguments") orelse Value{ .array = .init(self.gpa) };
        self.request(server, .execute_command, "workspace/executeCommand", .{ .command = name, .arguments = args }, .{ .server = server, .kind = .execute_command }) catch {};
    }

    fn emitWorkspaceEdit(self: *Lsp, server: *Server, edit: Value) void {
        var arena = std.heap.ArenaAllocator.init(self.gpa);
        defer arena.deinit();
        const alloc = arena.allocator();
        var files: std.ArrayList(FileEdit) = .empty;
        collectWorkspaceEdit(alloc, edit, &files);
        for (files.items) |*f| f.path = self.localPath(alloc, f.path);
        if (files.items.len > 0) self.sink.onResult(self.sink.ctx, .{ .workspace_edit = .{ .files = files.items, .encoding = server.encoding } });
    }

    pub fn format(self: *Lsp, buffer: *Buffer) bool {
        const d = self.docFor(buffer) orelse return false;
        self.flushDoc(d);
        self.request(d.server, .formatting, "textDocument/formatting", .{
            .textDocument = .{ .uri = d.uri },
            .options = .{ .tabSize = buffer.tab_size, .insertSpaces = buffer.translate_tabs_to_spaces },
        }, .{ .server = d.server, .kind = .formatting, .buffer = buffer, .version = buffer.version }) catch return false;
        return true;
    }

    pub fn completion(self: *Lsp, buffer: *Buffer, offset: usize, trigger: ?u8) bool {
        const d = self.docFor(buffer) orelse return false;
        if (!d.server.completion and d.server.initialized) return false;
        self.flushDoc(d);
        const pp = positionParams(d, offset);
        var trig_buf: [1]u8 = .{trigger orelse 0};
        const Context = struct { triggerKind: u8, triggerCharacter: ?[]const u8 };
        self.request(d.server, .completion, "textDocument/completion", .{
            .textDocument = pp.textDocument,
            .position = pp.position,
            .context = Context{ .triggerKind = if (trigger != null) 2 else 1, .triggerCharacter = if (trigger != null) &trig_buf else null },
        }, .{ .server = d.server, .kind = .completion, .buffer = buffer, .offset = offset, .version = buffer.version }) catch return false;
        return true;
    }

    // ---- Receiving --------------------------------------------------------

    fn handleMessage(self: *Lsp, server: *Server, msg: Value) void {
        const method = protocol.getStr(msg, "method");
        const id = protocol.get(msg, "id");
        if (method) |m| {
            if (id) |req_id| return self.handleServerRequest(server, req_id, m, protocol.get(msg, "params"));
            return self.handleNotification(server, m, protocol.get(msg, "params"));
        }
        const id_val = id orelse return;
        if (id_val != .integer) return;
        const p = self.pending.fetchRemove(id_val.integer) orelse return;
        if (protocol.get(msg, "error")) |err| {
            const text = protocol.getStr(err, "message") orelse "request failed";
            const code = protocol.getInt(err, "code") orelse 0;
            // RequestCancelled / ContentModified: the answer was overtaken by edits.
            const routine = code == -32800 or code == -32801;
            if (!routine) self.addLog(server.config, if (p.value.kind == .initialize) .err else .warning, "{t} request failed: {s}", .{ p.value.kind, text });
            if (p.value.kind == .initialize) {
                server.init_failed = true;
                self.changed();
            }
            if (p.value.kind != .completion and p.value.kind != .hover and p.value.kind != .signature_help) {
                self.sink.onResult(self.sink.ctx, .{ .message = text });
            }
            return;
        }
        const result = protocol.get(msg, "result") orelse Value.null;
        self.handleResponse(server, p.value, result);
    }

    fn handleServerRequest(self: *Lsp, server: *Server, id: Value, method: []const u8, params: ?Value) void {
        if (std.mem.eql(u8, method, "workspace/configuration")) {
            // One value per requested item, looked up by its section.
            var arena = std.heap.ArenaAllocator.init(self.gpa);
            defer arena.deinit();
            const alloc = arena.allocator();
            const tree = self.settingsTree(alloc, server) catch Value.null;
            const items = protocol.get(params, "items");
            const list = if (items != null and items.? == .array) items.?.array.items else &.{};
            const out = alloc.alloc(Value, list.len) catch return;
            for (list, out) |item, *o| o.* = lookupSection(tree, protocol.getStr(item, "section"));
            self.respond(server, id, out);
            return;
        }
        if (std.mem.eql(u8, method, "workspace/workspaceFolders")) {
            const root_uri = protocol.pathToUri(self.gpa, self.root orelse ".") catch return;
            defer self.gpa.free(root_uri);
            const Folder = struct { uri: []const u8, name: []const u8 };
            self.respond(server, id, &[_]Folder{.{ .uri = root_uri, .name = std.fs.path.basename(self.root orelse ".") }});
            return;
        }
        if (std.mem.eql(u8, method, "workspace/applyEdit")) {
            // A command (from a code action) changing files.
            if (protocol.get(params, "edit")) |e| self.emitWorkspaceEdit(server, e);
            self.respond(server, id, .{ .applied = true });
            return;
        }
        // client/registerCapability, window/workDoneProgress/create, ...
        self.respond(server, id, null);
    }

    fn handleNotification(self: *Lsp, server: *Server, method: []const u8, params: ?Value) void {
        const show = std.mem.eql(u8, method, "window/showMessage");
        if (std.mem.eql(u8, method, "textDocument/publishDiagnostics")) {
            self.applyDiagnostics(params);
        } else if (show or std.mem.eql(u8, method, "window/logMessage")) {
            const t = protocol.getInt(params, "type") orelse 4;
            const m = protocol.getStr(params, "message") orelse return;
            const level: LogLevel = switch (t) {
                1 => .err,
                2 => .warning,
                3 => .info,
                else => .log,
            };
            self.addLog(server.config, level, "{s}", .{m});
            if (show and t <= 2) self.sink.onResult(self.sink.ctx, .{ .message = m });
        } else if (std.mem.eql(u8, method, "$/progress")) {
            self.applyProgress(server, params);
        }
    }

    /// Work-done progress: begin adds an entry, report updates it, end drops it.
    fn applyProgress(self: *Lsp, server: *Server, params: ?Value) void {
        if (server.dead) return;
        var token_buf: [32]u8 = undefined;
        const token: []const u8 = switch (protocol.get(params, "token") orelse return) {
            .string => |s| s,
            .integer => |n| std.fmt.bufPrint(&token_buf, "#{d}", .{n}) catch return,
            else => return,
        };
        const value = protocol.get(params, "value");
        const kind = protocol.getStr(value, "kind") orelse return;
        const gpa = self.gpa;
        const index: ?usize = for (server.progress.items, 0..) |p, i| {
            if (std.mem.eql(u8, p.token, token)) break i;
        } else null;
        if (std.mem.eql(u8, kind, "end")) {
            if (index) |i| freeProgress(gpa, server.progress.orderedRemove(i));
            return self.changed();
        }
        const pct: ?u8 = if (protocol.getInt(value, "percentage")) |n| @intCast(std.math.clamp(n, 0, 100)) else null;
        if (std.mem.eql(u8, kind, "begin")) {
            if (index != null or server.progress.items.len >= 32) return;
            const title = protocol.getStr(value, "title") orelse "";
            const message = protocol.getStr(value, "message") orelse "";
            const p: Progress = .{
                .token = gpa.dupe(u8, token) catch return,
                .title = gpa.dupe(u8, title) catch "",
                .message = gpa.dupe(u8, message) catch "",
                .percentage = pct,
            };
            server.progress.append(gpa, p) catch return freeProgress(gpa, p);
        } else if (index) |i| {
            const p = &server.progress.items[i];
            if (protocol.getStr(value, "message")) |m| {
                const copy = gpa.dupe(u8, m) catch return;
                gpa.free(p.message);
                p.message = copy;
            }
            if (pct != null) p.percentage = pct;
        }
        self.changed();
    }

    fn applyDiagnostics(self: *Lsp, params: ?Value) void {
        const uri = protocol.getStr(params, "uri") orelse return;
        var doc: ?*Doc = null;
        for (self.docs.items) |d| if (std.mem.eql(u8, d.uri, uri)) {
            doc = d;
        };
        const d = doc orelse return;
        const list = protocol.get(params, "diagnostics") orelse return;
        if (list != .array) return;
        var out: std.ArrayList(buffer_mod.Diagnostic) = .empty;
        defer out.deinit(self.gpa);
        for (list.array.items) |item| {
            const range = protocol.getRange(protocol.get(item, "range")) orelse continue;
            const start = protocol.positionToOffset(d.buffer, range.start, d.server.encoding, &d.scratch);
            const end = protocol.positionToOffset(d.buffer, range.end, d.server.encoding, &d.scratch);
            const sev = protocol.getInt(item, "severity") orelse 1;
            out.append(self.gpa, .{
                .start = start,
                .end = @max(start, end),
                .severity = @enumFromInt(@as(u8, @intCast(std.math.clamp(sev, 1, 4)))),
                .message = self.gpa.dupe(u8, protocol.getStr(item, "message") orelse "") catch continue,
                .source = self.gpa.dupe(u8, protocol.getStr(item, "source") orelse "") catch continue,
            }) catch break;
        }
        d.buffer.setDiagnostics(out.items);
        self.sink.onResult(self.sink.ctx, .diagnostics_changed);
    }

    fn handleResponse(self: *Lsp, server: *Server, p: Pending, result: Value) void {
        var arena = std.heap.ArenaAllocator.init(self.gpa);
        defer arena.deinit();
        const alloc = arena.allocator();
        switch (p.kind) {
            .initialize => {
                const caps = protocol.get(result, "capabilities");
                if (protocol.getStr(caps, "positionEncoding")) |enc| server.encoding = if (std.mem.eql(u8, enc, "utf-8")) .utf8 else .utf16;
                if (protocol.get(caps, "textDocumentSync")) |sync| {
                    const kind = if (sync == .integer) sync.integer else protocol.getInt(sync, "change") orelse 1;
                    server.sync = switch (kind) {
                        0 => .none,
                        2 => .incremental,
                        else => .full,
                    };
                }
                server.hover = protocol.getBool(caps, "hoverProvider");
                if (protocol.get(caps, "signatureHelpProvider")) |sh| {
                    server.signature_help = true;
                    for ([_][]const u8{ "triggerCharacters", "retriggerCharacters" }) |key| {
                        if (protocol.get(sh, key)) |tc| if (tc == .array) {
                            for (tc.array.items) |t| if (t == .string and t.string.len == 1 and server.n_sig_trigger < server.sig_trigger_chars.len and !server.isSignatureTrigger(t.string[0])) {
                                server.sig_trigger_chars[server.n_sig_trigger] = t.string[0];
                                server.n_sig_trigger += 1;
                            };
                        };
                    }
                }
                server.definition = protocol.getBool(caps, "definitionProvider");
                server.references = protocol.getBool(caps, "referencesProvider");
                server.rename = protocol.getBool(caps, "renameProvider");
                server.formatting = protocol.getBool(caps, "documentFormattingProvider");
                server.code_action = protocol.getBool(caps, "codeActionProvider");
                server.code_action_resolve = protocol.getBool(protocol.get(caps, "codeActionProvider"), "resolveProvider");
                const comp = protocol.get(caps, "completionProvider");
                server.completion = comp != null;
                if (protocol.get(comp, "triggerCharacters")) |tc| if (tc == .array) {
                    for (tc.array.items) |t| if (t == .string and t.string.len == 1 and server.n_trigger < server.trigger_chars.len) {
                        server.trigger_chars[server.n_trigger] = t.string[0];
                        server.n_trigger += 1;
                    };
                };
                server.initialized = true;
                if (protocol.getStr(protocol.get(result, "serverInfo"), "version")) |v| {
                    // "clangd version 21.1.8 linux x86_64-…": just the number.
                    var words = std.mem.tokenizeAny(u8, v, " ,()");
                    const short = while (words.next()) |w| {
                        if (std.ascii.isDigit(w[0])) break w;
                    } else v;
                    const n = @min(short.len, server.version_buf.len);
                    @memcpy(server.version_buf[0..n], short[0..n]);
                    server.version_len = n;
                }
                const body = std.json.Stringify.valueAlloc(self.gpa, .{ .jsonrpc = "2.0", .method = "initialized", .params = Empty{} }, .{}) catch return;
                server.send(body, true);
                for (server.queued.items) |q| {
                    server.sendRaw(q);
                    self.gpa.free(q);
                }
                server.queued.clearRetainingCapacity();
                log.debug("{s} ready (encoding {t}, sync {t})", .{ server.config.name, server.encoding, server.sync });
                self.addLog(server.config, .info, "{s}{s}{s} ready in {d} ms ({s} positions, {t} sync)", .{
                    server.config.name,
                    if (server.version_len > 0) " " else "",
                    server.version(),
                    platform.nowMs() - server.started_ms,
                    if (server.encoding == .utf8) "UTF-8" else "UTF-16",
                    server.sync,
                });
            },
            .shutdown => {},
            .hover => {
                const contents = protocol.get(result, "contents") orelse return;
                var text: std.ArrayList(u8) = .empty;
                protocol.markupToText(alloc, contents, &text) catch return;
                const trimmed = std.mem.trim(u8, text.items, " \n");
                if (trimmed.len == 0) return;
                self.sink.onResult(self.sink.ctx, .{ .hover = .{ .buffer = p.buffer.?, .offset = p.offset, .text = trimmed } });
            },
            .signature_help => {
                const sig = signature.parse(alloc, result) catch null;
                self.sink.onResult(self.sink.ctx, .{ .signature_help = .{ .buffer = p.buffer.?, .offset = p.offset, .signature = sig } });
            },
            .goto, .references => {
                var locs: std.ArrayList(Location) = .empty;
                collectLocations(alloc, result, server.encoding, &locs);
                for (locs.items) |*l| l.path = self.localPath(alloc, l.path);
                if (p.kind == .goto) {
                    self.sink.onResult(self.sink.ctx, .{ .goto = .{ .what = p.goto, .locations = locs.items } });
                } else {
                    self.sink.onResult(self.sink.ctx, .{ .references = .{ .locations = locs.items } });
                }
            },
            .completion => {
                const buffer = p.buffer orelse return;
                const doc = self.docFor(buffer) orelse return;
                const list = if (result == .array) result else protocol.get(result, "items") orelse return;
                if (list != .array) return;
                var items: std.ArrayList(CompletionItem) = .empty;
                for (list.array.items) |it| {
                    const label = protocol.getStr(it, "label") orelse continue;
                    var item: CompletionItem = .{
                        .label = label,
                        .detail = protocol.getStr(it, "detail") orelse "",
                        .kind = @intCast(std.math.clamp(protocol.getInt(it, "kind") orelse 0, 0, 255)),
                        .insert_text = protocol.getStr(it, "insertText") orelse label,
                        .filter_text = protocol.getStr(it, "filterText") orelse label,
                        .sort_text = protocol.getStr(it, "sortText") orelse label,
                    };
                    if (protocol.get(it, "textEdit")) |te| {
                        // TextEdit or InsertReplaceEdit.
                        const range = protocol.getRange(protocol.get(te, "range")) orelse protocol.getRange(protocol.get(te, "insert"));
                        if (range) |r| if (p.version == buffer.version) {
                            item.edit_start = protocol.positionToOffset(buffer, r.start, server.encoding, &doc.scratch);
                            item.edit_end = protocol.positionToOffset(buffer, r.end, server.encoding, &doc.scratch);
                        };
                        if (protocol.getStr(te, "newText")) |nt| item.insert_text = nt;
                    }
                    items.append(alloc, item) catch break;
                }
                self.sink.onResult(self.sink.ctx, .{ .completion = .{ .buffer = buffer, .offset = p.offset, .version = p.version, .items = items.items } });
            },
            .rename => {
                var files: std.ArrayList(FileEdit) = .empty;
                collectWorkspaceEdit(alloc, result, &files);
                for (files.items) |*f| f.path = self.localPath(alloc, f.path);
                self.sink.onResult(self.sink.ctx, .{ .workspace_edit = .{ .files = files.items, .encoding = server.encoding } });
            },
            .code_action => {
                var actions: std.ArrayList(CodeAction) = .empty;
                if (result == .array) for (result.array.items) |item| {
                    const title = protocol.getStr(item, "title") orelse continue;
                    const json = std.json.Stringify.valueAlloc(alloc, item, .{}) catch continue;
                    actions.append(alloc, .{
                        .title = title,
                        .kind = protocol.getStr(item, "kind") orelse "",
                        .preferred = protocol.getBool(item, "isPreferred"),
                        .disabled = protocol.getStr(protocol.get(item, "disabled"), "reason"),
                        .json = json,
                    }) catch break;
                };
                self.sink.onResult(self.sink.ctx, .{ .code_actions = .{ .buffer = p.buffer.?, .actions = actions.items } });
            },
            .code_action_resolve => self.applyCodeAction(server, result),
            .execute_command => {},
            .formatting => {
                if (result != .array) return;
                var edits: std.ArrayList(TextEdit) = .empty;
                collectTextEdits(alloc, result, &edits);
                self.sink.onResult(self.sink.ctx, .{ .formatting = .{ .buffer = p.buffer.?, .version = p.version, .edits = edits.items, .encoding = server.encoding } });
            },
        }
    }
};

fn collectLocations(alloc: Allocator, v: Value, enc: Encoding, out: *std.ArrayList(Location)) void {
    switch (v) {
        .array => |arr| for (arr.items) |item| collectLocations(alloc, item, enc, out),
        .object => {
            // Location or LocationLink.
            const uri = protocol.getStr(v, "uri") orelse protocol.getStr(v, "targetUri") orelse return;
            const range = protocol.getRange(protocol.get(v, "range")) orelse
                protocol.getRange(protocol.get(v, "targetSelectionRange")) orelse return;
            const path = protocol.uriToPath(alloc, uri) catch return;
            out.append(alloc, .{ .path = path, .range = range, .encoding = enc }) catch {};
        },
        else => {},
    }
}

fn collectTextEdits(alloc: Allocator, v: Value, out: *std.ArrayList(TextEdit)) void {
    if (v != .array) return;
    for (v.array.items) |e| {
        const range = protocol.getRange(protocol.get(e, "range")) orelse continue;
        out.append(alloc, .{ .range = range, .text = protocol.getStr(e, "newText") orelse "" }) catch return;
    }
}

fn collectWorkspaceEdit(alloc: Allocator, v: Value, out: *std.ArrayList(FileEdit)) void {
    if (protocol.get(v, "documentChanges")) |dc| if (dc == .array) {
        for (dc.array.items) |change| {
            const uri = protocol.getStr(protocol.get(change, "textDocument"), "uri") orelse continue;
            var edits: std.ArrayList(TextEdit) = .empty;
            collectTextEdits(alloc, protocol.get(change, "edits") orelse continue, &edits);
            out.append(alloc, .{ .path = protocol.uriToPath(alloc, uri) catch continue, .edits = edits.items }) catch return;
        }
        return;
    };
    if (protocol.get(v, "changes")) |changes| if (changes == .object) {
        var it = changes.object.iterator();
        while (it.next()) |entry| {
            var edits: std.ArrayList(TextEdit) = .empty;
            collectTextEdits(alloc, entry.value_ptr.*, &edits);
            out.append(alloc, .{ .path = protocol.uriToPath(alloc, entry.key_ptr.*) catch continue, .edits = edits.items }) catch return;
        }
    };
}

test "log text is made printable" {
    var buf = "\x1b[31merror\x1b[0m: bad\r\nnext\x07 line \n\n".*;
    try std.testing.expectEqualStrings("error: bad\nnext line", sanitizeLog(&buf));
    var long: [max_log_text + 10]u8 = undefined;
    @memset(&long, 'a');
    // "é" across the cut: the cut moves back to the character's start.
    long[max_log_text - 1] = 0xc3;
    long[max_log_text] = 0xa9;
    try std.testing.expectEqual(max_log_text - 1, sanitizeLog(&long).len);
}

test "configuration tree from dotted settings" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();
    var root: Value = .{ .object = .empty };
    try Lsp.setPath(alloc, &root, "zls.enable_build_on_save", .{ .bool = true });
    var nested: std.json.ObjectMap = .empty;
    try nested.put(alloc, "typeCheckingMode", .{ .string = "strict" });
    try Lsp.setPath(alloc, &root, "python.analysis", .{ .object = nested });
    try std.testing.expect(Lsp.lookupSection(root, "zls.enable_build_on_save").bool);
    try std.testing.expectEqualStrings("strict", Lsp.lookupSection(root, "python.analysis.typeCheckingMode").string);
    try std.testing.expect(Lsp.lookupSection(root, "zls.missing") == .null);
    try std.testing.expect(Lsp.lookupSection(root, null) == .object);
}

test "no server starts while shutting down" {
    // `deinit` drains the executor, and what runs there can open a buffer (a
    // remote host that just connected opens its pending file): a server
    // started then would be freed without being stopped.
    const gpa = std.testing.allocator;
    var executor: Executor = .init(gpa);
    defer executor.deinit();
    const Ui = struct {
        fn onResult(_: *anyopaque, _: Result) void {}
    };
    var ui: u8 = 0;
    var lsp: Lsp = .init(gpa, std.testing.io, &executor, .{ .ctx = &ui, .onResult = Ui.onResult });
    var buffer = try Buffer.initText(gpa, 1, "const x = 1;\n");
    defer buffer.deinit();
    try buffer.setPath("x.zig");
    buffer.language = @import("../syntax/languages.zig").forPath("x.zig");
    const Open = struct {
        lsp: *Lsp,
        buffer: *Buffer,
        /// Servers started, or that failed to start.
        tried: usize = 0,

        fn run(ctx: *anyopaque) void {
            const self: *@This() = @ptrCast(@alignCast(ctx));
            self.lsp.openBuffer(self.buffer);
            self.tried = self.lsp.servers.items.len + self.lsp.failed.count();
        }
    };
    var open: Open = .{ .lsp = &lsp, .buffer = &buffer };
    executor.post(.{ .ctx = &open, .run = Open.run });
    lsp.deinit();
    try std.testing.expectEqual(@as(usize, 0), open.tried);
}
