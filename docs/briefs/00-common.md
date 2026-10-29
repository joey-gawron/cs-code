# Common rules for every module agent

You are implementing ONE module of **CsCode**, a VSCodium-class code editor in C# / .NET 10 (Avalonia UI, LSP, DAP, VS Code
extension compatibility, memory-stable). Your working directory is a **git worktree on its own branch**; other agents are
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
  so (real language server, real debug adapter, real Node, real Open VSX extension archives). Download tools with a script under
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

## Seven agents share ONE machine — don't collide
* **Never** `pkill`/`killall`/kill by process *name* (`node`, `dotnet`, `netcoredbg`, `typescript-language-server`…) — other agents run the same programs. Only kill PIDs **you** started (track them).
* Use **ephemeral ports** (bind port 0), unique temp dirs (`TempDirectory`), and set `CSCODE_HOME` to a temp dir in tests. Never read or write the real `~/.cscode`, `~/.config/cscode`, `~/.vscode*`, or install global dotnet/npm tools: install tools into your worktree's `.scratch/tools/`.
* Heavy commands (full builds, long soaks, big downloads) are fine but be a good neighbour: one build at a time, no busy loops, cap parallel test runners (`-- --maxthreads 4` style), clean up child processes and temp files you create.
* Don't stash, reset, rebase or `git clean` anything outside your own work; don't touch other worktrees (`git worktree list` shows them — leave them alone).

## Final report
End your work with a structured report (the harness asks for it): branch name (`git rev-parse --abbrev-ref HEAD`), head SHA,
status, what MUST/SHOULD/STRETCH items are done / not done, exact test command(s) and counts, Core changes, known gaps and
bugs, and notes the integrator needs (DI registrations, settings keys, commands registered, resources to copy to output).
