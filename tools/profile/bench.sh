#!/usr/bin/env bash
# Core hot-path benchmarks with BenchmarkDotNet's MemoryDiagnoser (docs/MEMORY.md, MUST 6): ns/op and bytes allocated/op.
#
#   tools/profile/bench.sh [BenchmarkDotNet args]      # default: every benchmark (ShortRun, in-process); results under .scratch/profile/bench/
#
# Builds the profiling host in Release and runs the benchmarks in-process (InProcessEmitToolchain).
set -euo pipefail
# shellcheck disable=SC1091
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
args=("$@")
[[ ${#args[@]} -eq 0 ]] && args=(--filter '*')
dotnet run -c Release --project "$root/tools/profile/CsCode.ProfileHost/CsCode.ProfileHost.csproj" -- bench "${args[@]}" --artifacts "$scratch/bench"
