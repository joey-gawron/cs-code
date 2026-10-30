#!/usr/bin/env bash
# The memory budget table of the real app (docs/MEMORY.md): idle (no folder), folder open, 20 documents, + a language server, + a debug session,
# + a terminal, + a remote session (loopback cscode-server), each measured after 15 s of settling, with the child processes and after an idle trim.
#
#   tools/profile/budget.sh [--server typescript]     # results: .scratch/profile/budget-<time>.jsonl
#
# The debug stage needs node and js-debug (CSCODE_JSDEBUG or tools/fetch-js-debug.sh); it is skipped otherwise.
set -euo pipefail
# shellcheck disable=SC1091
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
out="$scratch/budget-$(date +%Y%m%d-%H%M%S).jsonl"
dotnet "$(host_dll jit)" budget "$@" | tee "$scratch/budget-last.log" | sed -n 's/^RESULT //p' > "$out"
echo "results: $out"
