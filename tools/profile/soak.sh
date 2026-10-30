#!/usr/bin/env bash
# Soaks (docs/MEMORY.md, MUST 4/5/7).
#
#   tools/profile/soak.sh [--minutes N]          # the REAL app (headless, mixed scenario) for N minutes (default 12) in its own process:
#                                                #   live-heap and RSS slope per iteration and per hour with 95 % intervals, plateau by thirds,
#                                                #   CSV/JSON under .scratch/soak/
#   tools/profile/soak.sh --tests [--minutes N]  # the long variants of tests/CsCode.MemoryTests (Core + app scenarios), N minutes EACH
set -euo pipefail
# shellcheck disable=SC1091
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
minutes=12
tests=false
while [[ $# -gt 0 ]]; do
  case "$1" in
    --minutes) minutes="$2"; shift 2 ;;
    --tests) tests=true; shift ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
if $tests; then
  CSCODE_SOAK=1 CSCODE_SOAK_MINUTES="$minutes" dotnet test "$root/tests/CsCode.MemoryTests" --filter "Category=Soak&FullyQualifiedName~Long" \
    --logger "console;verbosity=detailed"
  exit
fi
log="$scratch/soak-$(date +%Y%m%d-%H%M%S).log"
dotnet "$(host_dll jit)" soak --minutes "$minutes" | tee "$log"
echo "log: $log"
