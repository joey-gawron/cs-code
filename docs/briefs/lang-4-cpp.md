# Stage brief — 4. C++

**User request (2026-11-01):** "Lets break this up into stages/languages: … 4. C++ …" (overview: `docs/briefs/languages.md`). Starts after
stage 3 (C) is merged: CMake, CTest, clangd's extension, document links and the native debugging windows are already there.

**Owns:** new `src/CsCode.Testing/Providers/Cpp/**` (GoogleTest, Catch2, doctest), `src/CsCode.Dap/**` (C++ adapter quirks and templates),
`src/CsCode.Lsp/Manager/BuiltinLanguageServers.cs` (clangd selector additions), shell wiring in `src/CsCode.App/**`, tests next to each,
the C++ sections of `docs/C-CPP.md`, and the regenerated `docs/LANGUAGES.md`.

## What is already there (verify, don't rebuild)
* Everything stage 3 lists, and its output: CMake configure / build / targets, CTest discovery and runs, clangd's extension (Switch
  Header/Source, inactive regions, AST, file status), `documentLink`, and the C debugging matrix.
* clangd features that are generic and already work: type hierarchy and call hierarchy (docs/NAVIGATION.md), inlay hints, semantic tokens,
  code lens, and clang-tidy fixes as code actions.
* CUDA (`cuda-cpp`) and Objective-C++ (`objective-cpp`) have highlighting but no server (docs/LANGUAGES.md matrix).
* The Test Explorer has no "Run All Tests" lens above a class or fixture (docs/TESTING.md).

## Toolchain on this Mac (checked 2026-11-01)
As stage 3: Apple clang / clangd 21.0.0, CMake 4.4.2, Ninja 1.13.2, Apple LLDB with libc++ data formatters, lldb-dap, CodeLLDB. GoogleTest
and Catch2 are not installed. The real-tool tests fetch them into their temp projects with CMake `FetchContent` at pinned tags (network:
ask once at the start of the stage), or skip with a reason.

## MUST
1. **GoogleTest provider.**
   * **Finding executables:** CMake targets that link `GTest::gtest` / `gtest_main` (from the File API), and CTest tests registered by
     `gtest_discover_tests` (avoid listing them twice).
   * **Discovery:** `<exe> --gtest_list_tests`. For locations, use the JSON listing (`--gtest_output=json:<file>`; check whether the
     installed GoogleTest includes file and line), else scan the source for `TEST`, `TEST_F`, `TEST_P` and `TYPED_TEST`.
   * **Runs:** `--gtest_filter=…` with `--gtest_output=xml:<file>` (or JSON) for results; failures carry file:line.
   * **Build first:** the target is built through the CMake integration before a run.
   * **Debugging** goes through CodeLLDB with the filter.
2. **Catch2 provider (v3; v2 where it costs little).**
   * **Discovery:** `--list-tests --reporter xml`, which gives file and line.
   * **Runs:** `--reporter xml` (or JUnit) for results. Tags show as filters, and debugging works as for GoogleTest.
3. **C++ debugging matrix on this Mac** (CodeLLDB, then lldb-dap), recorded in `docs/C-CPP.md` with evidence; fix what is fixable.
   * **libc++ values** through LLDB's formatters: `std::string`, `vector`, `map`, `unordered_map`, `optional`, `variant`, `unique_ptr` and
     `shared_ptr`.
   * **Types:** dynamic type of polymorphic objects, and templates and lambdas in the call stack.
   * **C++ exceptions:** throw and catch filters; check each adapter's filter names.
   * **Threads:** `std::thread` and `std::jthread`.
   * **Native windows:** Memory on a `std::vector`'s data, and Disassembly of an inlined function.
4. **clangd for `cuda-cpp` and `objective-cpp`:** add them to clangd's selector. Check what clangd does with `.cu` files on a Mac without a
   CUDA SDK, and document it.
5. **C++ snippets** in the bundled-snippets mechanism.

## SHOULD
* doctest provider (`--list-test-cases`, `--reporters=xml`).
* A "Run All Tests" lens above a GoogleTest fixture or Catch2 `TEST_CASE` group (shared Test Explorer code; keep the other providers working).
* C++20 modules with clangd (`--experimental-modules-support`): check with clangd 21 and document what works.
* vcpkg and Conan through CMake presets' `toolchainFile`: check that a preset using them configures; no package-manager UI.

## STRETCH
A "New Class" command that creates a header + source pair; CMake target sources edited from the UI.

## Threading & isolation (binding)
00-common applies. Listing tests runs the test executables: keep it bounded (time and output), on the IO lane, and never during typing.
Re-list only after a build of that target.

## Done when
In the real app on this Mac, a CMake C++ project with GoogleTest and Catch2 tests:
* builds, and lists, runs and debugs both kinds of test (with results and failure locations);
* debugs a program, showing STL containers readably and stopping on a thrown exception.

Also: `.cu` and `.mm` files get clangd. `docs/C-CPP.md` has the C++ matrix. The real-tool suites pass or skip with their reasons. Committed
without attribution trailers.
