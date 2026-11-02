# Sourced by tools/fetch-lsp-tools.sh and tools/fetch-debugpy.sh: the shared tool venv at .scratch/tools/python/venv (git-ignored).
# Homebrew's (and Debian's) Python is "externally managed" (PEP 668), so CsCode's Python tools never go into the system site-packages:
# they go into this venv, made with whatever python3 is on PATH (PYTHON=/path/python3 picks another one).
#
#   cscode_python_venv <root>                  # prints the venv's directory, creating it when missing
#   cscode_pip_install <venv> <requirements>   # pinned, hash-checked, wheels only, no dependency resolution
#
# Downloads are cached in .scratch/tools/python/pip-cache, not the user's pip cache, so the sample projects' own .venv reuse them.

cscode_python_venv() {
  local root="$1"
  local venv="$root/.scratch/tools/python/venv"
  local python="${PYTHON:-python3}"
  if [[ ! -x "$venv/bin/python" && ! -x "$venv/Scripts/python.exe" ]]; then
    command -v "$python" >/dev/null 2>&1 || { echo "python3 not found (set PYTHON=/path/to/python3)" >&2; return 2; }
    echo "creating the tool venv $venv with $("$python" --version 2>&1)" >&2
    "$python" -m venv "$venv" >&2
  fi
  printf '%s\n' "$venv"
}

cscode_pip_install() {
  local venv="$1" requirements="$2"
  local python="$venv/bin/python"
  [[ -x "$python" ]] || python="$venv/Scripts/python.exe"
  PIP_CACHE_DIR="$(dirname "$venv")/pip-cache" PIP_DISABLE_PIP_VERSION_CHECK=1 \
    "$python" -m pip install --quiet --require-hashes --only-binary=:all: --no-deps -r "$requirements"
}
