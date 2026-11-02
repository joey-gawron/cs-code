# Stage brief — 6. Zig

**User request (2026-11-01):** "Lets break this up into stages/languages: … 6. Zig" (overview: `docs/briefs/languages.md`). Starts after
stages 1 and 2 are merged: snippets, `ITaskProvider` and the native debugging windows.

**Owns:** `src/CsCode.Dap/**` (`zig` added to the native adapters' languages, Zig launch templates, loading Zig's LLDB pretty printers,
the `$zig` problem matcher, a `zig build` task provider), new `src/CsCode.Testing/Providers/Zig/**`, `src/CsCode.Lsp/**` (zls settings
and the version check), shell wiring in `src/CsCode.App/**`, tests next to each, `tools/fetch-zig.sh`, `docs/ZIG.md`, and the regenerated
`docs/LANGUAGES.md`.

## What is already there (verify, don't rebuild)
* zls is the built-in server (root markers `build.zig` and `zls.json`; formats with zig fmt). Zig is coloured by tree-sitter `zig` by default,
  with CsCode's own language configuration and folding by syntax tree. `RealZlsAndNilServerTests` covers initialize, diagnostics, hover and
  formatting (run on Linux).
* **Missing:** no debugger lists `zig` (CodeLLDB and lldb-dap list `rust`, `c` and `cpp`). There is no Zig test provider, no problem matcher,
  no snippets and no tasks.
* From stage 2: the Disassembly, Memory and Registers windows, data breakpoints and `ITaskProvider`. From stage 1: bundled snippets.

## Toolchain on this Mac (checked 2026-11-01)
**zig and zls are not installed.** Ask the user before installing, with these options:
* `brew install zig zls`;
* or pinned tarballs from ziglang.org and the zls releases into `.scratch/tools/`, digests checked.

zls must match zig's version. Apple LLDB, lldb-dap and CodeLLDB are there.

## MUST
1. **Debugging Zig through CodeLLDB (the default on macOS) and lldb-dap.**
   * Add `zig` to both adapters' breakpoint languages.
   * **Launch templates:** a `zig build` pre-launch task with the program picked from `zig-out/bin`, and a test binary emitted by `zig test`
     (check the flags for the installed version).
   * **Pretty printers:** load Zig's LLDB pretty printers through `initCommands` when they are found. Check where they live and whether the
     binary distribution or Homebrew ships them.
   * **Matrix in `docs/ZIG.md`,** with evidence: slices, optionals, error unions, tagged unions, generic functions in the call stack,
     breaking on panic, threads, and the Memory and Disassembly windows.
2. **Test Explorer provider.**
   * **Locations:** `test "name" { … }` and `test ident { … }` blocks, found with the bundled tree-sitter `zig` grammar.
   * **Runs:** per file, `zig test <file> --test-filter "<name>"`; for the whole project, `zig build test`.
   * **Results:** check the current output format and whether the build runner's machine-readable mode gives better results.
   * **Debugging** a test launches its emitted binary.
3. **Tasks.**
   * A `$zig` problem matcher (`file:line:col: error|note: message`).
   * A Zig task provider: `zig build`, `zig build run`, `zig build test`, and the project's own steps from `zig build -l`.
4. **zls.**
   * Detect a zig / zls version mismatch and show one clear message saying which versions were found.
   * Turn on build-on-save diagnostics when the project allows it (check zls's settings for the installed version).
   * Run the real-server test on this Mac.
5. **Zig snippets** in the bundled-snippets mechanism.

## SHOULD
`build.zig.zon` editing (what zls offers for it, and its highlighting); check that zls's inlay hints display.

## Threading & isolation (binding)
00-common applies. `zig build -l` and test listing run zig, which can be slow on a cold cache. Bound them, run them on the IO lane, and
cache the results until `build.zig` changes.

## Done when
In the real app on this Mac, a `zig init` project:
* builds through Run Task, with errors in Problems;
* lists, runs and debugs its tests in the Test Explorer;
* debugs the program under CodeLLDB with readable slices and optionals.

Also: zls reports a version mismatch clearly. `docs/ZIG.md` has the matrix. The real-tool suites pass or skip with their reasons. Committed
without attribution trailers.
