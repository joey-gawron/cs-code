#!/usr/bin/env bash
# Run keys through real Neovim (and CsCode.Vim, once built with the engine) and print both results.
#   tools/vim/lab.sh 'foo ‸bar\nbaz' 'dw' ['set sw=2' ...]
# The ‸ marks the cursor; \n and \t are expanded. Keys use Vim notation (<Esc>, <C-w>, <lt> ...).
# Requires nvim (CSCODE_NVIM, ~/.nix-profile/bin/nvim or PATH). Build once with: dotnet build tools/vim/VimLab
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec dotnet run --no-build --project "$here/VimLab" -- "$@"
