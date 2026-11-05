{
  description = "cs-zig tree-sitter grammar farm: <lang>/{parser,queries/} for every language languages.zig maps";

  # Same nixpkgs as the editor's own flake.lock, so parsers and queries match
  # what jiffy-edit highlights with.
  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";

  outputs =
    { nixpkgs, ... }:
    let
      systems = [
        "aarch64-darwin"
        "x86_64-darwin"
        "aarch64-linux"
        "x86_64-linux"
      ];
      forAllSystems = f: nixpkgs.lib.genAttrs systems (system: f nixpkgs.legacyPackages.${system});
      grammarsFor =
        pkgs:
        let
          inherit (pkgs) lib;
        # Tree-sitter grammars the editor loads at runtime (like Zed's grammar
        # extensions). Laid out as $JIFFY_EDIT_GRAMMARS/<lang>/{parser,queries/}.
        # These are nixpkgs' grammar packages, which ship the grammar's own
        # highlights query.
        grammarNames = [
          "bash"
          "c"
          "cpp"
          "css"
          "cython"
          "dune"
          "elisp"
          "go"
          "html"
          "javascript"
          "json"
          "less"
          "lua"
          "make"
          "markdown"
          "nix"
          "org"
          "python"
          "rust"
          "sml"
          "toml"
          "tsx"
          "yaml"
          "zig"
        ];
        # Most languages come from nvim-treesitter instead: its parsers are built
        # at the revisions its highlight queries are written for (languages.zig
        # gives these `query_last_wins`).
        nvimGrammarNames = [
          "ada"
          "angular"
          "arduino"
          "asm"
          "astro"
          "awk"
          "beancount"
          "bibtex"
          "bicep"
          "blade"
          "c_sharp"
          "capnp"
          "clojure"
          "cmake"
          "commonlisp"
          "csv"
          "cuda"
          "cue"
          "d"
          "dart"
          "desktop"
          "devicetree"
          "dhall"
          "diff"
          "dockerfile"
          "dot"
          "dtd"
          "earthfile"
          "editorconfig"
          "eex"
          "elixir"
          "elm"
          "embedded_template"
          "erlang"
          "fennel"
          "fish"
          "fortran"
          "fsharp"
          "gdscript"
          "git_config"
          "git_rebase"
          "gitattributes"
          "gitcommit"
          "gitignore"
          "gleam"
          "glimmer"
          "glsl"
          "gn"
          "gomod"
          "gosum"
          "gotmpl"
          "gowork"
          "graphql"
          "groovy"
          "hare"
          "haskell"
          "hcl"
          "heex"
          "hlsl"
          "htmldjango"
          "http"
          "hyprlang"
          "ini"
          "janet_simple"
          "java"
          "jinja"
          "jinja_inline"
          "json5"
          "jsonnet"
          "julia"
          "just"
          "kconfig"
          "kdl"
          "kitty"
          "kotlin"
          "latex"
          "linkerscript"
          "liquid"
          "llvm"
          "luau"
          "meson"
          "nasm"
          "nginx"
          "nickel"
          "nim"
          "ninja"
          "nu"
          "objc"
          "ocaml"
          "ocaml_interface"
          "odin"
          "pascal"
          "perl"
          "php"
          "php_only"
          "pkl"
          "po"
          "powershell"
          "prisma"
          "properties"
          "proto"
          "pug"
          "purescript"
          "query"
          "r"
          "racket"
          "razor"
          "readline"
          "rego"
          "requirements"
          "rescript"
          "ron"
          "rst"
          "ruby"
          "scala"
          "scheme"
          "scss"
          "solidity"
          "sql"
          "ssh_config"
          "starlark"
          "svelte"
          "swift"
          "systemverilog"
          "tablegen"
          "tcl"
          "teal"
          "templ"
          "terraform"
          "textproto"
          "thrift"
          "tsv"
          "twig"
          "typst"
          "udev"
          "vala"
          "vhdl"
          "vim"
          "vue"
          "wgsl"
          "xml"
          "xresources"
        ];
        nvim-treesitter = pkgs.vimPlugins.nvim-treesitter;
        # All of a language's nvim-treesitter queries: highlights.scm,
        # injections.scm (code embedded in other code), ...
        nvimQueries = dir: name: {
          name = "${dir}/queries";
          path = "${nvim-treesitter}/runtime/queries/${name}";
        };
        tsGrammar = name: pkgs.tree-sitter-grammars."tree-sitter-${name}";
        grammarDir = dir: parser: query: [
          {
            name = "${dir}/parser";
            path = parser;
          }
          {
            name = "${dir}/queries/highlights.scm";
            path = query;
          }
        ];
        queryDir = dir: query: {
          name = "${dir}/queries/highlights.scm";
          path = query;
        };
        grammars = pkgs.linkFarm "cs-zig-grammars" (
          map (name: {
            inherit name;
            path = tsGrammar name;
          }) grammarNames
          ++ lib.concatMap (name: [
            {
              name = "${name}/parser";
              path = "${nvim-treesitter.builtGrammars.${name}}/parser";
            }
            (nvimQueries name name)
          ]) nvimGrammarNames
          # Queries other languages inherit; nvim-treesitter's versions of
          # languages we take from nixpkgs go under nvim/.
          ++ [ (nvimQueries "html_tags" "html_tags") ]
          ++ map (name: nvimQueries "nvim/${name}" name) [
            "c"
            "cpp"
            "css"
            "go"
            "html"
          ]
          # Markdown's inline markup, a grammar of its own that Markdown injects.
          ++ [
            {
              name = "markdown_inline";
              path = pkgs.tree-sitter-grammars.tree-sitter-markdown-inline;
            }
          ]
          # Queries nixpkgs leaves out of the package, or files elsewhere.
          ++ grammarDir "typescript" "${tsGrammar "typescript"}/parser" "${(tsGrammar "typescript").src}/queries/highlights.scm"
          ++ grammarDir "crystal" "${tsGrammar "crystal"}/parser" "${tsGrammar "crystal"}/queries/nvim/highlights.scm"
          # JavaScript's JSX and parameter queries, listed next to its highlights.scm.
          ++ map (part: queryDir "javascript_${part}" "${tsGrammar "javascript"}/queries/highlights-${part}.scm") [
            "jsx"
            "params"
          ]
        );

        in
        grammars;
    in
    {
      packages = forAllSystems (pkgs: {
        default = grammarsFor pkgs;
      });
    };
}
