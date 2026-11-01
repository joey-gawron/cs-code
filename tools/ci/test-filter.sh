#!/usr/bin/env bash
# Prints the `dotnet test --filter` expression CI runs with (.github/workflows/ci.yml):
#
#   tools/ci/test-filter.sh            every test except the soak and perf suites (Category=Soak, Category=Perf: long, measured by hand) and
#                                      the load-sensitive tests listed in tools/ci/load-sensitive-tests.txt (docs/handoff/known-issues.md)
#   tools/ci/test-filter.sh --only     just those load-sensitive tests (the workflow's non-blocking "load-sensitive" job)
#
# Runs on macOS's bash 3.2. Use it locally the same way: dotnet test tests/CsCode.App.Tests --filter "$(tools/ci/test-filter.sh)"
set -euo pipefail
here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
list="$here/load-sensitive-tests.txt"
only=0
case "${1:-}" in
    "") ;;
    --only) only=1 ;;
    -h|--help) sed -n '2,/^[^#]/p' "$0" | sed -e '/^[^#]/d'; exit 0 ;;
    *) echo "test-filter.sh: unknown argument '$1'" >&2; exit 2 ;;
esac
names=()
while IFS= read -r line || [ -n "$line" ]; do
    line=${line%%#*}
    line=$(printf '%s' "$line" | tr -d '[:space:]')
    [ -n "$line" ] || continue
    case "$line" in *[\(\)\&\|=!~,]*) echo "test-filter.sh: '$line' holds a character a test filter cannot: $list" >&2; exit 1 ;; esac
    names+=("$line")
done < "$list"
[ "${#names[@]}" -gt 0 ] || { echo "test-filter.sh: $list lists no test" >&2; exit 1; }
if [ "$only" = 1 ]; then
    out=""
    for n in "${names[@]}"; do out="${out:+$out|}FullyQualifiedName~$n"; done
    printf '%s\n' "$out"
else
    out="Category!=Soak&Category!=Perf"
    for n in "${names[@]}"; do out="$out&FullyQualifiedName!~$n"; done
    printf '%s\n' "$out"
fi
