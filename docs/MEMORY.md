# Memory and responsiveness (`CsCode.Profiling` + `tests/CsCode.MemoryTests` + `tools/profile`)

"A well profiled, memory-stable app" in numbers: a memory monitor that keeps memory flat and reacts to pressure, a harness that proves it (leak,
soak, responsiveness), GC settings chosen by experiment, and a playbook for when something grows. Every number below was **measured on this
machine** (Linux x64, Debian 14, kernel 7.2, 22 cores, 62 GB, .NET 10.0.12 runtime from the Nix SDK 10.0.401) unless it says otherwise, while
eight other agents built and tested on the same machine (load average 10–36): CPU times and pauses carry that noise, resident sizes much less.

```
 CsCode.Profiling (Core only, no Avalonia)                          tests/CsCode.MemoryTests                tools/profile
   MemoryMonitor : IMemoryMonitor, IUserActivityMonitor               Monitor/   monitor + policy tests        CsCode.ProfileHost (cscode-profile):
     ProcessSampler (allocation-free /proc + GC counters)             Toolkit/   toolkit tests                   budget | app | synthetic | soak |
     MemoryPressurePolicy (pure: 70/90 %, hysteresis, growth)         Soak/      Core-level soaks                trim-effect | bench
     OsMemory (compacting GC, malloc_trim / EmptyWorkingSet)          App/       real-app host + scenarios     budget.sh  gc-matrix.sh  soak.sh
     MemoryMetrics (Meter "CsCode")  MemoryLog ("Memory" channel)                (linked into the tool)        gcdump.sh  counters.sh  trace.sh
     MemoryBreakdown (smaps: managed vs native)                       Responsiveness/ hostile matrix           bench.sh   publish-host.sh
   CsCode.Profiling.Testing: LeakProbe, HeapCensus, ResourceCensus, SubscriptionAudit, SoakRunner/IScenario, ProcessTree,
     ResponsivenessProbe, TypingLatencyProbe, ThreadBlockedDetector, LatencyHistogram, GcPauseRecorder, Load/ (CpuBurner, SlowResponder,
     Flooder, GiantPayload, Hanger, CrashLooper)
```

Composition: `AppServices.Build` already calls `services.AddCsCodeProfiling()`; the shell starts it (`ShellController.Initialize` →
`ProfilingShell.Attach`: `StartCsCodeProfiling()`, input handlers for the idle clock). The shell's `ShellMemoryMonitor` wraps the registered
monitor for its exit path (`ChildProcessTracker`), so every module keeps resolving `IMemoryMonitor` as before.

## 1. The memory monitor (MUST 1)

`MemoryMonitor` samples on a dedicated below-normal thread (`cscode-memory`): every **2 s**, every **10 s** once the user has been idle for 30 s
(input wakes it at once). A sample reads `/proc/self/status` (VmRSS, VmHWM, RssAnon, RssFile, RssShmem, VmSwap, Threads) through a handle opened
once and re-read with `pread` into a reused buffer, and the runtime's counters (`GC.GetTotalMemory(false)`, `GC.CollectionCount`,
`GC.GetTotalAllocatedBytes`, `GC.GetTotalPauseDuration`); `GC.GetGCMemoryInfo` (committed, fragmented, LOH, POH, pause %) allocates a small object
per call, so it is re-read only after a collection happened. Tracked child processes are read the same way, each through its own
`/proc/<pid>/status` handle — a handle stays bound to the process it was opened for, so an exited (or reused) pid reads as gone, never as someone
else. Elsewhere (Windows, macOS) a cached `Process` is refreshed (allocates; compiled, not measured).

* **Cost, measured** (`The_sampler_allocates_nothing_and_a_monitor_sample_only_its_published_snapshot`): the raw sampler allocates **0 bytes** per
  sample (< 2 KB over 1,000 samples, i.e. the occasional `GetGCMemoryInfo` after a GC); a full monitor sample with one tracked child allocates only
  the published `MemorySnapshot` (≤ 160 bytes/sample asserted; the record is ~120 bytes).
* **Published**: `Current`, `Sampled` (the status bar's memory item already subscribes), `CurrentDetails` (the Linux breakdown and GC details),
  `Children`, `LastTrim`, `Trimmed`.
* **dotnet-counters**: the `CsCode` meter (`System.Diagnostics.Metrics`) exposes `cscode.memory.working_set`, `.private`, `.file_backed`,
  `.gc_heap`, `.gc_committed`, `.gc_fragmented`, `.loh`, `.children`, `.budget`, `cscode.threads`, `cscode.gc.allocated` (its rate is the allocation
  rate), `cscode.gc.pause_time`, `cscode.gc.gen0/1/2_collections`, `cscode.gc.pause_time_percentage`, and the counters/histogram
  `cscode.memory.trims`, `.trim_released`, `.trim_duration`. Observable instruments read the last sample: no listener, no cost.

  ```
  tools/profile/counters.sh PID              # = dotnet-counters monitor -p PID --counters CsCode,System.Runtime
  tools/profile/counters.sh PID --csv 60     # 60 s to .scratch/profile/counters-PID-*.csv
  ```

## 2. Pressure policy, trims, idle trim (MUST 2)

| Setting | Default | Meaning |
|---|---|---|
| `cscode.memory.budgetMb` | 0 (unlimited) | Soft budget for the app's resident set (children have their own caps). |
| `cscode.memory.trimOnIdle` | true | Idle trim once per idle period. |
| `cscode.memory.idleTrimSeconds` (Profiling) | 120 | Seconds without input before the idle trim (minimum 10). |
| `cscode.memory.logIntervalSeconds` (Profiling) | 60 | Summary line period of the **Memory** output channel (0 = trims only). |

`MemoryPressurePolicy` is a pure state machine over (time, used bytes, budget), tested on a manual clock:

* below **70 %** of the budget nothing; at **70 %** a **Moderate** trim; at **90 %**, or with **sustained growth** above 70 % (linear fit over the
  last 2 min: ≥ 2 % of the budget per minute with R² ≥ 0.8, at least 10 samples spanning half the window), an **Aggressive** trim;
* **hysteresis**: a level re-arms only after usage fell 5 % below its threshold; while it stays above, the trim repeats at most every 5 min;
* **minimum interval** 30 s between policy trims (10 s to escalate to a higher level) — no trim storms; with no budget it never trims but logs
  sustained growth (32 MB/min) once per window.

**A trim** (policy, idle or `TrimNowAsync`) runs on a background thread, one at a time (a policy trim skips while another runs): every registered
`IMemoryTrimmable` in turn, each timed and isolated (a throwing trimmable is reported, the others and the collection still run), then the
collection and the page release, measured before/after (`MemoryTrimReport`, logged, in the Memory channel, `Trimmed` event, metrics):

* `TrimNowAsync(level)` and Idle trims: `GCSettings.LargeObjectHeapCompactionMode = CompactOnce`, `GC.Collect(2, Forced, blocking, compacting)`
  (Aggressive: `GCCollectionMode.Aggressive`, finalizers, a second pass), then `malloc_trim(0)` (glibc; skipped on musl, probed once),
  `SetProcessWorkingSetSize(-1, -1)` on Windows, nothing on macOS.
* **Policy Moderate trims** fire while the user may be typing, so they start a **background** gen2 collection instead (no compaction) and then
  `malloc_trim`. Measured (`A_policy_moderate_trim_collects_in_the_background_while_an_aggressive_one_compacts`, 600k live objects): the background
  trim's longest pause is under half the compacting trim's.
* **Child processes**: `TrackChildProcess(name, pid, restartAboveBytes, onOverBudget)` — a child above its cap for 2 consecutive samples (1 under
  Aggressive pressure) gets its owner's callback, at most once a minute, exceptions isolated.
* **Idle trim**: after `idleTrimSeconds` without input (`IUserActivityMonitor.NotifyActivity`, called by the shell's tunnel handlers for keys,
  text, pointer press/move/wheel — one timestamp write per event), an Idle-level trim with a compacting collection, once per idle period.

**Measured effect in the real app** (`cscode-profile trim-effect`, headless, 20 documents open after 3 open/close cycles of 21 files, live GC heap
~60 MB; load ~10–15):

| Trim | Working set before → after | GC heap before → after | Trimmables | Collect + release |
|---|---|---|---|---|
| Idle | 295.2 → 284.1 MB (−11.1) | 63.2 → 60.3 MB | 0 KB (4 caches, 2.6 ms) | 108 ms |
| Moderate (`TrimNowAsync`, compacting) | 296.7 → 292.8 MB (−3.9) | 61.3 → 60.4 MB | 0 KB | 64 ms |
| Aggressive | 298.3 → 271.9 MB (**−26.4**) | 61.5 → 60.4 MB | 1 KB | 120 ms |
| `malloc_trim` alone, right after each | −4.9 / −3.9 / −3.9 MB | | | |

What this says: the collection's pause is ~60–120 ms with this heap (hence idle-only compacting); `GCCollectionMode.Aggressive` is what gives
memory back (decommitting the GC's free regions); the registered trimmables (editor syntax tokens, file index, remote directory cache, icon
geometries) had **nothing** to release in this state — the 19 background documents had no live views (the document well keeps few views alive),
so their token state was already gone. `malloc_trim` on its own returns 4–5 MB of native heap; in a synthetic test (200k freed 1 KB native blocks,
`Malloc_trim_returns_freed_native_heap_pages`) it returned > 100 MB that glibc would otherwise keep.

## 3. GC and runtime configuration (MUST 3)

<!--GCMATRIX-->

## 4. Leak-detection toolkit (MUST 4, `CsCode.Profiling.Testing`)

Usable from any test project (reference `CsCode.Profiling`); `tests/CsCode.TestDoubles/LeakAssert` stays as it is.

| Helper | What it does |
|---|---|
| `LeakProbe<T>` | Tracks instances weakly (`WeakReference` + `ConditionalWeakTable` de-dup); `AssertAliveAtMost(n)` after full GCs, naming the survivors. |
| `HeapCensus.Run/RunAsync(name, scenario, runs, warmup)` | Live heap (full GC) after each run past a warm-up; least-squares slope with a 95 % interval; `AssertFlat(maxBytesPerRun)` fails only when the whole interval lies above the limit (noise passes). |
| `ResourceCensus` | Threads (`/proc/self/task`), descriptors (`/proc/self/fd`), handles, child processes; `AssertReturnsToBaselineAsync` polls (threads and fds go asynchronously). |
| `SubscriptionAudit` / `IEmitterProbe` | Finds every private `Emitter<T>` field of a service by reflection (no module changes) and asserts subscriber counts return to baseline. |
| `SoakRunner.RunAsync(IScenario, SoakOptions)` | Warm-up, then a time series (working set, private, live heap, committed, gen0/1/2, threads, fds, allocated, children) sampled between iterations; `SoakReport`: slope per iteration and per hour with 95 % intervals, peaks by thirds (plateau), gen2 rate first vs second half; CSV + JSON under `.scratch/soak/`; `AssertStable(SoakThresholds)`. Samples are value types in a pre-sized list so the series itself is not measured as growth. |
| `ProcessTree.Descendants(pid)` | The child process tree with resident sets and readable labels (node renames its thread to "MainThread"). |
| `LinearRegression`, `FullGc`, `MemoryAssertionException` | The statistics and the forced collection everything above uses. |

## 5. Soak / regression suite (MUST 5)

`tests/CsCode.MemoryTests` (xUnit v3, trait `Category=Soak` on the soak classes). The default `dotnet test` runs the **fast** variants (seconds to
a minute each); `CSCODE_SOAK=1 CSCODE_SOAK_MINUTES=N` runs the long variants (`tools/profile/soak.sh --tests --minutes N`).

* **Core level** (`Soak/CoreSoakTests`): document open/edit/close churn through `DocumentManager` + `InMemoryTextDocument` (with a subscription
  audit), language-feature registry register/query/unregister, diagnostics set/clear over 20 files, `Emitter` subscribe/fire/unsubscribe ×100,
  contribution registry churn, `MemoryMonitor` register/sample/Idle trim, child-process tracking with a real child per iteration, and the app's
  output-channel ring (1M lines into one channel stays within its 2 MB cap). Thresholds: live heap ≤ 64 B/iteration (95 % interval), threads and
  descriptors back within 4, gen2 rate not more than 3× higher in the second half; long variants also ≤ 1 MB/h.
* **App level** (`App/AppSoakTests`, the REAL app headless through `MemoryAppHost`): open/close 20 documents, scroll a 20k-line file, type into a
  document and close it unsaved, switch themes (through the setting), show/hide tool windows, start/stop a language server (a minimal python stdio
  server: the client's cost), create/kill terminals (sandboxed bash), js-debug sessions (node, stop at a breakpoint, Shift+F5), remote sessions
  through the loopback transport (a real `cscode-server` child). Asserted: live heap per iteration (fast: ≤ 64 KB with the 95 % rule; long: ≤ 4 KB
  and ≤ 4 MB/h), threads/fds back near baseline, **no child process left** after each scenario. Two more tests check the real composition: the shell
  starts the monitor and wraps it, trims reach the shell's trimmables, and typing resets the idle clock.
* `IScenario` is the extension point: an integrator adds scenarios (LSP with a real server, longer debug sessions, remote reconnects) without
  touching the runner; `MemoryAppHost` + `AppScenarios.cs` are linked into the profiling host too.

<!--FASTSOAK-->

### The real ≥ 10-minute soak

<!--SOAK-->

## 6. Benchmarks of Core's hot paths (MUST 6)

<!--BENCH-->

## 7. Memory budget of the real app (MUST 7)

<!--BUDGET-->

## 8. Profiling playbook (MUST 7)

All scripts run from anywhere, set up the Nix runtime libraries, never open a display and never touch your config (the profiling host uses a temp
`CSCODE_HOME`). Diagnostics tools are fetched on first use by `tools/profile/fetch-dotnet-tools.sh`: dotnet-counters, dotnet-gcdump, dotnet-trace,
dotnet-dump **10.0.750501** from nuget.org, SHA-512 checked against nuget.org's published package hash, each into
`{repo}/.scratch/downloads/<tool>/10.0.750501/` (shared by every worktree; nothing global, no install script).

| Task | Command |
|---|---|
| Live counters (app meter + runtime) | `tools/profile/counters.sh PID` |
| Managed heap snapshot (one full GC) | `tools/profile/gcdump.sh PID [name]` → `.gcdump` + top types `.txt` |
| What grew between two moments | take two gcdumps, `tools/profile/gcdump.sh --compare a.gcdump b.gcdump` (types by size growth) |
| Allocations / GC events | `tools/profile/trace.sh PID 20 gc-verbose` (or `cpu-sampling`, `gc-collect`) → `.nettrace` (PerfView, VS, or `dotnet-trace convert --format Speedscope`) |
| Full dump analysis | `dotnet-dump collect -p PID` then `dotnet-dump analyze FILE`: `dumpheap -stat`, `dumpheap -mt <MT>`, `gcroot <addr>` (who keeps it alive), `eeheap -gc`, `finalizequeue` |
| Native memory (Skia, ICU, HarfBuzz) | the monitor's `MemoryBreakdown` (smaps grouping, managed vs native estimate); `heaptrack dotnet cscode.dll` (not installed here) or `perf record -g -p PID` + `perf report` for allocation hot spots; `pmap -X PID` |
| GC internals | `DOTNET_gcTrace=1` (checked runtimes only), else `DOTNET_GCgen0size` / `GCgen0MaxBudget` experiments through `gc-matrix.sh`, and `GC.GetConfigurationVariables()` (the matrix records it) |
| Soak a build | `tools/profile/soak.sh --minutes 30` (real app), `tools/profile/soak.sh --tests --minutes 10` (suite) |
| Budget table | `tools/profile/budget.sh` |
| Benchmarks | `tools/profile/bench.sh` (Release; `--filter '*Glob*'`) |

Recipe for a suspected leak: reproduce with an `IScenario` (or the app's own steps), run it under `SoakRunner` to confirm a slope whose interval
excludes zero, take a gcdump after the warm-up and another after N more iterations, compare (`gcdump.sh --compare`), then `dotnet-dump` +
`gcroot` on one instance of the top growing type to find the root (usually an `Emitter` subscription or a static cache), fix, and keep the
scenario as a test with `HeapCensus`/`SoakReport.AssertStable`. PIDs: only processes you started (the profiling host, or `cscode --foreground`
started by you) — never another user's or agent's process.

## 9. `AddCsCodeProfiling` (MUST 8)

`services.AddCsCodeProfiling()` registers `MemoryMonitor` (singleton) as `IMemoryMonitor` and `IUserActivityMonitor`; nothing starts and no I/O
happens until the host calls `serviceProvider.StartCsCodeProfiling()` (the shell does, from `ShellController.Initialize`), which starts the
sampling thread and — when an `IOutputService` exists — the Memory channel writer, returning an `IDisposable` for the writer. Configuration,
logging and output are optional and resolved lazily on the sampling thread (no configuration: unlimited budget, idle trim on). Timings can be
overridden by registering a `MemoryMonitorOptions` instance first. `Dispose` is synchronous and non-blocking (the thread exits by itself), safe to
call twice (the container and the shell's wrapper both dispose it).

## 10. Responsiveness (MUST 9, T23)

<!--RESP-->

## 11. SHOULD items

* **Telemetry**: allocation rate and GC pause time as `CsCode` meter instruments (above); the **Memory** output channel writes a summary line every
  `logIntervalSeconds` (resident set split anon/file/shmem/swap, of-budget, GC heap/committed/LOH, collections, allocation MB/s and pause % since
  the previous line, threads, children by name) and one line per trim with its measured effect. The channel appears with its first line.
* **`MemoryBreakdown.Capture()`** (for an About/diagnostics view; `ToText()` renders it): `/proc/self/smaps_rollup` totals (RSS, PSS,
  private/shared, anonymous, swap) and `/proc/self/smaps` grouped into anonymous memory, managed assemblies, native libraries (Skia/HarfBuzz and
  ICU named), other files; anonymous memory split into managed (the GC's committed bytes) and native (the rest: malloc'd Skia/ICU/runtime memory,
  JIT code, stacks). The split is an estimate: the GC's regions are anonymous mappings smaps cannot tell apart. Windows/macOS: totals only.
* Windows/macOS paths compile (`OperatingSystem` guards); untested here.

<!--ANALYZERS-->

## 12. Findings for the end pass

<!--FINDINGS-->

## 13. Tests

```
bash .scratch/notes/with-nix-env.sh dotnet test tests/CsCode.MemoryTests                       # fast variants (DISPLAY unset)
CSCODE_SOAK=1 CSCODE_SOAK_MINUTES=10 dotnet test tests/CsCode.MemoryTests --filter Category=Soak # long soaks
tools/profile/budget.sh | tools/profile/gc-matrix.sh | tools/profile/soak.sh | tools/profile/bench.sh
```
