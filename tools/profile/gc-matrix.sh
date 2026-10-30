#!/usr/bin/env bash
# The GC / runtime configuration matrix (docs/MEMORY.md, MUST 3). Runs the profiling host's workloads once per configuration and repetition, each in
# a fresh process with the configuration set through DOTNET_* environment variables (equivalent to the runtimeconfig knobs), and collects one JSON
# line per run: peak RSS, steady-state RSS, GC pause p50/p99/max, CPU seconds, wall seconds, collections, allocations.
#
#   tools/profile/gc-matrix.sh [--workload synthetic|app] [--reps N] [--only NAME,NAME]
#
# Configurations are one-factor-at-a-time from the app's current settings (workstation, concurrent GC, tiered PGO, SustainedLowLatency set by
# AppStartup.ConfigureRuntime), plus server GC as a contrast and ReadyToRun through a separate publish (tools/profile/publish-host.sh --r2r).
# Results: .scratch/profile/gc-matrix-<workload>-<time>.jsonl and a median table on stdout (tools/profile/gc-matrix-table.py).
set -euo pipefail
# shellcheck disable=SC1091
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
workload=synthetic
reps=3
only=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --workload) workload="$2"; shift 2 ;;
    --reps) reps="$2"; shift 2 ;;
    --only) only="$2"; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
case "$workload" in
  synthetic) args=(synthetic --steps 10000) ;;
  app) args=(app --iterations 10) ;;
  *) echo "unknown workload $workload" >&2; exit 2 ;;
esac

# name | flavour (jit/r2r) | environment
configs=(
  "baseline|jit|"
  "concurrent-off|jit|DOTNET_gcConcurrent=0"
  "conserve-3|jit|DOTNET_GCConserveMemory=3"
  "conserve-6|jit|DOTNET_GCConserveMemory=6"
  "conserve-9|jit|DOTNET_GCConserveMemory=9"
  "retain-vm|jit|DOTNET_GCRetainVM=1"
  "tiered-off|jit|DOTNET_TieredCompilation=0"
  "tiered-pgo-off|jit|DOTNET_TieredPGO=0"
  "quickjit-loops-off|jit|DOTNET_TC_QuickJitForLoops=0"
  "ready-to-run|r2r|"
  "framework-r2r-off|jit|DOTNET_ReadyToRun=0"
  "gen0-4mb|jit|DOTNET_GCgen0size=0x400000"
  "gen0-64mb|jit|DOTNET_GCgen0size=0x4000000"
  "gen0-max-budget-6mb|jit|DOTNET_GCgen0MaxBudget=0x600000"
  "invariant-globalization|jit|DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1"
  "server-gc|jit|DOTNET_gcServer=1"
  "server-gc-2-heaps|jit|DOTNET_gcServer=1 DOTNET_GCHeapCount=2"
)

out="$scratch/gc-matrix-$workload-$(date +%Y%m%d-%H%M%S).jsonl"
for rep in $(seq 1 "$reps"); do
  for entry in "${configs[@]}"; do
    IFS='|' read -r name flavour envs <<<"$entry"
    if [[ -n "$only" && ",$only," != *",$name,"* ]]; then continue; fi
    dll="$(host_dll "$flavour")"
    read -r -a envarr <<<"$envs"
    line="$(env "${envarr[@]}" dotnet "$dll" "${args[@]}" 2>"$scratch/gc-matrix-last.err" | sed -n 's/^RESULT //p' | tail -1)" || true
    if [[ -z "$line" ]]; then echo "$name rep $rep: FAILED (see $scratch/gc-matrix-last.err)" >&2; continue; fi
    echo "{\"name\":\"$name\",\"rep\":$rep,\"load\":\"$(cut -d' ' -f1 /proc/loadavg)\",\"result\":$line}" >> "$out"
    echo "$name rep $rep: $line" >&2
  done
done
echo "results: $out"
python3 -I "$root/tools/profile/gc-matrix-table.py" "$out"
