#!/usr/bin/env bash
# Managed heap snapshot of a running CsCode (docs/MEMORY.md "Finding growth"): a .gcdump (type statistics + the object graph, small, safe to take
# from a live process — it triggers one full blocking GC) and its top types as text.
#
#   tools/profile/gcdump.sh PID [NAME]           # -> .scratch/profile/gcdump-NAME-<time>.gcdump + .txt (top 40 types by size)
#   tools/profile/gcdump.sh --compare A.gcdump B.gcdump   # growth between two dumps (types whose size grew), largest first
#
# PID must be a CsCode process YOU started (e.g. one started with `cscode --foreground`, or the profiling host): never another user's process.
set -euo pipefail
# shellcheck disable=SC1091
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
gcdump="$(dotnet_tool dotnet-gcdump)"
if [[ "${1:-}" == "--compare" ]]; then
  a="$("$gcdump" report "$2")"; b="$("$gcdump" report "$3")"
  python3 -I "$root/tools/profile/gcdump-diff.py" <(echo "$a") <(echo "$b")
  exit
fi
pid="${1:?usage: gcdump.sh PID [NAME]}"
name="${2:-$pid}"
out="$scratch/gcdump-$name-$(date +%Y%m%d-%H%M%S).gcdump"
"$gcdump" collect -p "$pid" -o "$out"
"$gcdump" report "$out" | head -60 > "${out%.gcdump}.txt"
echo "dump: $out"
echo "top types: ${out%.gcdump}.txt"
