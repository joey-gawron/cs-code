#!/usr/bin/env bash
# Linux only. Source me (do not execute) when `dotnet` comes from Nix on a non-NixOS Linux host. On macOS it does nothing: Avalonia there
# uses the system's own frameworks, with no X11 or fontconfig libraries to point at (Nix on a Mac included).
#
# The Nix dotnet runs against Nix's glibc, so it cannot see the distro's libfontconfig/libX11/... that SkiaSharp and
# Avalonia's X11 backend dlopen at runtime. This points LD_LIBRARY_PATH at the matching builds already in /nix/store.
# Not needed (and harmless) with a distro/Microsoft dotnet install.
#
#   source tools/nix-dev-env.sh && dotnet test CsCode.slnx
#
# Library list = what libSkiaSharp.so + Avalonia.X11 need transitively.
_libs=(
  libfontconfig.so.1 libfreetype.so.6 libexpat.so.1 libpng16.so.16 libz.so.1 libbz2.so.1.0 libbrotlidec.so.1 libbrotlicommon.so.1
  libX11.so.6 libxcb.so.1 libXau.so.6 libXdmcp.so.6 libICE.so.6 libSM.so.6 libuuid.so.1
  libXcursor.so.1 libXext.so.6 libXi.so.6 libXrandr.so.2 libXrender.so.1 libXfixes.so.3
  libGL.so.1 libEGL.so.1 libGLdispatch.so.0 libxkbcommon.so.0
)

if [[ "$(uname -s)" == Darwin ]]; then
  echo "nix-dev-env: macOS needs no library path, nothing to do" >&2
  unset _libs
  return 0 2>/dev/null || exit 0
fi

if [[ ! -d /nix/store ]]; then
  echo "nix-dev-env: no /nix/store, nothing to do" >&2
  return 0 2>/dev/null || exit 0
fi

_dirs=()
for _lib in "${_libs[@]}"; do
  # newest-looking match first; skip glibc/gcc runtime dirs (the dotnet binary already uses its own)
  _hit=$(ls -d /nix/store/*/lib/"$_lib" 2>/dev/null | grep -vE '/[^/]*-(glibc|gcc)[^/]*/' | tail -1)
  [[ -n "$_hit" ]] && _dirs+=("$(dirname "$_hit")")
done

_joined=$(printf '%s\n' "${_dirs[@]}" | awk '!seen[$0]++' | paste -sd: -)
export LD_LIBRARY_PATH="${_joined}${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"

# Fonts: reuse the host's fontconfig setup (Nix fontconfig has no fonts of its own on a non-NixOS host).
[[ -f /etc/fonts/fonts.conf ]] && export FONTCONFIG_FILE=/etc/fonts/fonts.conf

unset _libs _dirs _lib _hit _joined
