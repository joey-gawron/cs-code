#!/usr/bin/env bash
# An EventPipe trace of a running CsCode (docs/MEMORY.md): GC events with every allocation tick (gc-verbose) by default, or CPU sampling.
# Open the .nettrace in PerfView / Visual Studio, or convert: dotnet-trace convert --format Speedscope FILE.
#
#   tools/profile/trace.sh PID [SECONDS] [gc-verbose|cpu-sampling|gc-collect]   # -> .scratch/profile/trace-PID-<time>.nettrace
set -euo pipefail
# shellcheck disable=SC1091
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
trace="$(dotnet_tool dotnet-trace)"
pid="${1:?usage: trace.sh PID [SECONDS] [PROFILE]}"
seconds="${2:-20}"
profile="${3:-gc-verbose}"
out="$scratch/trace-$pid-$(date +%Y%m%d-%H%M%S).nettrace"
"$trace" collect -p "$pid" --profile "$profile" --duration "00:00:$(printf %02d "$seconds")" -o "$out"
echo "trace: $out"
