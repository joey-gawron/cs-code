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

**Harness**: `tools/profile/gc-matrix.sh` runs the profiling host once per configuration and repetition, each in a fresh process with the setting
as a `DOTNET_*` environment variable (which overrides the runtimeconfig knob; the host records the GC's effective configuration through
`GC.GetConfigurationVariables()`), from a Release publish (`tools/profile/publish-host.sh`; the ReadyToRun row uses
`publish-host.sh --r2r`, the tool's own harness build — the app's publish settings are W5's `build/Publish.props`). Columns: **peak RSS** =
VmHWM over the process lifetime; **steady RSS** = median of 100 ms samples over the second half of the workload; **GC pauses** = every
suspension (runtime events, `GcPauseRecorder`); **CPU** = user + system seconds. Every run also has the app's own latency mode
(SustainedLowLatency). Medians of the repetitions; the load column is the machine's 1-minute load average at each run (shared with other agents).

*Synthetic workload* (`cscode-profile synthetic --steps 10000`, ~10 s: documents of 1 KB–2 MB edited through `InMemoryTextDocument` (large ones
churn the LOH), token-state graphs per line, short-lived strings, UI-like churn and diagnostics publishes; 3 repetitions):

| configuration | runs | peak RSS MB | steady RSS MB | GC pause p50 ms | GC pause p99 ms | GC pause max ms | CPU s | wall s | gen2 | pauses | load |
|---|---|---|---|---|---|---|---|---|---|---|---|
| baseline | 3 | 215.6 | 131.3 | 0.3 | 3.1 | 5.8 | 10.6 | 10.4 | 832.0 | 1985.0 | 23.78, 14.35, 14.65 |
| concurrent-off | 3 | 198.8 | 132.3 | 0.3 | 2.9 | 5.5 | 10.5 | 10.3 | 828.0 | 1979.0 | 21.96, 12.89, 14.08 |
| conserve-3 | 3 | 157.3 | 110.4 | 0.3 | 3.2 | 15.1 | 11.9 | 11.4 | 1051.0 | 2414.0 | 18.96, 11.60, 19.54 |
| conserve-6 | 3 | 135.8 | 105.0 | 0.2 | 4.8 | 14.6 | 12.4 | 11.8 | 1993.0 | 4053.0 | 16.18, 10.51, 17.23 |
| conserve-9 | 3 | 115.9 | 82.0 | 0.3 | 6.0 | 22.0 | 17.8 | 16.4 | 2584.0 | 4234.0 | 14.46, 19.92, 15.04 |
| retain-vm | 3 | 202.9 | 129.3 | 0.3 | 3.0 | 4.7 | 11.3 | 10.9 | 841.0 | 2004.0 | 12.52, 17.63, 13.49 |
| tiered-off | 3 | 195.1 | 124.7 | 0.3 | 3.4 | 6.5 | 12.0 | 12.0 | 830.0 | 1969.0 | 11.74, 16.33, 12.04 |
| tiered-pgo-off | 3 | 221.2 | 130.5 | 0.3 | 3.1 | 6.1 | 10.4 | 10.4 | 818.0 | 1958.0 | 10.84, 15.12, 11.12 |
| quickjit-loops-off | 3 | 204.5 | 125.6 | 0.3 | 3.6 | 9.5 | 12.7 | 12.2 | 830.0 | 1973.0 | 10.19, 14.00, 51.80 |
| ready-to-run | 3 | 198.9 | 125.1 | 0.3 | 4.6 | 10.1 | 14.1 | 13.6 | 817.0 | 1944.0 | 9.55, 16.46, 48.74 |
| framework-r2r-off | 3 | 204.9 | 130.7 | 0.4 | 6.3 | 12.1 | 13.2 | 14.5 | 827.0 | 1971.0 | 14.38, 20.47, 39.78 |
| gen0-4mb | 3 | 208.8 | 130.6 | 0.4 | 3.8 | 12.9 | 12.8 | 12.7 | 857.0 | 2361.0 | 14.46, 19.32, 34.35 |
| gen0-64mb | 3 | 227.5 | 141.2 | 0.3 | 3.8 | 7.7 | 12.3 | 11.7 | 806.0 | 1672.0 | 13.08, 17.27, 28.08 |
| gen0-max-budget-6mb | 3 | 215.7 | 136.6 | 0.3 | 4.4 | 10.5 | 12.7 | 12.3 | 810.0 | 1944.0 | 12.07, 22.77, 24.59 |
| invariant-globalization | 3 | 205.4 | 122.5 | 0.3 | 4.2 | 8.6 | 14.3 | 13.6 | 823.0 | 1967.0 | 12.46, 20.05, 21.73 |
| server-gc | 3 | 296.0 | 204.2 | 5.8 | 32.0 | 51.7 | 61.8 | 18.0 | 232.0 | 523.0 | 13.11, 16.69, 18.10 |
| server-gc-2-heaps | 3 | 173.2 | 122.4 | 0.3 | 2.9 | 6.6 | 13.1 | 12.8 | 865.0 | 1764.0 | 16.14, 15.64, 15.79 |

*Real-app workload* (`cscode-profile app --iterations 6`, ~45 s: the headless app scrolls a 10k-line file, opens and closes 20 files, types
into one, switches themes and shows/hides tool windows each iteration; **1 repetition** per configuration — the second repetition was cut for
time, so differences under ~15 MB or ~20 % CPU are within noise):

| configuration | runs | peak RSS MB | steady RSS MB | GC pause p50 ms | GC pause p99 ms | GC pause max ms | CPU s | wall s | gen2 | pauses | load |
|---|---|---|---|---|---|---|---|---|---|---|---|
| baseline | 1 | 367.0 | 365.2 | 2.3 | 37.4 | 110.0 | 92.6 | 48.4 | 171.0 | 537.0 | 20.24 |
| concurrent-off | 1 | 363.3 | 357.6 | 3.0 | 35.8 | 57.8 | 103.9 | 54.5 | 159.0 | 511.0 | 24.05 |
| conserve-3 | 1 | 371.1 | 366.6 | 2.7 | 29.7 | 36.2 | 93.1 | 46.9 | 158.0 | 512.0 | 16.68 |
| conserve-6 | 1 | 371.6 | 355.9 | 3.7 | 34.8 | 70.4 | 118.3 | 61.0 | 165.0 | 521.0 | 19.42 |
| conserve-9 | 1 | 363.0 | 343.7 | 3.9 | 68.6 | 144.4 | 123.0 | 64.6 | 174.0 | 525.0 | 24.42 |
| retain-vm | 1 | 372.2 | 366.0 | 2.8 | 31.5 | 51.3 | 94.2 | 49.0 | 156.0 | 509.0 | 21.75 |
| tiered-pgo-off | 1 | 337.6 | 336.1 | 3.7 | 41.5 | 78.8 | 97.7 | 55.9 | 156.0 | 508.0 | 17.43 |
| ready-to-run | 1 | 377.5 | 370.8 | 2.9 | 32.0 | 46.8 | 100.0 | 48.0 | 161.0 | 516.0 | 14.36 |
| framework-r2r-off | 1 | 361.1 | 355.6 | 3.3 | 39.4 | 66.7 | 111.1 | 56.9 | 162.0 | 516.0 | 21.42 |
| gen0-4mb | 1 | 365.3 | 357.0 | 3.0 | 18.7 | 32.3 | 84.0 | 43.9 | 159.0 | 712.0 | 14.21 |
| invariant-globalization | 1 | 354.9 | 348.8 | 2.4 | 24.1 | 34.7 | 84.2 | 43.5 | 160.0 | 512.0 | 11.39 |
| server-gc | 1 | 509.0 | 505.5 | 3.6 | 34.3 | 67.0 | 101.1 | 40.8 | 140.0 | 312.0 | 11.70 |

**Decision** (encoded in `src/CsCode.Profiling/runtimeconfig.recommended.json` and `build/MemoryDefaults.props`, which the integrator imports into
`CsCode.App.csproj`; measured on Linux x64 here — Windows/macOS are assumed to behave alike, not measured):

| Knob | Choice | Evidence |
|---|---|---|
| `System.GC.Server` | **false** (workstation) | server GC: +140 MB steady in the app, +73 MB in the synthetic run, p99 pause 32 ms vs 3 ms (synthetic) |
| `System.GC.Concurrent` | **true** | off saved nothing (app −8 MB, within noise; synthetic equal) and loses background gen2 collections, the protection of a UI thread against long gen2 pauses |
| `System.GC.ConserveMemory` | **3** | synthetic: peak −58 MB, steady −21 MB for +12 % CPU and max pause 15 ms; app: neutral. 6 and 9 saved more in the synthetic run (steady −26/−49 MB) but cost +17/+68 % CPU and the app's p99 pause doubled at 9 |
| `System.GC.RetainVM` | false | no measurable effect |
| `TieredCompilation` / `TieredPGO` / `QuickJitForLoops` | on (unchanged) | turning tiering off cost CPU without a memory win; PGO off showed −29 MB in the single app run but nothing in the synthetic one (re-measure before acting) |
| ReadyToRun | W5's call (startup) | the R2R harness build ran +5 MB in the app (more mapped code) and within noise in the synthetic run; R2R is a start-up feature, not a memory one |
| `GCgen0size` / `GCgen0MaxBudget` | default | 4 MB gen0: +20 % CPU in the synthetic run; its lower app pauses come from one run at lower load. 64 MB: +10 MB steady |
| `System.Globalization.Invariant` | **false** (ICU stays) | −16 MB steady in the one app run (ICU's data is ~25 MB of file-backed, shareable pages: `libicudata` 16–22 MB resident in the budget runs), but culture-sensitive comparison, casing and sorting change (Turkish i, accents, the Explorer's sort order) |
| `DOTNET_gcServer=0`, `GCHeapCount` | workstation; heap count is a server-GC knob | server GC with 2 heaps: −42 MB peak in the synthetic run but still server GC's threads and higher app-level risk |
| `GCSettings.LatencyMode` | SustainedLowLatency (unchanged, set in code by `AppStartup.ConfigureRuntime`) | the editor's latency figures were measured in this mode |

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

**Fast run results** (the full `dotnet test tests/CsCode.MemoryTests` of 2026-10-29 22:24–22:30, load 11–20, with the soak below running beside
it): every Core-level soak passed (live heap 0 ± 0 B/iteration for documents, emitter, child tracking; within ± a few hundred bytes for the rest,
threads and descriptors back to baseline). App level, live heap per iteration and outcome:

| Scenario | Live heap / iteration (95 %) | Outcome |
|---|---|---|
| Themes (3 switches) | 118 ± 889 B | pass |
| Tool windows (4 show/hide) | −199 ± 1,694 B | pass |
| Terminal create/kill | 11 ± 3 KB | pass; no process left |
| Language server start/stop (minimal server) | 7 ± 6 KB | pass; no process left |
| js-debug session (stop at a breakpoint, Shift+F5) | 8 ± 24 KB | pass; no process left |
| Remote session connect/open/disconnect | −3 ± 10 KB | pass; no process left |
| Typing 100 keys + close unsaved | −6.1 ± 5.2 MB (noise: heavy GC churn) | pass |
| Open/close 20 documents | 5.4 ± 3.5 MB (6 iterations) | **fail** in that run (1.8 ± 2.5 MB, pass, in an earlier one); warm-up raised to 4 — see the long soak, whose open/close iterations plateau |
| Scroll a 20k-line file end to end | **4.8 ± 0.5 MB** | **fail — a real leak** (finding 8): a census of 16 passes grows linearly, 30.5 → 85.8 MB live |

### The real ≥ 10-minute soak

`tools/profile/soak.sh --minutes 12` (Release host, headless real app, 2026-10-29 22:24–22:36, load 11–20 with the fast suite running beside it):
the **mixed scenario** — each iteration opens 5 of 20 files, types 50 characters into one, opens the 10k-line file, closes everything; every 5th
iteration also switches three themes, shows/hides four tool windows and creates/kills a terminal. 10 warm-up iterations, then a sample every 10 s
(full collection first, so the heap series is the live heap). Series: `.scratch/soak/app-mixed-20261029-223619.csv`.

| | Whole run (680 iterations, 721 s, 68 samples) | First half | Second half | Last third |
|---|---|---|---|---|
| Live GC heap | 65.2 → 66.7 MB; **1,286 ± 891 B/iteration = 4.2 ± 2.8 MB/h** | 8.5 MB/h | **2.5 MB/h** | −4.1 MB/h (flat within noise) |
| Resident set | 320 → 382 MB; 239 ± 23 MB/h; peaks by third 357 / 378 / 384 MB | 358 MB/h (320 → 378) | 87 MB/h (373 → 382) | 110 MB/h |
| Threads / fds / children | 32 → 34 / 219 → 219 / 1 → 1 (the sandboxed bash of the last terminal iteration) | | | |
| Gen2 collections per iteration | 3.61 → 3.54 (not growing) | | | |

Reading: the **live heap plateaus** at ~66–67 MB (its slope falls from 8.5 MB/h to flat within the run); the **resident set is still settling**
— it climbed 58 MB in the first 6 minutes and 9 MB in the last 6 (GC-committed and native memory grow towards the working size of the mixed
workload, not the live heap). The ≤ 1 MB/h target after warm-up is **not yet demonstrated**: the whole-run interval (4.2 ± 2.8 MB/h) includes the
settling phase; a ≥ 1 h soak with a longer warm-up (`soak.sh --minutes 60`) is needed to state the plateau of the resident set. The scroll
scenario was left out of the mixed soak on purpose: it leaks (finding 8).

## 6. Benchmarks of Core's hot paths (MUST 6)

`tools/profile/bench.sh` (BenchmarkDotNet 0.15.8, `MemoryDiagnoser`, ShortRun in-process, Release; the benchmark classes are in
`tools/profile/CsCode.ProfileHost/CoreBenchmarks.cs`). ns/op were measured with the machine loaded (load average 14–21) and are noisy (see the
error column of the logs under `.scratch/profile/bench-*.log`); **allocations per operation are exact**.

| Benchmark | Before: ns/op | Before: allocated/op | After: ns/op | After: allocated/op | Change in Core |
|---|---:|---:|---:|---:|---|
| `ApplyEdits` one edit, 100 lines | 7,820 | 21,776 B | 8,790 | **10,184 B** | single-edit fast path: one `string.Concat` instead of LINQ + `StringBuilder` copy; line index counted then filled (no `List<int>` + `ToArray`) |
| `ApplyEdits` one edit, 20k lines | 2,791,796 | 4,187,434 B | 1,985,640 | **2,001,240 B** | same (the rest is the new text and its line index: `InMemoryTextDocument` is a string buffer by design; the editor uses AvaloniaEdit's rope) |
| `Glob.IsMatch("*.cs", path)` | 142 | 64 B | **50** | **0 B** | match on a span of the file name (no substring); compiled backtracking regex |
| `Glob.IsMatch("**/src/**/*.cs", path)` | 248 | 0 B | **137** | 0 B | compiled backtracking regex instead of NonBacktracking (which also kept ~150 KB of caches per pattern: 6 MB in the app) |
| `DocumentSelector.Score` (3 filters) | 420 | 192 B | **271** | **128 B** | loop instead of LINQ `Max` (the remaining 128 B is `Uri.LocalPath`) |
| `LanguageFeatureRegistry.GetProviders` (17 registrations) | 2,608 | 2,160 B | **900** | **136 B** | one candidate array, scores outside the lock, in-place insertion sort (same order: best score, then newest) |
| `Emitter.Fire` (8 subscribers) | 17.6 | 0 B | 9.3 | 0 B | none (already allocation-free) |
| `DiagnosticService.Set` | 71.5 | 24 B | 78.5 | 24 B | none (the 24 B is the `[uri]` change notification) |
| `DiagnosticService.GetAll` (2,000 entries) | 50,438 | 61,752 B | **12,057** | **32,024 B** | exact-size array, no iterator chain |

The interpreter (non-compiled) backtracking engine was tried first: it was 10× slower on path globs (2,430 ns) and on selectors with a pattern
(2,975 ns), so globs compile instead (a few KB of IL each; first use of a pattern costs about a millisecond). Behaviour is pinned by
`Benchmarks/CoreHotPathTests` (the new engine matches exactly like the NonBacktracking translation over a corpus of patterns and paths; a
pathological glob ends within its 250 ms match timeout and does not match; cached globs retain < 20 KB each; the registry keeps its order) and the
Core tests (35/35).

## 7. Memory budget of the real app (MUST 7)

`tools/profile/budget.sh` (Release host, headless Avalonia with Skia, `CSCODE_HOME` in a temp dir, built-in language servers off except the one
named): one process goes through the stages cumulatively; each stage settles 15 s (with frames), then the app process is measured (`VmRSS`, PSS
from `smaps_rollup`, the GC heap, the native-anonymous estimate of `MemoryBreakdown`) together with its child process tree, and once more after an
Idle trim. Measured 2026-10-29 21:48 (load ~10–20), commit 5338093 (before the glob change of section 6). Raw: `.scratch/profile/budget-*.jsonl`.

| Stage (cumulative) | App RSS | App PSS | GC heap (committed) | Native anon | Threads | Children (RSS) | App RSS after idle trim | Status |
|---|---:|---:|---:|---:|---:|---|---:|---|
| Idle, no folder | **192 MB** | 124 MB | 13 (22) MB | 43 MB | 26 | — | 191 MB | measured |
| Folder open (Explorer) | 214 MB | 131 MB | 19 (31) MB | 36 MB | 32 | — | 208 MB | measured |
| 20 open documents | **323 MB** | 240 MB | 85 (99) MB | 60 MB | 34 | — | 309 MB | measured |
| + 1 language server (typescript-language-server on a 2-file TS project) | **345 MB** | 260 MB | 89 (98) MB | 70 MB | 33 | **549 MB**: 4 node processes (typescript-language-server, 2 tsserver, typings installer) | 343 MB | measured |
| + debug session (js-debug + node, stopped at a breakpoint) | **372 MB** | 286 MB | 94 (105) MB | 85 MB | 38 | 1,012 MB: +3 node (js-debug server, debuggee) ≈ +464 MB | 367 MB | measured |
| + terminal (bash, Terminal window shown) | 373 MB | 286 MB | 93 (110) MB | 79 MB | 37 | +4 MB (bash) | 366 MB | measured |
| + remote session (loopback `cscode-server`, one remote file open) | **372 MB** | 284 MB | 92 (108) MB | 77 MB | 46 | **+786 MB**: cscode-server 83 MB + the TypeScript server it started for the remote `.js` file (4 node, ~700 MB) | 373 MB | measured |
| Soak growth after warm-up | see section 5 | | | | | | | measured (≤ 1 MB/h target) |

Notes: the headless platform renders in software into memory; a desktop window adds the X11 backend and the GPU driver's memory (a real-desktop
measurement is the integrator's: start `cscode --foreground`, then `tools/profile/counters.sh PID` or read the status bar's memory item). PSS
splits shared library pages with other processes; RSS counts them fully. Of the idle 192 MB, 96 MB is file-backed (assemblies 49 MB, fonts and
other files 38 MB, ICU data 16 MB, native libraries 16 MB) — mostly clean, shareable pages — and 65 MB anonymous (GC 22 MB committed, the rest
native: Skia, HarfBuzz, the runtime, JIT code, stacks).

**Where the 20 documents' 66 MB of managed heap goes** (playbook demonstration: `cscode-profile hold --documents 20`, `dotnet-dump collect
--type Heap`, `dumpheap -stat`, `gcroot`; 76 MB live after a full GC): TextMateSharp keeps each grammar's **raw JSON tree** after compiling it
(`Parser.Raw` 67k objects 5.9 MB + their `Dictionary<string, object>` entries 7.5 MB + key collections 0.9 MB + a large share of the 9.9 MB of
strings ≈ 15–20 MB for the C#, Markdown, JSON and shell grammars); **NonBacktracking regexes cached by `Glob`** held 5.3 MB of symbolic-automaton
dictionaries + 0.9 MB of BDDs (gcroot: the static `Glob.Cache` → `SymbolicRegexBuilder` → `CharSetSolver`) — fixed in section 6; 10.9 MB is free
space between objects (fragmentation); the compiled TextMate rules (`MatchRule`, `CaptureRule`, `RegExpSource`, `RuleId` tables) ~3 MB; the rest is
Avalonia's visual tree and composition objects (`ServerCompositionDrawListVisual`, `StyleInstance`, `DynamicResourceExpression` ×5,011) and the
editor views kept alive. (`dotnet-gcdump` on .NET 10 warns that its lossy event buffering can drop objects on large heaps — its per-type totals
did not add up here; use `dotnet-dump` for exact numbers.)

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

The toolkit (`CsCode.Profiling.Testing`, no Avalonia reference — every probe takes the thread's `Post(Action)`):

* **`ResponsivenessProbe(post, period = 10 ms)`**: a dedicated thread posts a tick every period, each tick records how long it waited in the
  thread's queue (what an input or render job posted at that moment would have waited). Ticks are pre-allocated (a ring of slots with cached
  delegates) and latencies go into a `LatencyHistogram` (HdrHistogram-style fixed buckets, exact below 128 µs, ≤ 1.6 % above, one interlocked
  increment per sample): **no allocation per sample** beyond the post itself (asserted). Every period gets its own tick even while earlier ones
  wait, as the repo's earlier heartbeats measured, so numbers stay comparable; a full ring counts ticks as dropped, `Pending` reveals a stuck
  thread. `Report()`/`StopAsync()` → p50/p90/p99/max; `AssertWithin(16, 50)` is ARCHITECTURE §6's budget.
* **`TypingLatencyProbe(postInput, deliver)`**: posts each key as an input job and measures key → applied buffer mutation (end of the synchronous
  delivery, or `NotifyMutated()` from the buffer's change event), split into the wait for the thread and the handling (the terminal tests'
  split). `AssertWithin(p99: 20)`.
* **`ThreadBlockedDetector(post, threshold)`**: one acknowledgement in flight, every wait over the threshold recorded; `AssertNoBlocks()`. Plus
  `AssertCallerNotBlocked(call, max)`: the synchronous cost of an API call (the "does not block the caller" tests; median of N calls).
* **`GcPauseRecorder`**: every GC pause of the process from the runtime's events (p50/p99/max), collections by generation and trigger reasons —
  tells a GC pause from the app's own work in a budget report.
* **Hostile load** (`Load/`): `CpuBurner` (in-process threads, or child `sh` busy loops optionally `nice`d, killed by pid), `SlowResponder`
  (awaited delay, or blocking the caller), `Flooder` (paced diagnostics publishes / output lines / any emitter on a producer thread),
  `GiantPayload` (50k completion items, 1M semantic tokens, 100 MB of log lines produced lazily, N diagnostics), `Hanger` (never answers until
  cancelled), `CrashLooper` (throwing provider, a process that exits at once).

**The matrix** (`tests/CsCode.MemoryTests/Responsiveness/HostileMatrix.cs`) is expressed against Core interfaces only (`IDiagnosticService`,
`IOutputService`, `ILanguageFeatureRegistry`/`ICompletionProvider`, `ICommandService`) through a `HostileContext`, so any host can run it:
diagnostics flood (200 publishes/s × 500 diagnostics over 20 files including the edited one), output flood (20,000 lines/s), giant completion
(50k items), slow completion (2 s), **blocking** completion (blocks its calling thread 200 ms — catches a provider called on the UI thread),
hanging completion, crashing completion, CPU burners (a quarter of the cores at nice 10, like a pegged language server: the machine is shared).
Each row: a user types 80 keys at 50 ms intervals into a real editor while the load runs. Budgets: dispatcher p99 ≤ 16 ms and max ≤ 50 ms, typing
p99 ≤ 20 ms, no key lost, heap within 64 MB of the start once the load is gone, UI queue drained (`IUiDispatcher.Stats` posted − executed ≤ 2)
within 3 s.

<!--HOSTILE-->

### Measuring UI budgets headless (T23)

The headless platform measures a different machine than a desktop unless the test is careful. Rules, as the terminal's `BudgetTests` and the
matrix above apply them:

1. **Post input as dispatcher jobs** at `DispatcherPriority.Input` (`TypingLatencyProbe` does): `Dispatcher.UIThread.Post(() =>
   target.RaiseEvent(new TextInputEventArgs { … }), DispatcherPriority.Input)`. Calling `TextArea.PerformTextInput` directly measures only the
   handler, and Avalonia's headless helpers (`window.KeyPress`, `KeyTextInput`) run the dispatcher queue themselves — either way the key never
   waits behind queued work, which is exactly what the budget is about.
2. **Do not pump from the measuring code.** `Dispatcher.UIThread.RunJobs()` inside the measured window runs the queue on the test's schedule;
   let the headless test's own loop run it (`await Task.Delay`), and measure with `ResponsivenessProbe(a => Dispatcher.UIThread.Post(a))`.
3. **Report the headless rasteriser separately.** The headless platform renders only when ticked (`AvaloniaHeadlessPlatform.ForceRenderTimerTick`)
   and then rasterises the invalidated area with software Skia **on the UI thread** (4–10 ms a frame, 15–25 gen2 collections a second while
   something keeps changing — triage item 23); a desktop rasterises on its render thread. Budget windows run without ticks (or, like the
   terminal, record only the control's own `Render` into a `DrawingGroup`, which is the UI thread's per-frame share on a desktop); a second window
   with 60 Hz ticks is reported as "with headless rasterisation", never budgeted.
4. **Run the app's GC mode** (`GCSettings.LatencyMode = SustainedLowLatency`, `AppStartup.ConfigureRuntime`) and put a `GcPauseRecorder` report
   next to the numbers.
5. **Best of three attempts, every attempt reported with the load average**; on this shared machine rerun a miss alone with
   `/proc/loadavg` < 4 before calling it real, and never loosen a budget.
6. **Check the aftermath**: queues drained (`Stats.Posted − Stats.Executed`, the probe's `Pending`), heap back after a full collection.

**Existing budget tests that should move to the shared probe** (not rewritten here — each module owns its tests):

| Test (project) | Today | Move to |
|---|---|---|
| `ResponsivenessTests` ×2 storms (App.Tests) | own 10 ms heartbeat thread, sorted list | `ResponsivenessProbe` + `GcPauseRecorder` (the storms are the known load-sensitive misses) |
| `ThemeSwitchingCostTests.Previewing_themes_at_key_repeat_rate…` (App.Tests, T4) | own heartbeat; keys via `window.KeyPress` (pumps the queue itself) and `RunJobs` loops in the window | keys as Input jobs, probe, no pumping |
| `VimPerformanceTests` ×2 (App.Tests) | own heartbeat, `RunJobs` in the measuring loop | `TypingLatencyProbe` + probe |
| `MacroRealAppTests.Playing_ten_thousand_times…` (App.Tests; known "10k budget under load") | posted timestamps + `RunJobs` loop | probe without pumping |
| `HostileProviderTests` typing tests (Editor.Tests) | `PerformTextInput` called directly (handler cost only) + own posted latencies + `RunJobs` | `TypingLatencyProbe` (Input jobs) + probe |
| `ResponsivenessTests` ×5, `RealGdbResponsivenessTests`, `RealJsDebugResponsivenessTests` (Panels.Debug.Tests; the 1M-line flood is load-sensitive) | own heartbeat | probe + `GcPauseRecorder` |
| `HostileTests` (Panels.Search.Tests) | own `HeartbeatProbe` | `ResponsivenessProbe` (same idea, histogram, pending/dropped) |
| `MacroPlayerTests.Playing_ten_thousand_times…` (Macros.Tests) | its own `ResponsivenessProbe` over `TestUiThread` | the shared probe with `TestUiThread.Post` |
| `ThreadingTests` "returns to the caller in well under a millisecond" (Dap.Tests), "Service_calls_from_the_ui_thread_return_immediately" (App.Tests) | stopwatches around calls | `ThreadBlockedDetector.AssertCallerNotBlocked` |
| `BudgetTests` (Panels.Terminal.Tests) | already follows rules 1–5 (it is the model) | optional: the shared probe for the histogram and pending/dropped counts |

## 11. SHOULD items

* **Telemetry**: allocation rate and GC pause time as `CsCode` meter instruments (above); the **Memory** output channel writes a summary line every
  `logIntervalSeconds` (resident set split anon/file/shmem/swap, of-budget, GC heap/committed/LOH, collections, allocation MB/s and pause % since
  the previous line, threads, children by name) and one line per trim with its measured effect. The channel appears with its first line.
* **`MemoryBreakdown.Capture()`** (for an About/diagnostics view; `ToText()` renders it): `/proc/self/smaps_rollup` totals (RSS, PSS,
  private/shared, anonymous, swap) and `/proc/self/smaps` grouped into anonymous memory, managed assemblies, native libraries (Skia/HarfBuzz and
  ICU named), other files; anonymous memory split into managed (the GC's committed bytes) and native (the rest: malloc'd Skia/ICU/runtime memory,
  JIT code, stacks). The split is an estimate: the GC's regions are anonymous mappings smaps cannot tell apart. Windows/macOS: totals only.
* Windows/macOS paths compile (`OperatingSystem` guards); untested here.

### Appendix: analyzer configuration proposal (IDisposable and event hygiene)

Proposed, **not enabled** (enabling rules is a repository-wide decision for the end pass; their counts on this code base were not measured — measure
with `dotnet build CsCode.slnx -p:TreatWarningsAsErrors=false -p:AnalysisModeReliability=All -p:AnalysisModeUsage=All 2>&1 | grep -c CA2000`):

```ini
# .globalconfig (repository root) — stage 1: visible in the IDE, not in the build
dotnet_diagnostic.CA2000.severity = suggestion   # dispose objects before losing scope (noisy with DI-owned and ownership-transferring factories)
dotnet_diagnostic.CA2213.severity = warning      # disposable fields not disposed — the "own your lifetime" rule (ARCHITECTURE §8.2); few false positives
dotnet_diagnostic.CA1001.severity = warning      # types owning disposable fields are disposable (already on in the default set for public types)
dotnet_diagnostic.CA2215.severity = warning      # Dispose calls base.Dispose
```

`static event` cannot be banned with the banned-API analyzer (it bans symbols, not declarations): `EventHygiene.StaticEvents` (Profiling.Testing)
scans assemblies by reflection, and `HygieneTests.No_CsCode_assembly_declares_a_new_static_event` runs it over every CsCode assembly. For `+=` on
singletons, the banned-API list can name the long-lived events that must never be subscribed from short-lived objects
(e.g. `E:Avalonia.Threading.Dispatcher.ShutdownStarted`, `E:System.AppDomain.ProcessExit`) — a per-module list, proposed for the end pass.

## 12. Findings for the end pass

Measured here, owned elsewhere — for the end pass (nothing below was changed outside this module):

1. **TextMateSharp keeps every grammar's raw JSON tree** after compiling it: ≈ 15–20 MB of the 66 MB the app's managed heap grows by with 20
   documents (`Parser.Raw`, `Dictionary<string, object>` entries, strings; section 7). Dropping the raw grammar after compilation (Editor module,
   W1's highlighting seam) is the largest single saving found.
2. **NonBacktracking regexes keep ~150 KB of caches each** once used. Fixed for Core's `Glob` (section 6); the same pattern is in
   `Platform/Workspace/GitIgnore.cs`, `Platform/EditorConfig/EditorConfigParser.cs`, `Editor/Documents/EditorConfig.cs` and
   `Panels.Search/Engine/TextMatcher.cs` (the last one on purpose: user regexes need linear time; the glob translations do not).
3. **GC pauses in the app under load**: the hostile-matrix runs record 3–6 gen2 collections a second while typing with a load running, most of them
   `induced_noforce` (the GC's own response to `GC.AddMemoryPressure`, which SkiaSharp calls for its native surfaces) and `alloc_small`, with p99
   GC pauses of 45–155 ms — the main reason the headless budgets miss (with triage item 23's software rasteriser). Worth a desktop check
   (`tools/profile/counters.sh PID` on a real window: `cscode.gc.gen2_collections`, `cscode.gc.pause_time`).
4. **Trimmables have little to give** in a normal state (section 2: 0–1 KB at every level with 20 documents open): the big re-creatable state
   is already released when the document well disposes background views. `GCCollectionMode.Aggressive` is what returns memory (−26 MB).
5. **Hidden sessions trim twice**: `SessionWindowController.TrimFromTimer` calls `IMemoryMonitor.TrimNowAsync(Aggressive)` (which now compacts
   and runs `malloc_trim`) and then `Sessions.ProcessMemory.TrimNow()` (the same again). W5 can drop the second call now that the monitor exists.
6. `tests/CsCode.App.Tests/LifecycleTests.The_app_memory_monitor_records_child_processes_and_stands_in_while_profiling_registers_none` still passes
   but its name is no longer true: profiling now registers a monitor (the shell wraps it).
7. **Language servers dominate the footprint**: typescript-language-server on a 2-file project ≈ 550 MB in 4 node processes; a remote session
   starts its own TypeScript server on the remote host for a remote `.js` file (≈ 700 MB more on this loopback "remote"). Caps exist per server
   (`LanguageServerDefinition.RestartAboveBytes` → `TrackChildProcess` over-budget callbacks); none of the built-ins sets one.
8. **Scrolling leaks** (Editor module / AvaloniaEdit 12.0.0): each end-to-end scroll of a 20k-line file (80 `ScrollToLine` steps with frames)
   keeps ~3.7 MB more live heap, linearly, with no plateau over 16 passes (`cscode-profile census --scenario scroll`: 30.5 → 85.8 MB live,
   249 → 321 MB resident). Two heap dumps 8 passes apart (`dumpheap -stat`, `gcroot`): +7,001 `AvaloniaEdit.Folding.FoldingMarginMarker` (3.6 MB)
   still children of the open editor's `FoldingMargin` (root: `TextArea.LeftMargins` → `FoldingMargin` → marker), and with them +2,749 visual
   lines' drawing visuals, text lines and glyph runs (`VisualLineDrawingVisual`, `TextLineImpl`, `GlyphRun` ×11k, `SKTextBlob` ×9.6k). The
   folding margin adds markers on each visual-line change without removing the old ones. The most important finding of this work: a user who
   scrolls a big file for an hour grows by hundreds of megabytes until the tab is closed.
9. The language-feature registry soak shows ~24 B/iteration of live-heap growth in 400 fast iterations (inside the 64 B threshold, cause not
   identified; likely run-time warm-up) — the long variant (`CSCODE_SOAK=1`) decides.

## 13. Tests

```
bash .scratch/notes/with-nix-env.sh dotnet test tests/CsCode.MemoryTests                       # fast variants (DISPLAY unset)
CSCODE_SOAK=1 CSCODE_SOAK_MINUTES=10 dotnet test tests/CsCode.MemoryTests --filter Category=Soak # long soaks
tools/profile/budget.sh | tools/profile/gc-matrix.sh | tools/profile/soak.sh | tools/profile/bench.sh
```
