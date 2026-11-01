#!/usr/bin/env bash
# Builds CsCode.app for macOS: the self-contained osx publish in Contents/MacOS, Info.plist, and Contents/Resources/cscode.icns made from the
# committed PNGs. On a Mac the .icns comes from iconutil (an .iconset of the PNGs, a missing size scaled by sips from the 1024 px one), the
# Info.plist is checked with plutil -lint and the bundle is signed ad hoc and verified; elsewhere tools/macos/make-icns.py writes the .icns
# and the layout, Info.plist and .icns are checked on Linux (tests/CsCode.App.Tests, PackagingTests). tools/package-macos.sh runs this on a Mac
# with the publish, the remote server bundles and the install of the app and the `cscode` command.
#
#   tools/macos/make-app-bundle.sh [--rid osx-arm64|osx-x64] (--from DIR | --unpinned) [--out DIR] [--server-bundles DIR] [--debug-adapters DIR]
#                                  [--no-sign | --sign-identity ID [--entitlements FILE] [--adapter-entitlements FILE]]
#
#   --rid             the macOS runtime (default: this Mac's; osx-arm64 elsewhere)
#   --from            an existing publish for that runtime
#   --unpinned        publish it now (tools/publish-app.sh --rid <rid> --unpinned) with the osx runtime packs restore picks from nuget.org,
#                     unchecked: only linux-x64's packs are pinned (tools/fetch-publish-packs.sh), so without --from or --unpinned the script refuses
#   --out             where CsCode.app is written (default: .scratch/macos); an earlier CsCode.app there is replaced
#   --server-bundles  the remote server bundles (tools/publish-server.sh's output) to ship in Contents/MacOS/server-bundles, where the app
#                     looks for them next to its program (src/CsCode.Remote, ServerBundles)
#   --debug-adapters  a folder of debug adapters laid out as the app reads them (netcoredbg/, js-debug/<version>/js-debug,
#                     codelldb/<version>/extension: src/CsCode.Dap/Adapters/BundledAdapters.cs) to ship in Contents/Resources/debug-adapters, where
#                     the app looks from its program in Contents/MacOS (there codesign would take a dotted folder such as codelldb/v1.12.3 for a
#                     nested bundle and refuse the app); tools/package-macos.sh assembles it. Each Mach-O file in it is signed ad hoc before the bundle
#   --no-sign         skip the ad-hoc signature (codesign --sign -), which Apple silicon needs for a bundle built or changed locally
#   --sign-identity ID  sign for other Macs instead of ad hoc: ID is a "Developer ID Application: Name (TEAMID)" identity in the keychain (or its
#                     SHA-1). Inside out, each with the hardened runtime (--options runtime) and a secure timestamp: every Mach-O file of the
#                     debug adapters (with --adapter-entitlements), every other file in Contents/MacOS (libraries and assemblies first, then
#                     the apphosts such as cscode-server: codesign wants everything there signed), then the bundle (with --entitlements); never
#                     --deep, which Apple advises against for signing. Notarization is tools/package-macos.sh's (--notarize)
#   --entitlements FILE           the app's entitlements (default packaging/macos/cscode.entitlements: allow-jit, disable-library-validation)
#   --adapter-entitlements FILE   the debug adapters' (default packaging/macos/debug-adapters.entitlements)
#   CODESIGN=PATH     the codesign to run (default codesign; tests put a recording stand-in here)
#
# Install: tools/package-macos.sh --install, or drag CsCode.app to /Applications (or ~/Applications). The tree-sitter bundle needs a macOS
# build (tools/treesitter/build-grammars.sh --target <rid> with a macOS compiler); without it the app colours with TextMate. A debug adapter
# the bundle lacks is offered for download by the app when a session needs one.
set -euo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
repo=$(cd "$here/../.." && pwd)
rid=""; from=""; out="$repo/.scratch/macos"; sign=1; unpinned=0; servers=""; adapters=""
identity="-"; entitlements="$repo/packaging/macos/cscode.entitlements"; adapter_entitlements="$repo/packaging/macos/debug-adapters.entitlements"
codesign=${CODESIGN:-codesign}
while [ $# -gt 0 ]; do
    case "$1" in
        --rid) rid=$2; shift 2 ;;
        --from) from=$2; shift 2 ;;
        --unpinned) unpinned=1; shift ;;
        --out) out=$2; shift 2 ;;
        --server-bundles) servers=$2; shift 2 ;;
        --debug-adapters) adapters=$2; shift 2 ;;
        --no-sign) sign=0; shift ;;
        --sign-identity) identity=$2; shift 2 ;;
        --entitlements) entitlements=$2; shift 2 ;;
        --adapter-entitlements) adapter_entitlements=$2; shift 2 ;;
        -h|--help) sed -n '2,/^[^#]/p' "$0" | sed -e '/^[^#]/d'; exit 0 ;; # the header comment
        *) echo "make-app-bundle.sh: unknown argument '$1'" >&2; exit 2 ;;
    esac
done
if [ -z "$rid" ]; then
    if [ "$(uname -s)" = Darwin ] && [ "$(uname -m)" = x86_64 ]; then rid=osx-x64; else rid=osx-arm64; fi
fi
case "$rid" in osx-x64|osx-arm64) ;; *) echo "make-app-bundle.sh: --rid must be osx-x64 or osx-arm64" >&2; exit 2 ;; esac
if [ -z "$identity" ]; then echo "make-app-bundle.sh: --sign-identity needs an identity (\"Developer ID Application: Name (TEAMID)\")" >&2; exit 2; fi
if [ "$identity" != "-" ]; then
    [ "$sign" = 1 ] || { echo "make-app-bundle.sh: --sign-identity and --no-sign contradict each other" >&2; exit 2; }
    for f in "$entitlements" "$adapter_entitlements"; do [ -f "$f" ] || { echo "make-app-bundle.sh: entitlements file $f not found" >&2; exit 2; }; done
    command -v "$codesign" >/dev/null 2>&1 || { echo "make-app-bundle.sh: --sign-identity needs codesign (Xcode's command line tools: xcode-select --install)" >&2; exit 1; }
fi
# tools/publish-app.sh refuses a runtime whose packs are not pinned unless told (only linux-x64's are): say so before anything is done.
if [ -z "$from" ] && [ "$unpinned" != 1 ]; then
    echo "make-app-bundle.sh: only linux-x64's runtime packs are pinned (tools/fetch-publish-packs.sh): pass --unpinned to publish $rid with the packs restore picks (unchecked), or bundle an existing publish with --from DIR" >&2
    exit 2
fi
if [ -n "$servers" ] && [ ! -f "$servers/manifest.json" ]; then
    echo "make-app-bundle.sh: --server-bundles $servers: no manifest.json there (tools/publish-server.sh writes it)" >&2
    exit 2
fi
if [ -n "$adapters" ] && [ ! -d "$adapters" ]; then
    echo "make-app-bundle.sh: --debug-adapters $adapters is not a directory" >&2
    exit 2
fi
# The .icns: Apple's iconutil on a Mac, else the standard-library writer.
iconutil=0
if command -v iconutil >/dev/null 2>&1 && command -v sips >/dev/null 2>&1; then iconutil=1
else command -v python3 >/dev/null || { echo "make-app-bundle.sh: python3 is required (for the .icns)" >&2; exit 1; }
fi

if [ -z "$from" ]; then
    "$repo/tools/publish-app.sh" --rid "$rid" --unpinned
    from="$repo/.scratch/publish/$rid"
fi
[ -f "$from/cscode" ] && [ -f "$from/cscode.dll" ] || { echo "make-app-bundle.sh: $from is not a CsCode publish" >&2; exit 1; }
# The version the app reports (its assembly version, as cscode.deps.json names it); the publish may not run here (another OS).
version=$(sed -n 's/^ *"cscode\/\([0-9][0-9A-Za-z.+-]*\)": {.*/\1/p' "$from/cscode.deps.json" 2>/dev/null | head -n1 || true) # none: 0.0.0 below
[ -n "$version" ] || version=0.0.0

mkdir -p "$out"
app="$out/CsCode.app"
tmp="$out/.CsCode.app.tmp"
rm -rf "$tmp"
mkdir -p "$tmp/Contents/MacOS" "$tmp/Contents/Resources"
cp -a "$from/." "$tmp/Contents/MacOS/"
chmod 755 "$tmp/Contents/MacOS/cscode"
if [ -n "$servers" ]; then
    mkdir -p "$tmp/Contents/MacOS/server-bundles"
    cp -a "$servers/." "$tmp/Contents/MacOS/server-bundles/"
fi
if [ -n "$adapters" ]; then
    mkdir -p "$tmp/Contents/Resources/debug-adapters"
    cp -a "$adapters/." "$tmp/Contents/Resources/debug-adapters/"
fi

if [ "$iconutil" = 1 ]; then
    # icon_<points>[@2x].png <- cscode-<pixels>.png; a size without its PNG (or with a PNG of another size) is scaled from the 1024 px one.
    iconset="$out/.cscode.iconset"
    rm -rf "$iconset"; mkdir -p "$iconset"
    for entry in 16:16x16 32:16x16@2x 32:32x32 64:32x32@2x 128:128x128 256:128x128@2x 256:256x256 512:256x256@2x 512:512x512 1024:512x512@2x; do
        px=${entry%%:*}; png="$repo/assets/branding/cscode-$px.png"; dest="$iconset/icon_${entry#*:}.png"
        if [ -f "$png" ] && [ "$(sips -g pixelWidth "$png" 2>/dev/null | awk '/pixelWidth/ {print $2}')" = "$px" ]; then cp "$png" "$dest"
        else sips -z "$px" "$px" "$repo/assets/branding/cscode-1024.png" --out "$dest" >/dev/null; fi
    done
    iconutil -c icns -o "$tmp/Contents/Resources/cscode.icns" "$iconset"
    rm -rf "$iconset"
else
    python3 -I "$here/make-icns.py" --branding "$repo/assets/branding" "$tmp/Contents/Resources/cscode.icns" >/dev/null
fi

# Document types: Finder's Open With and drops on the Dock icon offer CsCode for text, source code and folders, as an alternative
# (LSHandlerRank Alternate: it never takes over a type's default app). LSMinimumSystemVersion is the .NET 10 runtime's own (libcoreclr's
# LC_BUILD_VERSION minos, 12.0).
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
    <key>LSMinimumSystemVersion</key>
    <string>12.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSPrincipalClass</key>
    <string>NSApplication</string>
    <key>CFBundleDocumentTypes</key>
    <array>
        <dict>
            <key>CFBundleTypeName</key>
            <string>Text and source code</string>
            <key>CFBundleTypeRole</key>
            <string>Editor</string>
            <key>LSHandlerRank</key>
            <string>Alternate</string>
            <key>LSItemContentTypes</key>
            <array>
                <string>public.text</string>
                <string>public.plain-text</string>
                <string>public.source-code</string>
                <string>public.script</string>
                <string>public.shell-script</string>
                <string>public.json</string>
                <string>public.xml</string>
                <string>public.yaml</string>
                <string>net.daringfireball.markdown</string>
                <string>public.comma-separated-values-text</string>
                <string>public.log</string>
            </array>
        </dict>
        <dict>
            <key>CFBundleTypeName</key>
            <string>Folder</string>
            <key>CFBundleTypeRole</key>
            <string>Editor</string>
            <key>LSHandlerRank</key>
            <string>Alternate</string>
            <key>LSItemContentTypes</key>
            <array>
                <string>public.folder</string>
            </array>
        </dict>
    </array>
</dict>
</plist>
PLIST
printf 'APPL????' > "$tmp/Contents/PkgInfo"
if command -v plutil >/dev/null 2>&1; then plutil -lint -s "$tmp/Contents/Info.plist"; fi
rm -rf "$app"
mv "$tmp" "$app"
# Whether a file is Mach-O (one `file -b` per file: parsing `file`'s list breaks on a universal binary, whose description holds colons).
macho() { case "$(file -b "$1")" in Mach-O*) return 0 ;; *) return 1 ;; esac; }
if [ "$sign" = 1 ] && [ "$identity" != "-" ]; then
    # Developer ID, for other Macs (then notarized by tools/package-macos.sh --notarize): inside out, every Mach-O file with the hardened
    # runtime and a secure timestamp, as notarization requires of each; the bundle last, which seals the rest (Contents/Resources as files).
    sign_one() { # <file> <entitlements>
        "$codesign" --force --sign "$identity" --options runtime --timestamp --entitlements "$2" "$1" \
            || { echo "make-app-bundle.sh: codesign failed for $1" >&2; exit 1; }
    }
    echo "Signing with \"$identity\" (hardened runtime, timestamp)…"
    if [ -d "$app/Contents/Resources/debug-adapters" ]; then
        while IFS= read -r -d '' bin; do macho "$bin" && sign_one "$bin" "$adapter_entitlements"; done \
            < <(find "$app/Contents/Resources/debug-adapters" -type f -print0 | sort -z)
    fi
    # Contents/MacOS holds code only, for codesign: every file there is signed, the assemblies, .json files and remote server bundles too
    # (their signature goes in extended attributes, which ditto keeps), or the bundle's signature fails with "code object is not signed at all
    # In subcomponent: …/Avalonia.Remote.Protocol.dll" (found running this on the real publish; Avalonia's macOS deployment guide signs every
    # file there the same way). Libraries and other files before the executables that load them; the main executable (Contents/MacOS/cscode)
    # is signed with the bundle.
    executable() { # a Mach-O program (not a library: a .dylib or .so by name, or what `file` calls a shared library or bundle)
        case "$1" in *.dylib|*.so) return 1 ;; esac
        macho "$1" && case "$(file -b "$1")" in *executable*) return 0 ;; *) return 1 ;; esac
    }
    while IFS= read -r -d '' bin; do
        [ "$bin" = "$app/Contents/MacOS/cscode" ] && continue
        executable "$bin" || sign_one "$bin" "$entitlements"
    done < <(find "$app/Contents/MacOS" -type f -print0 | sort -z)
    while IFS= read -r -d '' bin; do
        [ "$bin" = "$app/Contents/MacOS/cscode" ] && continue
        executable "$bin" && sign_one "$bin" "$entitlements"
    done < <(find "$app/Contents/MacOS" -type f -print0 | sort -z)
    sign_one "$app" "$entitlements"
    "$codesign" --verify --deep --strict --verbose=2 "$app" || { echo "make-app-bundle.sh: the signed bundle does not verify" >&2; exit 1; }
elif [ "$sign" = 1 ] && command -v codesign >/dev/null 2>&1; then
    # Ad hoc (no identity): enough for this Mac, and what Apple silicon requires of a bundle built or changed locally. A copy downloaded
    # from elsewhere is still stopped by Gatekeeper; that needs a Developer ID signature and notarization (docs/handoff/macos-audit.md, pkg-2).
    # The debug adapters' executables and libraries first, each on its own (in Contents/Resources the bundle's signature seals them as
    # files, it does not sign them), keeping whatever entitlements and flags their own signature had; then the bundle, which seals them.
    if [ -d "$app/Contents/Resources/debug-adapters" ]; then
        find "$app/Contents/Resources/debug-adapters" -type f -print0 | while IFS= read -r -d '' bin; do
            macho "$bin" || continue
            codesign --force --sign - --preserve-metadata=entitlements,flags,runtime "$bin" 2>/dev/null || codesign --force --sign - "$bin"
            codesign --verify --strict "$bin"
        done
    fi
    codesign --force --deep --sign - "$app"
    codesign --verify --deep --strict "$app"
fi
echo "Built $app ($rid, CsCode $version)"
