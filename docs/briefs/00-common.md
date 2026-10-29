# Common rules for every module agent

You are implementing ONE module of **CsCode**, a VSCodium-class code editor in C# / .NET 10 (Avalonia UI, LSP, DAP, Vim emulation,
remote editing, memory-stable; there is deliberately NO extension system). Your working directory is a **git worktree on its own branch**; other agents are
building the other modules at the same time in their own worktrees. Your work is merged later, so ownership boundaries matter.

## Read first (in this order)
1. `docs/ARCHITECTURE.md` — decisions, module map, memory rules, repo rules. Treat it as binding.
2. Every file under `src/CsCode.Core/` — the contracts you implement and/or consume. Read them completely; they are small.
3. `tests/CsCode.TestDoubles/` — shared fakes (`TestCommandService`, `TestConfigurationService`, `RecordingWindowService`,
   `InMemoryOutputService`, `RecordingNavigationService`, `TestContextKeyService`, `TestAppPaths`, `TempDirectory`) and
   `LeakAssert`. Use them in your tests instead of inventing new fakes; you may ADD to that project (new files only).
4. Your module brief (`docs/briefs/<module>.md`).

## Non-negotiable rules
* **Ownership.** Edit only the directories your brief lists. Never edit another module's files. The `Directory.Packages.props`
  rule: add `<PackageVersion>` lines only inside your module's marked section. If you need a Core change, make it
  **additive** (new files / new members only — never rename, remove or change signatures) under `src/CsCode.Core/<Area>/`,
  keep it minimal, and list it in your report. Do not "fix" other modules.
* **Contracts first.** Implement the Core interfaces named in your brief exactly; register them in your
  `ServiceCollectionExtensions` (`AddCsCode<Module>`). Cross-module interaction happens ONLY through Core interfaces from DI.
* **Build clean, tests green.** `dotnet build CsCode.slnx` must be warning-free (TreatWarningsAsErrors is on). Run your own test
  project(s) until green. If `dotnet` comes from Nix, prefix every shell command that builds/tests/runs with
  `source tools/nix-dev-env.sh && ` (shell state does not persist between Bash calls). Don't weaken analyzers or suppress
  warnings to get green; fix the cause (a narrowly-scoped, commented `#pragma`/`NoWarn` is acceptable only for third-party quirks).
* **Memory-stability rules** (ARCHITECTURE §8) apply to everything you write: every `Subscribe`/`Register` result lives in a
  `DisposableStore`; everything per-document/session/server is disposed when its owner goes away; caches are bounded and (if large)
  `IMemoryTrimmable`; no unbounded logs/queues; no allocation in hot paths. Every lifecycle-bearing type you add needs a
  `LeakAssert.NotRetained` test proving that, after dispose/close, nothing is retained.
* **Real beats mock.** Besides fast unit tests with fakes, write integration tests against the REAL thing wherever the brief says
  so (real language server, real debug adapter, real `vim`/`nvim`, real `ssh`). Download tools with a script under
  `tools/` into `.scratch/tools/` (git-ignored). Never commit binaries, `node_modules`, downloads or screenshots. A test needing a
  tool/network must **skip with an explicit reason** (not fail) when the tool is unavailable — use an `Assert.Skip`/skip helper.
* **Commits.** Commit early and often on your current branch: `git add -A && git commit -m "<imperative subject>"`. One logical
  step per commit, so a partial result is still mergeable. **NEVER add `Co-Authored-By` trailers or any AI/Claude/"Generated with"
  attribution to commit messages.** Never push, never change branches, never rewrite history, never touch `main`.
* **Honesty.** Do not claim anything works that you did not run. If a feature is partial, stubbed or broken, say exactly that in
  your report and in the module docs. "Tests pass" means you ran them and saw them pass.
* **Priorities.** Finish **MUST** items completely (working, tested, committed) before **SHOULD**, then **STRETCH**. Prefer a
  smaller feature set that is solid over a wide one that is flaky.
* **Style.** Match the surrounding code (file-scoped namespaces, `_camelCase` private fields, nullable enabled, immutable records
  for models, `ValueTask` for hot async paths, `CancellationToken` on every async API). Comments explain *why*, not *what*.
* **Docs.** Each module writes a short `docs/<MODULE>.md` (design, public surface, limits, how to run the integration tests).

## Threading rules (binding — ARCHITECTURE §6)
The user's explicit requirement: **editing and the GUI stay on their own threads, isolated from the language servers**, which can burn a lot of CPU/memory.
* **The UI thread is sacred.** It does input, layout, rendering coordination and the (unavoidable) AvaloniaEdit buffer mutations — nothing else. On it: **no blocking** (`.Result`, `.Wait()`, `.GetAwaiter().GetResult()`, `Thread.Sleep`, sync file/network I/O), no JSON (de)serialisation, no LSP/DAP result mapping, no tokenisation, no search/regex over document text, no sorting/filtering of > 1,000 items, no CPU work > 4 ms per dispatcher callback (chunk it). UI-facing projects enable the banned-API rule for the blocking calls above.
* **The typing path never waits for anything**: keystroke → buffer mutation → return. Completion/hover/diagnostics/etc. are downstream, asynchronous, cancellable, and may arrive late or never without affecting typing.
* **Non-UI projects** (Platform, Lsp, Dap, Vim, Profiling, Remote…) never reference Avalonia and never assume a UI thread; use `ConfigureAwait(false)` in library code; hop to your own dedicated lane before doing any non-trivial work, so a caller on the UI thread pays only the cost of enqueueing (< 0.1 ms).
* **Producers → UI traffic is one-way, coalesced and bounded**: latest-wins per key (document+feature), tagged with the document version and dropped when stale, delivered in small immutable batches; never unbounded lists (page/cap/virtualise).
* **Lanes** (dedicated threads, not the shared thread pool, so a CPU-hungry language server can't starve anything): UI (Avalonia dispatcher) · Editor worker (derived per-document work from immutable snapshots) · Language (LSP client work, BelowNormal priority) · IO (file system, watchers, search) · Debug (DAP client). Child processes (language servers, debug adapters) start at **below-normal OS priority** (setting `cscode.lsp.processPriority`).
* **Prove it**: every module that does background work includes a "does not block the caller" test (assert the synchronous portion of the call on the caller thread), and the integration phase runs a *hostile-server responsiveness suite* (CPU-burning, slow, flooding, hanging, crashing servers) while simulated typing runs and asserts dispatcher latency and typing latency budgets (see `docs/briefs/profiling.md`).

## Seven agents share ONE machine — don't collide
* **Never** `pkill`/`killall`/kill by process *name* (`node`, `dotnet`, `netcoredbg`, `typescript-language-server`…) — other agents run the same programs. Only kill PIDs **you** started (track them).
* Use **ephemeral ports** (bind port 0), unique temp dirs (`TempDirectory`), and set `CSCODE_HOME` to a temp dir in tests. Never read or write the real `~/.cscode`, `~/.config/cscode`, `~/.vscode*`, or install global dotnet/npm tools: install tools into your worktree's `.scratch/tools/`.
* Heavy commands (full builds, long soaks, big downloads) are fine but be a good neighbour: one build at a time, no busy loops, cap parallel test runners (`-- --maxthreads 4` style), clean up child processes and temp files you create.
* **Never read, copy or use anything under `~/.ssh`** (it holds the user's real private keys), the user's ssh-agent, or the real `known_hosts`. Tests that need SSH generate throwaway keys/config in a temp dir.
* Don't stash, reset, rebase or `git clean` anything outside your own work; don't touch other worktrees (`git worktree list` shows them — leave them alone).

## Final report
End your work with a structured report (the harness asks for it): branch name (`git rev-parse --abbrev-ref HEAD`), head SHA,
status, what MUST/SHOULD/STRETCH items are done / not done, exact test command(s) and counts, Core changes, known gaps and
bugs, and notes the integrator needs (DI registrations, settings keys, commands registered, resources to copy to output).
