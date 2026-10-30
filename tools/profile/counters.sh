#!/usr/bin/env bash
# Live counters of a running CsCode (docs/MEMORY.md): the app's own CsCode meter (working set, private/file-backed memory, GC heap and committed,
# LOH, children, budget, threads, allocation rate, GC pause time, trims) next to the runtime's System.Runtime counters.
#
#   tools/profile/counters.sh PID                    # live view (dotnet-counters monitor)
#   tools/profile/counters.sh PID --csv SECONDS      # record to .scratch/profile/counters-PID-<time>.csv for SECONDS
#
# PID must be a CsCode process you started.
set -euo pipefail
# shellcheck disable=SC1091
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
counters="$(dotnet_tool dotnet-counters)"
pid="${1:?usage: counters.sh PID [--csv SECONDS]}"
if [[ "${2:-}" == "--csv" ]]; then
  out="$scratch/counters-$pid-$(date +%Y%m%d-%H%M%S).csv"
  "$counters" collect -p "$pid" --counters CsCode,System.Runtime --format csv -o "$out" --duration "00:00:$(printf %02d "${3:-30}")"
  echo "counters: $out"
else
  "$counters" monitor -p "$pid" --counters CsCode,System.Runtime
fi
