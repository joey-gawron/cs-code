#!/usr/bin/env bash
# Downloads the official .NET packs that the self-contained publish (tools/publish-app.sh, build/Publish.props) needs, and checks each one
# against its pin. The download is NuGet's own: `dotnet restore` of the App with the publish properties, from nuget.org (nuget.config), into
# the normal NuGet cache; restore also verifies the packages' signatures. Then every .nupkg in the cache must have the pinned size, SHA-512
# (the packageHash nuget.org publishes in its catalog; checked against it when pinned, 2026-10-29) and SHA-256.
#
#   tools/fetch-publish-packs.sh [--aot] [--verify-only] [--print-version]
#
#   --aot            also the NativeAOT compiler packs (only for the evaluation in docs/SESSIONS.md; the installed app does not use them)
#   --verify-only    do not restore; only check what is in the cache
#   --print-version  print the pinned runtime version and exit (tools/publish-app.sh passes it to the publish: build/Publish.props)
#
# tools/publish-app.sh runs this before every publish and does not publish when a pack differs from its pin. The restore asks for exactly the
# pinned version (build/Publish.props pins the runtime, apphost, crossgen2 and ILCompiler packs), whatever the SDK bundles.
#
# On a Nix-provided dotnet: bash .scratch/notes/with-nix-env.sh tools/fetch-publish-packs.sh
set -euo pipefail

repo=$(cd "$(dirname "$0")/.." && pwd)
cache=${NUGET_PACKAGES:-$HOME/.nuget/packages}
version=10.0.12
aot=0
restore=1
while [ $# -gt 0 ]; do
  case "$1" in
    --aot) aot=1; shift ;;
    --verify-only) restore=0; shift ;;
    --print-version) echo "$version"; exit 0 ;;
    -h|--help) sed -n '2,16p' "$0"; exit 0 ;;
    *) echo "fetch-publish-packs.sh: unknown argument '$1'" >&2; exit 2 ;;
  esac
done

# id version size sha512(base64, as nuget.org publishes it) sha256(hex)
pins=(
  "microsoft.netcore.app.runtime.linux-x64 $version 40083503 FWJ8ixsfc+snzowYyAUlRGHehPjH9xtmLg0owWDV1Ju9dUeZf+OKuBagE+E/flME5Hhx7rObgcSVTXQak/wvGg== 8869e2fb974bc810ffe40b8fb8e18b7ffb1d1a03e52a094c804668c4d793b1ae"
  "microsoft.netcore.app.host.linux-x64 $version 5564040 toj0lPRN7oDJe02OyTYDBkRW6mUWRn8LeUQWA7o5Rjnbt+7eJUB8ATlwKubVqDYnuOIyAkQMoDwmDC3gdB1yZg== b62638d76d67a03e7f1f905f64eba792a4ff1c1ef42875b1ad8f8f8ddea8da87"
  "microsoft.netcore.app.crossgen2.linux-x64 $version 12411276 hBrbpFloNh3BlCDLxPff3ElYmGUuQM2ZTKlYRtGXYak9e+0nDMSmtQcxApMf06f8M4mGyEsGCSscW0BnuJuE2g== 1c9527057c5db49d87a5ae38d092dc302d6f1ff8aea6a03755d5fe0e067f33db"
  "microsoft.netcore.app.ref $version 7188850 uN98mMdrujRLQdIBUdx55aTcdktf24k4RP39uJW+BSR9MctOk0Urpmi+svP2Kj8w7YsSQuBregxTsREl/Gm6KA== bf6e0a1fa7b5ce6a9bbfb7191d2c3151c2d6f812eafabee833fd000c1293b872"
)
aot_pins=(
  "runtime.linux-x64.microsoft.dotnet.ilcompiler $version 13843064 EQ+HtSVgpCYFcKjcQPDhQazwnuy8AeeSoTudz65Kl1/8B7U5JDQ6tmV8uFhT94tXMnEs5XMoaYXcLOsBcgf8gg== 99287bda0371a735b38fcdaef0f5bf780107c36148ebc61527939274108c03d1"
  "microsoft.netcore.app.runtime.nativeaot.linux-x64 $version 26334943 mgW2qXcts+ADITMrHQKhSc+OBZq/AjS3z8ERazD6WIr9AxoDwlVIDg+tf1a7f2g6QgQ33ZqFKPOSOTRLKusAoA== 30a349428fa9960c42889a3788627e530a22d1e22e75020209d10c952fdf386f"
)
[ "$aot" = 1 ] && pins+=("${aot_pins[@]}")

restore_with() {
  dotnet restore "$repo/src/CsCode.App/CsCode.App.csproj" --nologo -v:q -r linux-x64 -p:SelfContained=true -p:CsCodeNuGetRuntimePacks=true -p:CsCodePinnedRuntimeVersion="$version" "$@" \
    --disable-build-servers --artifacts-path "$repo/.scratch/publish-artifacts/restore-linux-x64"
}
if [ "$restore" = 1 ]; then
  echo "Restoring the publish packs from nuget.org into $cache"
  restore_with -p:PublishReadyToRun=true -p:PublishReadyToRunComposite=true
  [ "$aot" = 1 ] && restore_with -p:PublishAot=true -p:PublishReadyToRun=false
fi

bad=0
for pin in "${pins[@]}"; do
  read -r id version size sha512 sha256 <<<"$pin"
  f="$cache/$id/$version/$id.$version.nupkg"
  if [ ! -f "$f" ]; then echo "MISSING  $id $version ($f)"; bad=1; continue; fi
  got_size=$(stat -c %s "$f")
  got512=$(openssl dgst -sha512 -binary "$f" | base64 -w0)
  got256=$(sha256sum "$f" | cut -d' ' -f1)
  if [ "$got_size" = "$size" ] && [ "$got512" = "$sha512" ] && [ "$got256" = "$sha256" ]; then
    echo "ok       $id $version  $size bytes  sha256 $got256"
  else
    echo "MISMATCH $id $version: size $got_size (pinned $size), sha512 $got512 (pinned $sha512), sha256 $got256 (pinned $sha256)"
    bad=1
  fi
done
exit "$bad"
