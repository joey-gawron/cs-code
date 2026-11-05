#!/bin/sh
# Usage: eval "$(tools/dev-env.sh)"
# Points pkg-config at tree-sitter and libvterm from nixpkgs, which a Mac has
# neither of (harfbuzz and fontconfig come from MacPorts or Homebrew).
for p in $(nix build --no-link --print-out-paths nixpkgs#tree-sitter nixpkgs#libvterm-neovim); do
  [ -d "$p/lib/pkgconfig" ] && pc="$pc:$p/lib/pkgconfig"
done
echo "export PKG_CONFIG_PATH=\"\${PKG_CONFIG_PATH:+\$PKG_CONFIG_PATH}${pc}\""
