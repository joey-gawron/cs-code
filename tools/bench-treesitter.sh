#!/usr/bin/env bash
# Reruns the WHOLE tree-sitter vs TextMate benchmark set of docs/TREE-SITTER-EVALUATION.md in one command, so the numbers can be repeated
# on a quiet machine (the first ones were taken while five other agents loaded it: every row carries the 1-minute load average).
#
#   tools/bench-treesitter.sh            # Release build (default)
#   tools/bench-treesitter.sh --debug    # Debug build (what the editor's other numbers in docs/EDITOR.md use)
#   tools/bench-treesitter.sh --filter B1   # only the scenarios whose name contains B1 (B1, B1b, B2_B3, B4, B5)
#   tools/bench-treesitter.sh --pass timing # only the timing pass (or --pass memory)
#
# Results: .scratch/bench/treesitter-<run>.md (git-ignored). Two passes: timings with tree-sitter's counting allocator OFF, then memory with
# it ON (exact native bytes). Needs what the evaluation used: libtree-sitter 0.25+ and compiled grammars (found automatically in the Nix
# store, or set CSCODE_TREESITTER_LIB / CSCODE_TREESITTER_GRAMMARS). Nothing is downloaded. Headless: no window opens.
set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
cd "$root"

config=Release
filter=TreeSitterBenchmarks
passes="0 1"
while [ $# -gt 0 ]; do
  case "$1" in
    --debug) config=Debug ;;
    --filter) shift; filter="TreeSitterBenchmarks&FullyQualifiedName~$1" ;;
    --pass) shift; case "$1" in timing) passes=0 ;; memory) passes=1 ;; both) passes="0 1" ;; *) echo "--pass timing|memory|both" >&2; exit 2 ;; esac ;;
    *) echo "unknown argument $1" >&2; exit 2 ;;
  esac
  shift
done

run=$(date +%Y%m%d-%H%M%S)
out=".scratch/bench/treesitter-$run.md"
mkdir -p .scratch/bench
home=$(mktemp -d "${TMPDIR:-/tmp}/cscode-bench-XXXXXX")
trap 'rm -rf "$home"' EXIT

unset DISPLAY WAYLAND_DISPLAY SSH_AUTH_SOCK
export CSCODE_HOME="$home" CSCODE_BENCH_TREESITTER=1 CSCODE_BENCH_RUN="$run"
# shellcheck disable=SC1091
[ -f tools/nix-dev-env.sh ] && source tools/nix-dev-env.sh >/dev/null

{
  echo "# tree-sitter vs TextMate benchmarks, run $run ($config build)"
  echo
  echo "* machine: $(uname -sr), $(nproc) cores, $(awk '/MemTotal/ {printf "%.0f GB", $2/1024/1024}' /proc/meminfo)"
  echo "* load average at start: $(cut -d' ' -f1-3 /proc/loadavg)"
  echo "* commit: $(git rev-parse --short HEAD 2>/dev/null || echo unknown)"
  echo
  echo "| scenario | engine | metric | value | unit | load1 | alloc tracking |"
  echo "|---|---|---|---:|---|---:|---|"
} > "$out"

dotnet build tests/CsCode.Editor.Tests/CsCode.Editor.Tests.csproj -c "$config" -nologo -v q
for track in $passes; do
  echo "== pass $((track + 1))/2 (allocation tracking $([ $track = 1 ] && echo on || echo off))"
  CSCODE_TREESITTER_TRACK_ALLOC=$track dotnet test tests/CsCode.Editor.Tests/CsCode.Editor.Tests.csproj -c "$config" --no-build \
    --filter "FullyQualifiedName~$filter" -- RunConfiguration.MaxCpuCount=1 || echo "(some benchmarks failed: see above)"
done
echo "* load average at end: $(cut -d' ' -f1-3 /proc/loadavg)" >> "$out"
echo "results: $out"
