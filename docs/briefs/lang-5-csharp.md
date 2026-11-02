# Stage brief — 5. C#

**User request (2026-11-01):** "Lets break this up into stages/languages: … 5. C# …" (overview: `docs/briefs/languages.md`). Needs only
stage 1 (formatter choice and code actions on save). Task auto-detection comes from stage 2, if it has been merged.

**Owns:** `src/CsCode.Lsp/**` (a Roslyn language server definition and its extension class; csharp-ls's metadata request), a read-only
virtual document scheme for decompiled, metadata and source-generated code (additive Core contract plus editor and shell support),
`src/CsCode.Dap/**` (netcoredbg on macOS arm64, .NET task provider, `dotnet watch`), `src/CsCode.Testing/Providers/DotNet/**`
(Microsoft.Testing.Platform, F#), shell wiring in `src/CsCode.App/**` (solution choice, NuGet commands), tests next to each,
`tools/fetch-roslyn-ls.sh`, `docs/CSHARP.md`, and the regenerated `docs/LANGUAGES.md`.

## What is already there (verify, don't rebuild)
* csharp-ls is the built-in server (root markers `*.sln`, `*.slnx` and `*.csproj`). netcoredbg is the debugger (types `coreclr` and legacy
  `netcoredbg`; no hit-count breakpoints or logpoints, docs/DAP.md).
* The .NET test provider handles xUnit v3 and VSTest, with TRX results. `$msCompile`. VS Code's C# snippets are built in.
* Pieces that already expect Roslyn: `CodeLensCommands` maps `roslyn.client.peekReferences`
  (`src/CsCode.Editor/Features/CodeLensCommands.cs:28`), and `DotnetLaunchEnvironment.KnownDotnetServers` lists
  `Microsoft.CodeAnalysis.LanguageServer` (`src/CsCode.Lsp/Runtime/DotnetLaunchEnvironment.cs:39`).
* Razor (`.cshtml`, `.razor`) has highlighting only (`docs/LANGUAGES.md:177`).
* **Known issue:** `RealDebuggerEndToEndTests` never stops at its breakpoint on macOS arm64 (`docs/handoff/known-issues.md:29`).
* docs/TESTING.md gaps: projects that only use Microsoft.Testing.Platform (MSTest runner, NUnit, TUnit without the VSTest adapter) and F#
  test projects are not handled. A `dotnet watch` task cannot be tracked (docs/TERMINAL.md).

## Toolchain on this Mac (checked 2026-11-01)
.NET SDK 10.0.403 in `~/.dotnet` (export `DOTNET_ROOT` and `PATH` in tool shells), csharp-ls 0.28.0 and ilspycmd in `~/.dotnet/tools`,
netcoredbg in `.scratch/tools/netcoredbg`. **The Roslyn language server is not installed.** Find its official distribution route (the VS
Code C# extension downloads a per-RID `Microsoft.CodeAnalysis.LanguageServer` package from a Microsoft feed) and its licence, then ask
the user with the source, version and size before fetching. Record the digest in `tools/fetch-roslyn-ls.sh`.

## MUST
1. **Roslyn language server as a built-in alternative to csharp-ls.** Check every flag and method name against dotnet/roslyn's
   `LanguageServer` sources at the fetched version.
   * **Launch:** stdio, a log level, and a log directory under CsCode's logs.
   * **Opening the code:** after `initialized`, send `solution/open` with the solution the root markers find (a quick pick when there are
     several, remembered per workspace), or `project/open` with the project files.
   * **Ready:** the server counts as ready when project initialization completes (Roslyn's notification).
   * **Restore:** answer its request with `dotnet restore`, shown with progress.
   * **Source-generated documents:** open them through Roslyn's request for their text.
   * **Default server:** which one is the default is the user's call after MUST 4.
2. **Go to definition into library code.** csharp-ls returns metadata locations whose text comes from its `csharp/metadata` request; Roslyn
   returns decompiled or SourceLink files. Check both servers' behaviour.
   * Both open as read-only virtual documents, with highlighting and navigation inside them.
   * The title is like `String.cs [decompiled]`.
   * They reuse the same scheme as source-generated files.
3. **Test Explorer.**
   * **Microsoft.Testing.Platform-only projects** (MSTest runner, NUnit, TUnit): run with `--report-trx`, filter per framework (check each
     one's filter options), and debug by launching the test executable.
   * **F# test projects:** detect `.fsproj` and discover through the runner's test listing.
4. **Evaluation of csharp-ls against Roslyn** on this repository (CsCode itself is a large real solution), recorded in `docs/CSHARP.md`:
   * time to first diagnostics, memory, and completion latency;
   * rename, refactorings and code fixes offered;
   * analyzers and `.editorconfig` severities, source generators, and go to library code.

   End with a recommendation for the default and ask the user.
5. **netcoredbg on macOS arm64:** find why the real panel test never stops, then fix it or document the cause with evidence. Run the full
   debug matrix on this Mac and record it:
   * breakpoints (line, conditional) and exceptions;
   * async call stacks;
   * Locals for collections, records and LINQ;
   * evaluate, set variable, threads and attach.

## SHOULD
* `codeActionsOnSave` with Roslyn's fix-all and organize-usings kinds, through stage 1's participant; check and document them.
* **.NET tasks** through stage 2's `ITaskProvider`: `dotnet build`, `test`, `run` and `clean` per project, with `$msCompile`.
* **Hot reload:** a `dotnet watch` task template whose background begin and end patterns let the task be tracked.
* **NuGet:**
  * Add / Update / Remove Package commands: search through the NuGet v3 search API on the sources in the user's `nuget.config`, then
    pick a version.
  * The change runs `dotnet add package` / `dotnet remove package` as a task.
  * A Restore command.
* **`dotnet new`:** New Project and New Item from a quick pick of the installed templates.

## STRETCH (ask the user before starting)
A Solution view (projects, references and packages in a tool window, modelled on Visual Studio 2022's Solution Explorer); Razor and Blazor
through Roslyn's Razor support (check how recent versions ship it).

## Threading & isolation (binding)
00-common applies. Roslyn on a large solution is the heaviest server CsCode will run. The hostile-server budgets must hold while it loads
CsCode's own solution. Memory and time to ready go in the evaluation. Virtual documents are bounded and closed with their last view.

## Done when
In the real app on this Mac, with the user's chosen default server:
* CsCode's own solution loads.
* F12 on a BCL type opens readable library code, and a source-generated file opens.
* An MSTest-runner project and an NUnit project run and debug in the Test Explorer.
* F5 stops at a breakpoint under netcoredbg (or the cause is documented with evidence).

Also: `docs/CSHARP.md` has the evaluation and the debug matrix. The real-tool suites pass or skip with their reasons. Committed without
attribution trailers.
