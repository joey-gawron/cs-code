#!/usr/bin/env bash
# Prints the `dotnet test --filter` expression CI runs with (.github/workflows/ci.yml):
#
#   tools/ci/test-filter.sh            every test except the soak and perf suites (Category=Soak, Category=Perf: long, measured by hand) and
#                                      the load-sensitive tests listed in tools/ci/load-sensitive-tests.txt (docs/handoff/known-issues.md)
#   tools/ci/test-filter.sh --only     just those load-sensitive tests (the workflow's non-blocking "load-sensitive" job)
#   --os macos|linux                   the OS whose list to use (default: this machine's, from uname): a line marked "macos:" or "linux:"
#                                      counts on that OS only, an unmarked line on every OS
#
# Runs on macOS's bash 3.2. Use it locally the same way: dotnet test tests/CsCode.App.Tests --filter "$(tools/ci/test-filter.sh)"
set -euo pipefail
here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
list="$here/load-sensitive-tests.txt"
only=0
case "$(uname -s)" in
    Darwin) os=macos ;;
    Linux) os=linux ;;
    *) os=other ;;
esac
while [ $# -gt 0 ]; do
    case "$1" in
        --only) only=1 ;;
        --os)
            [ $# -gt 1 ] || { echo "test-filter.sh: --os needs macos or linux" >&2; exit 2; }
            os=$2; shift ;;
        -h|--help) sed -n '2,/^[^#]/p' "$0" | sed -e '/^[^#]/d'; exit 0 ;;
        *) echo "test-filter.sh: unknown argument '$1'" >&2; exit 2 ;;
    esac
    shift
done
names=()
while IFS= read -r line || [ -n "$line" ]; do
    line=${line%%#*}
    line=$(printf '%s' "$line" | tr -d '[:space:]')
    [ -n "$line" ] || continue
    case "$line" in
        macos:*|linux:*)
            [ "${line%%:*}" = "$os" ] || continue
            line=${line#*:} ;;
        *:*) echo "test-filter.sh: '$line' has an OS mark other than macos: or linux: ($list)" >&2; exit 1 ;;
    esac
    case "$line" in *[\(\)\&\|=!~,]*) echo "test-filter.sh: '$line' holds a character a test filter cannot: $list" >&2; exit 1 ;; esac
    names+=("$line")
done < "$list"
[ "${#names[@]}" -gt 0 ] || { echo "test-filter.sh: $list lists no test for $os" >&2; exit 1; }
if [ "$only" = 1 ]; then
    out=""
    for n in "${names[@]}"; do out="${out:+$out|}FullyQualifiedName~$n"; done
    printf '%s\n' "$out"
else
    out="Category!=Soak&Category!=Perf"
    for n in "${names[@]}"; do out="$out&FullyQualifiedName!~$n"; done
    printf '%s\n' "$out"
fi
