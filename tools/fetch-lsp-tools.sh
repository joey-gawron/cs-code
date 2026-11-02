#!/usr/bin/env bash
# Installs the REAL language servers used by the CsCode.Lsp integration tests into .scratch/tools/lsp/ (git-ignored).
# Nothing is installed globally. Idempotent: re-running only does work that is missing.
#
#   tools/fetch-lsp-tools.sh                  # typescript-language-server + typescript (npm)
#   tools/fetch-lsp-tools.sh --with-csharp    # additionally csharp-ls (dotnet tool, needs the .NET SDK + network)
#   tools/fetch-lsp-tools.sh --with-python    # additionally pyright + basedpyright (npm) and Ruff (hash-pinned, into the tool venv)
#
# The tests look for the binaries in {repo}/.scratch/tools/lsp/node_modules/.bin (npm) and {repo}/.scratch/tools/lsp (dotnet tool)
# and SKIP with an explicit reason when they are missing, so running this script is what turns the skips into real runs.
# Ruff goes into .scratch/tools/python/venv (tools/python/venv.sh), its tests look for .scratch/tools/python/venv/bin/ruff.
set -euo pipefail

TS_LS_VERSION="${TS_LS_VERSION:-4.3.3}"
TYPESCRIPT_VERSION="${TYPESCRIPT_VERSION:-5.6.3}"
CSHARP_LS_VERSION="${CSHARP_LS_VERSION:-}"   # empty = latest
PYRIGHT_VERSION="1.1.414"       # npm; MIT
BASEDPYRIGHT_VERSION="1.40.2"   # npm; MIT

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dest="$root/.scratch/tools/lsp"
mkdir -p "$dest"

with_csharp=0 with_python=0
for arg in "$@"; do
  case "$arg" in
    --with-csharp) with_csharp=1 ;;
    --with-python) with_python=1 ;;
    *) echo "unknown argument: $arg" >&2; exit 2 ;;
  esac
done

if ! command -v npm >/dev/null 2>&1; then
  echo "fetch-lsp-tools: npm not found; cannot install typescript-language-server (the TypeScript integration tests will skip)" >&2
else
  bin="$dest/node_modules/.bin/typescript-language-server"
  tsc="$dest/node_modules/typescript/lib/tsserver.js"
  installed_ls=""
  [[ -f "$dest/node_modules/typescript-language-server/package.json" ]] && installed_ls="$(node -p "require('$dest/node_modules/typescript-language-server/package.json').version" 2>/dev/null || true)"
  installed_ts=""
  [[ -f "$dest/node_modules/typescript/package.json" ]] && installed_ts="$(node -p "require('$dest/node_modules/typescript/package.json').version" 2>/dev/null || true)"
  if [[ -x "$bin" && -f "$tsc" && "$installed_ls" == "$TS_LS_VERSION" && "$installed_ts" == "$TYPESCRIPT_VERSION" ]]; then
    echo "typescript-language-server $installed_ls + typescript $installed_ts already installed"
  else
    echo "installing typescript-language-server@$TS_LS_VERSION + typescript@$TYPESCRIPT_VERSION into $dest"
    [[ -f "$dest/package.json" ]] || echo '{"name":"cscode-lsp-tools","private":true,"version":"0.0.0"}' > "$dest/package.json"
    npm install --prefix "$dest" --no-audit --no-fund --loglevel=error "typescript-language-server@$TS_LS_VERSION" "typescript@$TYPESCRIPT_VERSION"
  fi
fi

if [[ "$with_csharp" == 1 ]]; then
  if ! command -v dotnet >/dev/null 2>&1; then
    echo "fetch-lsp-tools: dotnet not found; cannot install csharp-ls" >&2
  elif [[ -x "$dest/csharp-ls" ]]; then
    echo "csharp-ls already installed"
  else
    echo "installing csharp-ls into $dest"
    if [[ -n "$CSHARP_LS_VERSION" ]]; then
      dotnet tool install csharp-ls --tool-path "$dest" --version "$CSHARP_LS_VERSION"
    else
      dotnet tool install csharp-ls --tool-path "$dest"
    fi
  fi
fi

if [[ "$with_python" == 1 ]]; then
  if ! command -v npm >/dev/null 2>&1; then
    echo "fetch-lsp-tools: npm not found; cannot install pyright / basedpyright (their tests will skip)" >&2
  else
    installed_pr="" installed_bpr=""
    [[ -f "$dest/node_modules/pyright/package.json" ]] && installed_pr="$(node -p "require('$dest/node_modules/pyright/package.json').version" 2>/dev/null || true)"
    [[ -f "$dest/node_modules/basedpyright/package.json" ]] && installed_bpr="$(node -p "require('$dest/node_modules/basedpyright/package.json').version" 2>/dev/null || true)"
    if [[ "$installed_pr" == "$PYRIGHT_VERSION" && "$installed_bpr" == "$BASEDPYRIGHT_VERSION" ]]; then
      echo "pyright $installed_pr + basedpyright $installed_bpr already installed"
    else
      echo "installing pyright@$PYRIGHT_VERSION + basedpyright@$BASEDPYRIGHT_VERSION into $dest"
      [[ -f "$dest/package.json" ]] || echo '{"name":"cscode-lsp-tools","private":true,"version":"0.0.0"}' > "$dest/package.json"
      # npm checks each tarball against the registry's integrity (SHA-512) before unpacking it.
      npm install --prefix "$dest" --no-audit --no-fund --loglevel=error --save-exact "pyright@$PYRIGHT_VERSION" "basedpyright@$BASEDPYRIGHT_VERSION"
    fi
    # basedpyright's package also declares the bins `pyright` and `pyright-langserver`, and npm links them to basedpyright: point them back at
    # Pyright's own scripts, or everything that starts "Pyright" from here runs basedpyright.
    ln -sf ../pyright/index.js "$dest/node_modules/.bin/pyright"
    ln -sf ../pyright/langserver.index.js "$dest/node_modules/.bin/pyright-langserver"
    "$dest/node_modules/.bin/pyright" --version
  fi
  # shellcheck source=python/venv.sh
  source "$root/tools/python/venv.sh"
  if venv="$(cscode_python_venv "$root")"; then
    echo "installing Ruff (tools/python/requirements-ruff.txt) into $venv"
    cscode_pip_install "$venv" "$root/tools/python/requirements-ruff.txt"
    "$venv/bin/ruff" --version
  else
    echo "fetch-lsp-tools: no python3 for the tool venv; Ruff not installed (its tests will skip)" >&2
  fi
fi

echo "done. tools in: $dest"
