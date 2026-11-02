#!/usr/bin/env bash
# Fetches Microsoft's debugpy (MIT, github.com/microsoft/debugpy) for the Python debugging and test suites and local development:
#
#   tools/fetch-debugpy.sh                # the pinned bundled copy + debugpy and pytest in the tool venv; skips what is already there
#   tools/fetch-debugpy.sh --force        # unpack the bundled copy again
#   tools/fetch-debugpy.sh --bundled-only # only the bundled copy (no venv)
#   DEBUGPY_WHEEL=/path/debugpy-1.8.22-py2.py3-none-any.whl tools/fetch-debugpy.sh   # a local copy of the pinned wheel: NO download
#   DEBUGPY_DEST=/some/dir tools/fetch-debugpy.sh                                     # install the bundled copy under /some/dir/<version>/
#
# Two things, because CsCode runs debugpy two ways (CsCode.Dap's DebugpyAdapterFactory):
#  1. The BUNDLED copy: the pure-Python wheel (py2.py3-none-any, no compiled speedups, so one copy serves every interpreter and platform)
#     unpacked to .scratch/tools/debugpy/<version>/, run as `<interpreter> <dest>/<version>/debugpy/adapter` when the selected
#     interpreter has no debugpy of its own. The version, size and SHA-256 are PINNED (the same values as CsCode.Dap's DebugpyInstaller,
#     which the app uses after asking the user; the digest is the one PyPI publishes); entries are listed and checked (no absolute paths,
#     no '..', no links) before anything is extracted. Nothing inside the wheel is run by this script.
#  2. debugpy + pytest INSIDE the tool venv (.scratch/tools/python/venv, tools/python/venv.sh), hash-pinned in
#     tools/python/requirements-test.txt: the interpreter the suites select, which has debugpy (`-m debugpy.adapter`) and pytest.
set -euo pipefail

version="1.8.22"
wheel="debugpy-${version}-py2.py3-none-any.whl"
size=5374857
sha256="a9e9d3550e15ca479c59333e90845029190531f0cacfedab3b815a57bd913947"
url="https://files.pythonhosted.org/packages/56/3d/c7dc9f35bc9e22cdb53679f844bee40fd5cff871862f60595869a433400d/${wheel}"

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dest="${DEBUGPY_DEST:-${root}/.scratch/tools/debugpy}/${version}"
force=0 bundled_only=0
for arg in "$@"; do
  case "$arg" in
    --force) force=1 ;;
    --bundled-only) bundled_only=1 ;;
    *) echo "fetch-debugpy: unknown argument: $arg" >&2; exit 2 ;;
  esac
done

if [[ -f "$dest/debugpy/adapter/__main__.py" && $force -eq 0 ]]; then
  echo "bundled debugpy ${version} already present: $dest/debugpy/adapter"
else
  command -v unzip >/dev/null || { echo "fetch-debugpy: unzip is required" >&2; exit 2; }
  mkdir -p "$(dirname "$dest")"
  tmp="$(mktemp -d "$(dirname "$dest")/.fetch-debugpy.XXXXXX")"
  trap 'rm -rf "$tmp"' EXIT
  if [[ -n "${DEBUGPY_WHEEL:-}" ]]; then
    [[ -f "$DEBUGPY_WHEEL" ]] || { echo "fetch-debugpy: DEBUGPY_WHEEL '${DEBUGPY_WHEEL}' does not exist" >&2; exit 2; }
    echo "Using the local wheel ${DEBUGPY_WHEEL} (no download)"
    cp "$DEBUGPY_WHEEL" "$tmp/$wheel"
  else
    command -v curl >/dev/null || { echo "fetch-debugpy: curl is required (or set DEBUGPY_WHEEL)" >&2; exit 2; }
    echo "Downloading ${wheel} (${size} bytes) ..."
    curl -fsSL -A "cscode-fetch" --max-filesize $((size * 2)) "$url" -o "$tmp/$wheel"
  fi
  actual="$( (sha256sum "$tmp/$wheel" 2>/dev/null || shasum -a 256 "$tmp/$wheel") | awk '{print $1}' | tr '[:upper:]' '[:lower:]')"
  if [[ "$actual" != "$sha256" ]]; then
    echo "fetch-debugpy: SHA-256 mismatch (got ${actual}, expected ${sha256}) - refusing to extract" >&2
    exit 1
  fi
  echo "SHA-256 verified: ${actual} ($(wc -c < "$tmp/$wheel" | tr -d ' ') bytes, expected ${size})"
  if unzip -Z1 "$tmp/$wheel" | grep -Eq '(^/|(^|/)\.\.(/|$))'; then echo "fetch-debugpy: wheel contains unsafe paths" >&2; exit 1; fi
  if unzip -Z "$tmp/$wheel" | grep -Eq '^l'; then echo "fetch-debugpy: wheel contains links" >&2; exit 1; fi
  mkdir -p "$tmp/x"; unzip -q "$tmp/$wheel" -d "$tmp/x"
  [[ -f "$tmp/x/debugpy/adapter/__main__.py" ]] || { echo "fetch-debugpy: the wheel has no debugpy/adapter" >&2; exit 1; }
  rm -rf "$dest"
  mv "$tmp/x" "$dest"
  echo "$version" > "$dest/VERSION"
  echo "Installed bundled debugpy ${version} -> $dest/debugpy/adapter"
fi

if [[ $bundled_only -eq 0 ]]; then
  # shellcheck source=python/venv.sh
  source "$root/tools/python/venv.sh"
  venv="$(cscode_python_venv "$root")"
  echo "installing debugpy and pytest (tools/python/requirements-test.txt) into $venv"
  cscode_pip_install "$venv" "$root/tools/python/requirements-test.txt"
  "$venv/bin/python" -c 'import debugpy, pytest; print("venv: debugpy", debugpy.__version__, "pytest", pytest.__version__)'
fi
