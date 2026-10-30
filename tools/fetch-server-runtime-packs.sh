#!/usr/bin/env bash
# Restores (downloads, when they are not in the NuGet cache yet) the .NET runtime packs the self-contained cscode-server bundles need for the
# five supported runtime ids, and checks every package against its pinned digest (docs/REMOTE.md, "Server bundles").
#
#   Microsoft.NETCore.App.Runtime.<rid> and Microsoft.NETCore.App.Host.<rid>, version 10.0.12 (the runtime of SDK 10.0.401), for linux-x64,
#   linux-arm64, osx-x64, osx-arm64 and win-x64, and the targeting pack Microsoft.NETCore.App.Ref. tools/publish-server.sh takes every pack from
#   the NuGet cache, never from the SDK's own packs folder (a distribution may patch those: Nix's give the apphost a /nix/store loader).
#
# Source: nuget.org only (nuget.config), through `dotnet restore` into the normal NuGet cache; the SDK verifies nuget.org's repository
# signature on restore. The pinned values below are nuget.org's own published package hashes (catalog "packageHash", SHA-512, base64) and
# sizes, checked on 2026-10-29; the SHA-256 of each .nupkg is listed for reference. Nothing is executed from the packages.
#
# Usage: tools/fetch-server-runtime-packs.sh   (on a Nix-provided dotnet: bash .scratch/notes/with-nix-env.sh tools/fetch-server-runtime-packs.sh)
set -euo pipefail

repo=$(cd "$(dirname "$0")/.." && pwd)
version=10.0.12
cache=${NUGET_PACKAGES:-$HOME/.nuget/packages}

# id | size | sha512 (base64, nuget.org catalog) | sha256 (hex, reference)
pins=(
  "microsoft.netcore.app.runtime.linux-x64|40083503|FWJ8ixsfc+snzowYyAUlRGHehPjH9xtmLg0owWDV1Ju9dUeZf+OKuBagE+E/flME5Hhx7rObgcSVTXQak/wvGg==|8869e2fb974bc810ffe40b8fb8e18b7ffb1d1a03e52a094c804668c4d793b1ae"
  "microsoft.netcore.app.host.linux-x64|5564040|toj0lPRN7oDJe02OyTYDBkRW6mUWRn8LeUQWA7o5Rjnbt+7eJUB8ATlwKubVqDYnuOIyAkQMoDwmDC3gdB1yZg==|b62638d76d67a03e7f1f905f64eba792a4ff1c1ef42875b1ad8f8f8ddea8da87"
  "microsoft.netcore.app.ref|7188850|uN98mMdrujRLQdIBUdx55aTcdktf24k4RP39uJW+BSR9MctOk0Urpmi+svP2Kj8w7YsSQuBregxTsREl/Gm6KA==|bf6e0a1fa7b5ce6a9bbfb7191d2c3151c2d6f812eafabee833fd000c1293b872"
  "microsoft.netcore.app.runtime.linux-arm64|37570581|wi7jZfGEVTuajiD5Rx/RL5sGRGcHn5d6UU6o/H6IhexDHlVpaPGt5R6JicIcEEietKAYEatuMMzsSnFI26DIbg==|7268839dca1ec677d4c9908b4e52395d4c003c6989550d0b8e965923a4a70036"
  "microsoft.netcore.app.runtime.osx-x64|39034152|iPpHFMcrH8KdkNomgTAX/T3SoBBTWb9iWVIFhHqFfU4lwXo67QK0JbMA9kG53i1VEYMrdbhQwRzoHyAKF4voBg==|ab5c57042afdbadc29d8ddbf7753de2ae9abd983e971da8cf97a2d246ea8f72b"
  "microsoft.netcore.app.runtime.osx-arm64|36679189|8rDt9QoxwXsA6a0tVl9b4fD5my0EZtXvJndCEFESf/H5hSlqvi26RBwfnvyiz3b8wS2pdP8G6H7xqdRJbSVkwg==|2f0e72fdf79ead91d0e1ad4960ea3a9bdeba1b7cb08467e3ead0a427d1fd2e08"
  "microsoft.netcore.app.runtime.win-x64|39968868|Oa/LIiAy6r6+LH+lGjfEkcaw9FZ5ateIhDGXG47vT2icruVFQmA5jOD/r+SRxWWJGzXnOvxnWFo+TEvemVcQ7g==|0f63dee7ca4383cb16848966f81e439399e2058e1671ebe27e5eaf4b200e8691"
  "microsoft.netcore.app.host.linux-arm64|5310725|uEj/w3++nHnuqUu0yVd5Wyd5crjQ9JYGuWRxi8Owki08NaDEv6mM1AwPGkp6+eYibpdSNQ0NPwGU2GJXo0PJ6Q==|bd566dc6e4084ccba0d3f48a0c4085d2b50c00ba0340b65fab32177fd17470a0"
  "microsoft.netcore.app.host.osx-x64|5443267|YWj0a7N/Le5f90ObETvI/TBH8JhNG/Ke2euy1Cu1b872T2G3FWNOfzNgu1zKoSPuKUPr9qtDL3bDRX2NSw8q8Q==|17b4a60a181370cc3939d761e765eb60171939ae9dedfed0fe8c67388519c783"
  "microsoft.netcore.app.host.osx-arm64|4984494|2sYBokxR07f7uX4XyVUZkv3B8319NtJfAspf2iauqo/HrrTu7jRx5bayYwwtohdsSi7W5nzHNqjYW/t2UWi33Q==|adbd6e6ec1911498f431aa97a2d1bba9d159ff60df0d7744cb8f0445744a1e34"
  "microsoft.netcore.app.host.win-x64|5790931|M8J2D1k24eswYJ/DaJdHYbwzHrcg+gWTv5L58FDG2R1n9nOiJ4MWCrhMFtZzbowCwQBmztbAbM7tN49cuqeFiA==|1fbc2fcae33182f41955db3391ecf8a1b4aa047429852651d419b6b16eb4a523"
)

empty=$(mktemp -d "${TMPDIR:-/tmp}/cscode-no-packs.XXXXXX")
trap 'rm -rf "$empty"' EXIT
for rid in linux-x64 linux-arm64 osx-x64 osx-arm64 win-x64; do
  echo "Restoring the $rid runtime packs…"
  # As publish-server.sh does: packs from NuGet only (an empty pack root), so the cache gets exactly what the bundles are built from.
  dotnet restore "$repo/src/CsCode.Server/CsCode.Server.csproj" --nologo -v:q -r "$rid" -p:SelfContained=true \
    -p:NetCoreTargetingPackRoot="$empty" -p:AllowMissingPrunePackageData=true
done

status=0
for pin in "${pins[@]}"; do
  IFS='|' read -r id size sha512 sha256 <<<"$pin"
  file="$cache/$id/$version/$id.$version.nupkg"
  if [ ! -f "$file" ]; then echo "MISSING  $id $version ($file)"; status=1; continue; fi
  actual_size=$(stat -c %s "$file")
  actual512=$(openssl dgst -sha512 -binary "$file" | base64 -w0)
  actual256=$(sha256sum "$file" | cut -d' ' -f1)
  if [ "$actual_size" = "$size" ] && [ "$actual512" = "$sha512" ] && [ "$actual256" = "$sha256" ]; then
    echo "OK       $id $version  $size bytes  sha256 $actual256"
  else
    echo "MISMATCH $id $version: size $actual_size (want $size), sha512 $actual512 (want $sha512), sha256 $actual256 (want $sha256)"
    status=1
  fi
done
exit $status
