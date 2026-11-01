# Continuous integration (`.github/workflows/ci.yml`)

GitHub Actions, on every push and pull request, on **ubuntu-latest** (x64) and **macos-latest** (arm64; audit pkg-16). Nothing in it has run
on GitHub yet (the branch is not pushed); its parts were run locally on a Mac (see the end).

| Job | What | Gating |
|---|---|---|
| `build` (both OSes) | `dotnet restore CsCode.slnx`, `dotnet build CsCode.slnx -warnaserror` | yes |
| `test` (both OSes × 4 groups, in parallel) | builds each group's test projects with `-warnaserror`, then `dotnet test --no-build --filter "$(tools/ci/test-filter.sh)" --blame-hang-timeout 10m --blame-hang-dump-type mini`; trx results and hang dumps are uploaded when a group fails | yes |
| `package-macos` | `tools/package-macos.sh --no-adapters --no-server-bundles`, then `codesign --verify --deep --strict`, `plutil -lint` and `CsCode.app/Contents/MacOS/cscode --version`; the ad-hoc app is uploaded (7 days) | yes |
| `load-sensitive` (both OSes) | only the tests the gating jobs leave out (`tools/ci/test-filter.sh --only`), nightly (03:17 UTC) and on *Run workflow* | no (`continue-on-error`) |

Test groups (every `tests/CsCode.*Tests` project must be in one: `CiWorkflowTests` checks it): **app** (App.Tests), **editor** (Editor,
Vim, Macros, Core), **services** (Dap, Lsp, Platform, Remote, Sessions, Terminal), **panels-memory** (Panels.Debug, Panels.Search,
Panels.Terminal, MemoryTests). NuGet packages are cached per OS, keyed on `global.json`, `Directory.Packages.props`,
`Directory.Build.props` and the project files. Runs of one branch cancel the older one.

## What the gating runs leave out

* `Category=Soak` and `Category=Perf`: the soak suites and benchmarks (long, measured by hand; docs/MEMORY.md).
* The **load-sensitive tests** in `tools/ci/load-sensitive-tests.txt`: timing budgets and environment checks that fail on a loaded or shared
  machine and pass alone, each named in docs/handoff/known-issues.md. One fully qualified name (or a class) per line; `CiWorkflowTests` fails
  when an entry names no test, so a rename cannot silently put it back into the gating runs. Remove a line once its test is fixed; add one
  only with a known-issues entry.
* Locally: `dotnet test tests/CsCode.App.Tests --filter "$(tools/ci/test-filter.sh)"` is the gating selection, `--filter
  "$(tools/ci/test-filter.sh --only)"` the excluded one.

Tests that need tools the runners lack skip with their reason: netcoredbg, CodeLLDB, js-debug (`tools/fetch-*.sh` are not run), gdb, nvim,
sshd and Docker (remote), a real X server. `RealLldbDapTests` runs its handshake on macos-latest (Xcode's lldb-dap) and skips the C session
there unless Developer Mode is on.

## Checked here before the first push (2026-10-31, on the arm64 Mac)

* The workflow parses (Ruby's and Python's YAML loaders) and every job's commands were run as written: the package job's script end to end
  (`tools/package-macos.sh --no-adapters --no-server-bundles`, verify, `--version`), the filter against the Dap tests (`--only` selects the
  one listed Dap test), the build with `-warnaserror`.
* Not checked: the runners themselves (their preinstalled SDKs, fonts, python3), the cache, artifact upload, and how long each group takes;
  look at the first run's timings and move projects between groups if one dominates.
