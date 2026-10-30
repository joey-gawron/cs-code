#!/usr/bin/env bash
# Core hot-path benchmarks with BenchmarkDotNet's MemoryDiagnoser (docs/MEMORY.md, MUST 6): ns/op and bytes allocated/op.
#
#   tools/profile/bench.sh [BenchmarkDotNet args]      # default: every benchmark, short job; results under .scratch/profile/bench/
set -euo pipefail
# shellcheck disable=SC1091
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
args=("$@")
[[ ${#args[@]} -eq 0 ]] && args=(--filter '*' --job short)
cd "$scratch"
dotnet "$(host_dll jit)" bench "${args[@]}" --artifacts "$scratch/bench"
