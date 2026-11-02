#!/usr/bin/env bash
# Fetches the C++ test frameworks the C++ test providers' real-tool tests build against: GoogleTest (BSD-3-Clause, github.com/google/googletest),
# Catch2 v3 (BSL-1.0, github.com/catchorg/Catch2) and doctest (MIT, github.com/doctest/doctest), into .scratch/tools/cpp-test-frameworks/,
# and builds and installs each there with CMake, so a test's temp CMake project finds them with find_package (CMAKE_PREFIX_PATH) and no test
# ever touches the network:
#
#   tools/fetch-cpp-test-frameworks.sh            # pinned versions, skipped when already installed
#   tools/fetch-cpp-test-frameworks.sh --force    # extract and build again
#   CPP_TEST_FRAMEWORKS_ARCHIVES=/dir tools/fetch-cpp-test-frameworks.sh   # use archives you already have (named as below): NO download
#   CPP_TEST_FRAMEWORKS_DEST=/some/dir tools/fetch-cpp-test-frameworks.sh # install under /some/dir instead
#
# Each archive is GitHub's source tarball of the pinned tag. Its size and SHA-256 are PINNED (recorded when the archives were first fetched on
# 2026-11-01; GitHub publishes no digest for tag archives): an archive is refused unless its digest matches, and its entries are listed and
# checked (no absolute paths, no '..') before anything is extracted. Result, per framework: <dest>/<name>-<version>/install/{include,lib}
# with the CMake package config (GTest::gtest_main, Catch2::Catch2WithMain, doctest::doctest), and <dest>/prefix-path listing the install
# directories for CMAKE_PREFIX_PATH. Building needs cmake and a C++17 compiler; Ninja is used when it is installed.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dest="${CPP_TEST_FRAMEWORKS_DEST:-${root}/.scratch/tools/cpp-test-frameworks}"
force=0
[[ "${1:-}" == "--force" ]] && force=1

# name | version | archive URL | size | SHA-256
frameworks=(
  "googletest|1.17.0|https://github.com/google/googletest/archive/refs/tags/v1.17.0.tar.gz|885595|65fab701d9829d38cb77c14acdc431d2108bfdbf8979e40eb8ae567edf10b27c"
  "Catch2|3.8.1|https://github.com/catchorg/Catch2/archive/refs/tags/v3.8.1.tar.gz|1197623|18b3f70ac80fccc340d8c6ff0f339b2ae64944782f8d2fca2bd705cf47cadb79"
  "doctest|2.4.12|https://github.com/doctest/doctest/archive/refs/tags/v2.4.12.tar.gz|2188849|73381c7aa4dee704bd935609668cf41880ea7f19fa0504a200e13b74999c2d70"
)

command -v cmake >/dev/null || { echo "fetch-cpp-test-frameworks: cmake is required" >&2; exit 2; }
command -v tar >/dev/null || { echo "fetch-cpp-test-frameworks: tar is required" >&2; exit 2; }
generator=()
command -v ninja >/dev/null && generator=(-G Ninja)
jobs="$( (sysctl -n hw.ncpu 2>/dev/null || nproc 2>/dev/null || echo 4) | tr -d ' ')"
jobs=$(( jobs > 4 ? jobs / 2 : jobs )) # other builds share the machine

mkdir -p "$dest"
prefixes=()
for entry in "${frameworks[@]}"; do
  IFS='|' read -r name version url size sha256 <<< "$entry"
  dir="${dest}/${name}-${version}"
  prefixes+=("${dir}/install")
  if [[ -f "${dir}/install/INSTALLED" && $force -eq 0 ]]; then
    echo "${name} ${version} already installed: ${dir}/install"
    continue
  fi
  archive="${name}-v${version}.tar.gz"
  tmp="$(mktemp -d "${dest}/.fetch-${name}.XXXXXX")"
  trap 'rm -rf "$tmp"' EXIT

  if [[ -n "${CPP_TEST_FRAMEWORKS_ARCHIVES:-}" ]]; then
    [[ -f "${CPP_TEST_FRAMEWORKS_ARCHIVES}/${archive}" ]] || { echo "fetch-cpp-test-frameworks: ${CPP_TEST_FRAMEWORKS_ARCHIVES}/${archive} does not exist" >&2; exit 2; }
    echo "Using the local archive ${CPP_TEST_FRAMEWORKS_ARCHIVES}/${archive} (no download)"
    cp "${CPP_TEST_FRAMEWORKS_ARCHIVES}/${archive}" "$tmp/$archive"
  else
    command -v curl >/dev/null || { echo "fetch-cpp-test-frameworks: curl is required (or set CPP_TEST_FRAMEWORKS_ARCHIVES)" >&2; exit 2; }
    echo "Downloading ${name} ${version} (${size} bytes) ..."
    curl -fsSL -A "cscode-fetch" --max-filesize $((size * 2)) "$url" -o "$tmp/$archive"
  fi

  actual_size="$(wc -c < "$tmp/$archive" | tr -d ' ')"
  # Lower-cased with tr, not ${actual,,}: macOS ships bash 3.2.
  actual="$( (sha256sum "$tmp/$archive" 2>/dev/null || shasum -a 256 "$tmp/$archive") | awk '{print $1}' | tr '[:upper:]' '[:lower:]')"
  if [[ "$actual" != "$sha256" ]]; then
    echo "fetch-cpp-test-frameworks: ${archive}: SHA-256 mismatch (got ${actual}, expected ${sha256}) - refusing to extract" >&2
    exit 1
  fi
  echo "SHA-256 verified: ${actual} (${actual_size} bytes, expected ${size})"

  # Safe extraction: list first and reject absolute paths and '..' components before unpacking anything.
  if tar -tzf "$tmp/$archive" | grep -Eq '(^/|(^|/)\.\.(/|$))'; then echo "fetch-cpp-test-frameworks: ${archive} contains unsafe paths" >&2; exit 1; fi
  mkdir -p "$tmp/src"
  tar -xzf "$tmp/$archive" -C "$tmp/src" --strip-components 1

  options=(-DCMAKE_BUILD_TYPE=Debug -DCMAKE_CXX_STANDARD=17 "-DCMAKE_INSTALL_PREFIX=${tmp}/install" -DCMAKE_INSTALL_LIBDIR=lib)
  case "$name" in
    googletest) options+=(-DBUILD_GMOCK=ON -DINSTALL_GTEST=ON) ;;
    Catch2)     options+=(-DCATCH_INSTALL_DOCS=OFF -DCATCH_INSTALL_EXTRAS=ON -DBUILD_TESTING=OFF) ;;
    doctest)    options+=(-DDOCTEST_WITH_TESTS=OFF -DDOCTEST_WITH_MAIN_IN_STATIC_LIB=OFF) ;;
  esac
  echo "Building ${name} ${version} ..."
  cmake -S "$tmp/src" -B "$tmp/build" "${generator[@]}" "${options[@]}" > "$tmp/configure.log" 2>&1 || { cat "$tmp/configure.log" >&2; exit 1; }
  cmake --build "$tmp/build" --parallel "$jobs" > "$tmp/build.log" 2>&1 || { tail -50 "$tmp/build.log" >&2; exit 1; }
  cmake --install "$tmp/build" > "$tmp/install.log" 2>&1 || { cat "$tmp/install.log" >&2; exit 1; }
  # CMake writes the install prefix into the package configs only as a relative path, so the tree can be moved into place.
  echo "${version}" > "$tmp/install/INSTALLED"

  rm -rf "$dir"
  mkdir -p "$dir"
  mv "$tmp/install" "$dir/install"
  mv "$tmp/$archive" "$dir/$archive"
  rm -rf "$tmp"
  trap - EXIT
  echo "Installed ${name} ${version} -> ${dir}/install"
done

(IFS=';'; echo "${prefixes[*]}") > "${dest}/prefix-path"
echo "CMAKE_PREFIX_PATH for the test projects: $(cat "${dest}/prefix-path")"
