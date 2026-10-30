#!/usr/bin/env bash
# Builds CsCode.app for macOS: the self-contained osx publish in Contents/MacOS, Info.plist, and Contents/Resources/cscode.icns made from the
# committed PNGs (tools/macos/make-icns.py). UNTESTED ON macOS: written on Linux (docs/briefs/app-icon-and-install.md, SHOULD); the bundle's
# layout, Info.plist and .icns are checked on Linux (tests/CsCode.App.Tests, MacBundleTests), its start on a Mac is not.
#
#   tools/macos/make-app-bundle.sh [--rid osx-arm64|osx-x64] [--from DIR] [--out DIR] [--no-sign]
#
#   --rid     the macOS runtime (default: this Mac's; osx-arm64 elsewhere)
#   --from    an existing publish for that runtime (default: tools/publish-app.sh --rid <rid>, which restores the osx runtime packs from nuget.org)
#   --out     where CsCode.app is written (default: .scratch/macos); an earlier CsCode.app there is replaced
#   --no-sign skip the ad-hoc signature (codesign --sign -), which Apple silicon needs for a bundle built or changed locally
#
# Install: drag CsCode.app to /Applications (or ~/Applications). Terminal: ln -s /Applications/CsCode.app/Contents/MacOS/cscode ~/bin/cscode (or any
# directory on PATH). The tree-sitter bundle needs a macOS build (tools/treesitter/build-grammars.sh --target <rid> with a macOS compiler); without
# it the app colours with TextMate. Debug adapters are offered for download by the app when a session needs one.
set -euo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
repo=$(cd "$here/../.." && pwd)
rid=""; from=""; out="$repo/.scratch/macos"; sign=1
while [ $# -gt 0 ]; do
    case "$1" in
        --rid) rid=$2; shift 2 ;;
        --from) from=$2; shift 2 ;;
        --out) out=$2; shift 2 ;;
        --no-sign) sign=0; shift ;;
        -h|--help) sed -n '2,17p' "$0"; exit 0 ;;
        *) echo "make-app-bundle.sh: unknown argument '$1'" >&2; exit 2 ;;
    esac
done
if [ -z "$rid" ]; then
    if [ "$(uname -s)" = Darwin ] && [ "$(uname -m)" = x86_64 ]; then rid=osx-x64; else rid=osx-arm64; fi
fi
case "$rid" in osx-x64|osx-arm64) ;; *) echo "make-app-bundle.sh: --rid must be osx-x64 or osx-arm64" >&2; exit 2 ;; esac
command -v python3 >/dev/null || { echo "make-app-bundle.sh: python3 is required (for the .icns)" >&2; exit 1; }

if [ -z "$from" ]; then
    "$repo/tools/publish-app.sh" --rid "$rid"
    from="$repo/.scratch/publish/$rid"
fi
[ -f "$from/cscode" ] && [ -f "$from/cscode.dll" ] || { echo "make-app-bundle.sh: $from is not a CsCode publish" >&2; exit 1; }
# The version the app reports (its assembly version, as cscode.deps.json names it); the publish may not run here (another OS).
version=$(sed -n 's/^ *"cscode\/\([0-9][0-9A-Za-z.+-]*\)": {.*/\1/p' "$from/cscode.deps.json" 2>/dev/null | head -n1)
[ -n "$version" ] || version=0.0.0

mkdir -p "$out"
app="$out/CsCode.app"
tmp="$out/.CsCode.app.tmp"
rm -rf "$tmp"
mkdir -p "$tmp/Contents/MacOS" "$tmp/Contents/Resources"
cp -a "$from/." "$tmp/Contents/MacOS/"
chmod 755 "$tmp/Contents/MacOS/cscode"
python3 -I "$here/make-icns.py" --branding "$repo/assets/branding" "$tmp/Contents/Resources/cscode.icns" >/dev/null
cat > "$tmp/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>CsCode</string>
    <key>CFBundleDisplayName</key>
    <string>CsCode</string>
    <key>CFBundleIdentifier</key>
    <string>local.cscode.CsCode</string>
    <key>CFBundleExecutable</key>
    <string>cscode</string>
    <key>CFBundleIconFile</key>
    <string>cscode</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundleShortVersionString</key>
    <string>$version</string>
    <key>CFBundleVersion</key>
    <string>$version</string>
    <key>LSApplicationCategoryType</key>
    <string>public.app-category.developer-tools</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSPrincipalClass</key>
    <string>NSApplication</string>
</dict>
</plist>
PLIST
printf 'APPL????' > "$tmp/Contents/PkgInfo"
rm -rf "$app"
mv "$tmp" "$app"
if [ "$sign" = 1 ] && command -v codesign >/dev/null 2>&1; then
    codesign --force --deep --sign - "$app"
fi
echo "Built $app ($rid, CsCode $version)"
