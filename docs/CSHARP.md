# C# in CsCode (stage 5)

What CsCode does for C# (and F#/VB test projects), how it was checked on an Apple Silicon Mac (macOS, .NET SDK 10.0.403, 18 cores, 64 GB),
and what is left. Brief: `docs/briefs/lang-5-csharp.md`.

| Area | What there is | Where |
|---|---|---|
| Language servers | **Roslyn** (Microsoft.CodeAnalysis.LanguageServer, what VS Code's C# extension runs; the default) and csharp-ls (the lighter alternative), one of them at a time (`cscode.lsp.serverChoice`, **C#: Select Language Server…**) | `src/CsCode.Lsp/CSharp/`, `BuiltinLanguageServers.cs` |
| Library and generated code | go to definition into the BCL / NuGet packages and into source generators' output opens **read-only virtual documents** (`String.cs [decompiled]`, `RegexGenerator.g.cs [generated]`), with highlighting, hover and go to definition inside | Core `VirtualDocuments.cs`, `CSharp/ExternalDocuments.cs` |
| Test Explorer | xUnit v3, VSTest (as before), now also **Microsoft.Testing.Platform** projects (MSTest runner, NUnit runner, TUnit) and **F# / VB** test projects; run and debug | `src/CsCode.Testing/Providers/DotNet/` |
| Debugging | netcoredbg; breakpoints now bind on macOS (they never did for a project behind a symbolic link) | `src/CsCode.Dap/Session/SourcePathAliases.cs` |
| Code actions on save | `editor.codeActionsOnSave`'s `source.organizeImports` (**Organize Usings**: Roslyn's remove unnecessary usings, then its sort) and `source.fixAll` (**Fix All in Document**: each analyzer warning's document-wide fix all), built from Roslyn's own actions; also Shift+Alt+O and the palette | `src/CsCode.Lsp/CSharp/RoslynSourceActions.cs` |
| Tasks | the .NET tasks.json template has a **`watch`** task (`dotnet watch run`, hot reload) tracked through `$dotnet-watch` | `src/CsCode.Dap/Tasks/ProblemMatchers.cs` |
| NuGet | **NuGet: Add / Update / Remove Package…**, **NuGet: Restore Packages** | `src/CsCode.App/DotNet/` |

## 1. The two servers

### Roslyn (the default)

`roslyn-language-server` from nuget.org (`dotnet tool install --global roslyn-language-server --prerelease`), or the
`Microsoft.CodeAnalysis.LanguageServer` executable of an extracted package (the alternative the definition also looks for). A development checkout
fetches 5.13.0-1.26514.10 with `tools/fetch-roslyn-ls.sh` (MIT, Microsoft, built from dotnet/roslyn d82b7f7; SHA-512 pinned; the user agreed to
the download on 2026-11-01). Everything below was checked against that build: its `--help`, the method names in its assemblies, and its traffic
(the protocol notes, with source links into dotnet/roslyn at that commit and into vscode-csharp, are in the session notes of this stage).

* **Launch:** `--stdio --logLevel Information --extensionLogDirectory ${cscodeLogsDir}/roslyn --telemetryLevel off --clientProcessId ${cscodePid}`.
  Telemetry defaults to `all` in Roslyn, so it is switched off. `${cscodeLogsDir}` (CsCode's logs directory) and `${cscodePid}` are new launch
  variables (`LaunchVariables.ExpandArguments`). One server per **solution**: the root is the outermost marked directory (`RootStrategy.Outermost`).
* **Opening the code:** Roslyn loads nothing by itself. After `initialized` the client sends `solution/open {solution: uri}` with the solution at
  the root, or `project/open {projects: [uri…]}` with every project below it (build output and tool folders skipped, at most 2000) when there is
  none. Several solutions: a quick pick, remembered per workspace (`csharp.roslyn.solutions` in workspace state); **C#: Open Solution…**
  (`csharp.openSolution`) asks again and restarts the server.
* **Ready:** `workspace/projectInitializationComplete` (no params). The `[client]` log line gives the time it took; the status bar says
  "C#: CsCode.slnx loaded"; the loading itself shows as work-done progress ("Loading CsCode.slnx…").
* **Restore:** this build has no "project needs restore" request any more (`workspace/_roslyn_projectNeedsRestore` is gone from source and
  assemblies). The server runs `dotnet restore` itself after loading (setting `projects.dotnet_enable_automatic_restore`, on by default) and
  reports it as work-done progress ("Restore", "Restoring X.csproj"), which CsCode shows like any server's progress. **NuGet: Restore Packages**
  runs `dotnet restore` as a task (section 5).
* **Source-generated documents:** `roslyn-source-generated://<project>/<hint name>?documentId=…` locations; their text comes from LSP 3.18's
  `workspace/textDocumentContent {uri}` → `{text}` (`sourceGeneratedDocument/_roslyn_getText` is gone from this build).
* **Diagnostics** are pull only, and Roslyn registers several providers (syntax, compiler semantics, analyzers…), each answering for its own
  identifier. The client used to keep one identifier (the last registered): Roslyn's compiler errors never showed. It now pulls every registered
  identifier and publishes each under an owner of its own (`lsp:roslyn#<identifier>`), so one provider's report never replaces another's
  (`LanguageClient.Server.cs`).
* **Client-side commands** Roslyn's answers name (its `executeCommandProvider` lists none of them; `RoslynCommands.cs`, registered by
  `RoslynExtension` through the per-server extension pattern):
  `roslyn.client.nestedCodeAction` (a code action that is a menu: a quick pick of its `NestedCodeActions`, then `codeAction/resolve`),
  `roslyn.client.fixAllCodeAction` ("Fix All: …": a pick of the scope among `FixAllFlavors`, then `codeAction/resolveFixAll`),
  `roslyn.client.completionComplexEdit` (override and partial-method completions: the edit, then the caret at `newOffset`).
  `roslyn.client.peekReferences` (code lens) was already mapped by the editor. **Toasts** (`window/_roslyn_showToast`) show as messages.
* **Code actions on save** (`editor.codeActionsOnSave`, stage 1's will-save participant). Roslyn's code actions are only `quickfix`,
  `refactor`, `refactor.extract` and `refactor.inline`, and it ignores `context.only`: neither `source.organizeImports` nor `source.fixAll`
  matches anything it answers. CsCode builds both from Roslyn's own actions (`RoslynSourceActions.cs`), offered only when a source kind is
  asked for (never in the light bulb):
  * `source.organizeImports.roslyn`, **Organize Usings**: Roslyn's "Remove unnecessary usings" (the IDE0005 fix, which removes every
    unnecessary using of the file), then its "Sort Usings" refactoring (System first unless `.editorconfig`'s
    `dotnet_sort_system_directives_first` says otherwise), both asked over the using directives. IDE0005 needs no severity: Roslyn offers the
    fix at its default (hidden) one. Also Organize Imports (Shift+Alt+O) and **C#: Organize Usings**.
  * `source.fixAll.roslyn`, **Fix All in Document**: for each analyzer diagnostic of warning or error severity in the file (IDE…, CA…, a
    NuGet analyzer's; not the compiler's CS…, which `dotnet format` leaves too, nor suggestions), one id after another in document order,
    Roslyn's "Fix All:" of its fix for that diagnostic in the Document scope (`codeAction/resolveFixAll`). A rule is fixed on save by raising it
    to a warning in `.editorconfig` (`dotnet_diagnostic.IDE0090.severity = warning`). Also **C#: Fix All in Document**.
  * Each step is a round trip on the text the step before left, so each action is a command that runs its steps (Pyright's organize imports
    is a command too). On save an edit is applied only before the participant's deadline (`editor.codeActionsOnSaveTimeout`, 750 ms) and only
    to the text it was computed for. Measured on a small project (`RealCSharpServerTests`): organize 60–70 ms and a fix all of one id 175 ms;
    the **first** organize after the solution loaded took 0.9–1 s (Roslyn loading its fixers), so the first save may remove the unnecessary
    usings and leave the sorting to the next one, unless the timeout is raised.
  * Found on the way: `codeAction/resolveFixAll` fails ("Sequence contains no elements") when its range also holds an action whose fix all
    lacks the asked scope (Sort Usings' has only Project and Solution). So each fix all is resolved at its own diagnostic's range, and the
    diagnostics are pulled from Roslyn for the current text before each id (`textDocument/diagnostic` without an identifier answers every
    source).
  * Sorting on format (VS Code's `dotnet.formatting.organizeImportsOnFormat`) is Roslyn's own setting in CsCode,
    `"csharp|formatting.dotnet_organize_imports_on_format": true`: Roslyn's full-document formatting then sorts the usings (checked).

  ```json
  "[csharp]": { "editor.codeActionsOnSave": { "source.fixAll": "explicit", "source.organizeImports": "explicit" } }
  ```

### csharp-ls (the alternative)

`csharp-ls --features metadata-uris` (0.28.0, `dotnet tool install --global csharp-ls`). The flag matters: without it, go to definition on a
symbol of a referenced assembly answers nothing. One server per project (the nearest `.sln`/`.slnx`/`.csproj`).

### Choosing

`cscode.lsp.serverChoice` is a new, generic setting: which built-in runs where several serve one language, by group
(`{"csharp": "csharp"}` runs csharp-ls). Unset, the group's default runs: Roslyn (the user's decision after the evaluation in section 3). An
explicit `cscode.languageServers.<id>.enabled` still wins. The palette command **C#: Select Language Server…** writes it to user settings; the
change applies at once (the other server is stopped). With Roslyn not installed, opening a C# file says how to install it or switch to csharp-ls.

## 2. Library and generated code (read-only virtual documents)

A Core contract (additive, `src/CsCode.Core/Text/VirtualDocuments.cs`): one URI scheme for code that is not a file of the workspace,
`cscode-virtual://<provider>/<kind>/<id>/<file name>`, served by `IVirtualDocumentProvider`s through `IVirtualDocumentService`. Core's
`VirtualDocumentService` is also that scheme's `IFileSystemProvider`, so the editor opens such a document like a file: read-only (writes,
deletes and renames are refused), coloured by its file name's language. The tab is titled `String.cs [decompiled]` (kinds: `decompiled`,
`metadata`, `library source` for SourceLink / embedded PDB sources, `generated`, `expanded`). Bounded: the service caches the last 16 texts (8 M
characters in all); a document lives in the editor as long as a view shows it; the window's session restore skips these tabs.

**One scheme for every read-only document** (decided in the C# merge, 2026-11-02). Stage 2 (Rust) had built a second one, `lsp-readonly:`
(`ReadOnlyDocuments`: a server extension puts text into a 32-entry store that is the scheme's file system), for Expand Macro and the syntax
tree / HIR / MIR views. Core's design was kept and Rust's store became one of its providers (`results`; `Put` is unchanged for extensions):

* it is a Core contract, so any module can serve such documents, not only LSP extensions;
* text is pulled from its producer when a document is opened, so library and generated code (which the server can serve again) never needs a
  copy kept in advance, and a document the cache dropped is fetched again while its server knows it; pushed results fit as a provider that
  holds them (`IVirtualDocumentService.Invalidate` drops the cached copy when one is put again at the same URI);
* the cache is bounded by size as well as count;
* the tab title says what the document is (`vec!.rs [expanded]` next to `String.cs [decompiled]`), the window's session restore skips them,
  and a document a server named is synced back to that server under its own URI (hover and go to definition inside library code).

Library, decompiled, generated and macro-expansion documents therefore open the same way: one scheme, one file system, one title rule.

The LSP module serves `lsp`: a location a server answers (definition, declaration, implementation, references…) is turned into a virtual URI
when the server's source says it is library or generated code (`ExternalDocuments`, the newest 1024 names remembered):

| Server | Location it answers | Kind | Text from |
|---|---|---|---|
| csharp-ls | `csharp:/<project>.csproj/decompiled/System.String.cs` | decompiled | `csharp/metadata {textDocument: {uri}}` → `{projectName, assemblyName, symbolName, source}` |
| Roslyn | `file:` under `$TMPDIR/MetadataAsSource/<session>/DecompilationMetadataAsSourceFileProvider/<id>/String.cs` | decompiled | the file |
| Roslyn | `…/MetadataAsSource/<session>/PdbSourceDocument…/<id>/File.cs` (embedded source; SourceLink needs C# Dev Kit, not in this package) | library source | the file |
| Roslyn | `roslyn-source-generated://<project>/RegexGenerator.g.cs?…` | generated | `workspace/textDocumentContent` |

The opened virtual document is synced to **the server that named it, under the server's own URI** (`didOpen` with `csharp:…` or the temp
file's `file:` URI; routing ignores roots for it), so hover and go to definition work inside it (checked: hover inside Roslyn's `String.cs`).
Limits: a generated file does not refresh when its generator's input changes (Roslyn's `workspace/textDocumentContent/refresh` is not
handled; reopen it); diagnostics a server publishes for a `csharp:` document are not shown.

## 3. Evaluation: csharp-ls and Roslyn on CsCode's own solution

`CSharpServerEvaluation` (`tests/CsCode.Lsp.Tests`, runs with `CSCODE_CSHARP_EVAL=1`; results in `.scratch/eval/csharp-*.json`) drives each
server through the built-in definition, the real process factory and CsCode's client, the way the app runs it. CsCode.slnx has 40 projects.
The file is `src/CsCode.Testing/Providers/DotNet/DotNetTestProvider.cs` with two lines added: a type error (`int wrong = "text";`, CS0029) and a
private field named against the repository's `.editorconfig` naming rule (`dotnet_naming_rule.private_fields_underscore.severity = suggestion`).
Measured 2026-11-01 while five other agents built on the same Mac (load average 7–9): timings are indicative, not benchmarks.

| | csharp-ls (as CsCode roots it: the project) | csharp-ls rooted at the solution | Roslyn (the solution) |
|---|---|---|---|
| server root | `src/CsCode.Testing/` | repository | repository |
| running after open | 0.4 s | 0.3 s | 0.5 s |
| projects loaded (Roslyn's "ready") | – | – | 3.4 s |
| first diagnostic (CS0029) after open | 2.8 s | 8.4 s | 4.6 s |
| memory of the server's process tree, after loading / after the requests below | 292 / 346 MB | 832 / 868 MB | 662 / 827 MB |
| completion after `_info.` (cold / warm median / warm p90 of 20) | 192 / 14 / 20 ms | 135 / 6 / 10 ms | 92 / 11 / 15 ms |
| rename of a private field | 4 edits, 167 ms | 4 edits, 130 ms | 4 edits, 141 ms |
| diagnostics on the file | CS0029 | CS0029 | CS0029, IDE1006 ×3 (Information), IDE0051 (Hint) |
| `.editorconfig` severity (IDE1006 = suggestion) | not reported (no IDE analyzers) | not reported | **honoured** (Information) |
| code actions on the error | 13 (Introduce constant ×4, Extract method, Extract local function, Introduce parameter ×6, Convert to raw string) | same | 7, nested menus (Introduce constant, Extract method, Extract local function, Convert to raw string, **Fix All**, Introduce parameter ×2) |
| refactorings on a method name | Move static members, Extract base class | same | Move static members, **Use block body (+ Fix All)**, Extract base class, Suppress or configure issues |
| go to definition into the BCL (`ConcurrentDictionary`) | `ConcurrentDictionary.cs [decompiled]`, 217 ms | 197 ms | `ConcurrentDictionary.cs [decompiled]`, 305 ms |
| go to a source generator's output (`[GeneratedRegex]`) | the declaration (no generated documents) | same | `RegexGenerator.g.cs [generated]` |

Neither offers a fix for CS0029 itself (both list refactorings at the range). Analyzer diagnostics: Roslyn runs the IDE analyzers and honours
`.editorconfig` severities; csharp-ls 0.28 reports compiler diagnostics only. Both load the projects the solution references and go to library
code; only Roslyn shows generated code.

**Roslyn is the default** (recommended by this evaluation; the user's decision, 2026-11-02). It is what VS Code's C# extension runs, it reports
analyzer and `.editorconfig` diagnostics, shows source-generated code, offers Fix All, and at the same scope (a whole solution) it was faster to
the first diagnostic than csharp-ls with comparable memory. csharp-ls stays available as the lighter choice for one project at a time (≈ 300 MB)
through **C#: Select Language Server…**. The costs: Roslyn ships as a prerelease .NET tool that changes often (two protocol changes since the
VS Code C# extension's documentation: restore and generated text), and needs the .NET 10 runtime.

## 4. Debugging (netcoredbg on macOS arm64)

### Why the real end-to-end test never stopped at its breakpoint

`RealDebuggerEndToEndTests` (Panels.Debug) builds its sample in the temp directory, `/var/folders/…`, a symbolic link to
`/private/var/folders/…`. The compiler writes the **physical** source path into the portable PDB (checked with System.Reflection.Metadata on the
test's own PDB: `/private/var/folders/…/Program.cs`), and netcoredbg 3.2.0-1092 binds an absolute breakpoint path only when it equals a PDB
document path as a string (its `ModulesSources::ResolveBreakpoint`; no realpath, no `sourceFileMap`, upstream issue #78). The breakpoint was sent
as `/var/folders/…`, stayed unverified, and the program ran to its end. Linux passes because `/tmp` is no link there. The Dap tests had been fixed
by building at the physical path (dbg-16 in the macOS audit); the panel test never was, and the product had the same bug for any project opened
through a linked folder.

**Fix (product, all platforms):** a netcoredbg quirk (`NetCoreDbgAdapterQuirks.BindsPhysicalSourcePaths`) makes the session send breakpoint paths
with every link resolved and map the adapter's paths in stack frames back under the folder as the editor opened it (`SourcePathAliases`: one
pair per translated directory, so a step into another file of the project maps back too). A path without links is sent unchanged.
`RealDebuggerEndToEndTests` now passes on this Mac (3/3), and was removed from `docs/handoff/known-issues.md` and the load-sensitive list.
Ruled out with evidence (session notes): architecture (adapter, dbgshim and runtime are all arm64), dbgshim 9.0.13 against runtime 10.0.14
(a mismatch fails loudly), code signing and entitlements (.NET 10's debugger transport needs no `task_for_pid`).

### The debug matrix (real netcoredbg 3.2.0-1092, macOS arm64, 2026-11-01)

From `RealNetCoreDbgTests` and `RealNetCoreDbgMatrixTests` (`tests/CsCode.Dap.Tests`, `Category=RealAdapter`), and the panels' end to end.

| Feature | Result |
|---|---|
| Line breakpoints | ✅ stop, verified; a project under a linked folder binds since the fix above |
| Conditional breakpoints | ✅ |
| Hit-count breakpoints, logpoints | ❌ not supported by netcoredbg (reported unsupported, docs/DAP.md) |
| Exception breakpoints (thrown / user-unhandled) | ✅ stop on the throw with exception info |
| Async call stacks | ⚠️ physical stack only: `<InnerAsync>d__0.MoveNext()` then runtime frames (`ExecutionContext.RunInternal`, `AsyncStateMachineBox.MoveNext`, timer frames); the awaiting callers (`OuterAsync`, `Main`) are not shown — netcoredbg has no async logical stacks |
| Locals: primitives, strings | ✅ |
| Locals: collections | ⚠️ the value is the type (`{System.Collections.Generic.List<int>}`, no `Count = 3`: no `[DebuggerDisplay]`), the children are the raw fields (`_items`, `_size`, `Count = 3`, an `Item` indexer shown as `TargetParameterCountException`); the elements are under `_items` (`[0] = 1`…). Dictionaries likewise (`_entries`, `Count = 2`) |
| Locals: records | ✅ properties (`X = 3`, `Y = 4`, plus `EqualityContract`) |
| Locals: LINQ queries | ⚠️ the iterator's fields (`_source`, `_predicate`, `_selector`, `_current`); no "Results View" |
| Evaluate | ✅ members, arithmetic, indexers (`list.Count + point.X` = 6, `map["two"]` = 2); ❌ lambdas (`list.Where(x => x > 1)`: "Parameter not implemented") |
| Set variable | ✅ (`counter` set to 42 in Locals; the program printed 42) |
| Threads | ✅ named threads (`matrix-worker`, `.NET TP Worker`…), listed once the adapter has seen them: at the first stop after `Thread.Start` only the main thread was listed |
| Attach (`request: attach`, `processId`) | ✅ stops at a breakpoint in the attached program |
| Exit code | ❌ always 0 on macOS arm64 (netcoredbg issue #256; the tests accept 0 on a Mac) |

## 5. Test Explorer: Microsoft.Testing.Platform and F#

Checked with real projects (MSTest 4.5.2 runner, NUnit 4.6.1 + NUnit3TestAdapter 6.3.1 runner, TUnit 1.78.0, all on MTP 2.5.2 with
Microsoft.Testing.Extensions.TrxReport 2.5.2; an F# xUnit v3 3.2.2 project), `RealTestingPlatformTests` (`Category=RealTools`).

* **Detection.** An MSTest (`EnableMSTestRunner`) or NUnit (`EnableNUnitRunner`) runner project sets `IsTestingPlatformApplication` but **not**
  `IsTestProject`: before this stage such projects were not detected at all. A project is a test project when either is true; it runs on MTP when
  `IsTestingPlatformApplication` is true and it is not xUnit v3 (whose native runner CsCode already used). Framework: `mtp.mstest`, `mtp.nunit`,
  `mtp.tunit` from the package references. `.fsproj` and `.vbproj` are detected like `.csproj`.
* **Runs.** Build, then `<exe> --list-tests json` (MTP 2.3+: uid, namespace, type, method, and a file and line for MSTest and TUnit), then
  `<exe> --report-trx --filter-uid <uids…>`: a method's uids are all its data rows. The filters were checked per framework: MSTest's
  `--filter FullyQualifiedName=…` works (a data row's name has no arguments); NUnit's runner parses `--filter` with the adapter's own parser,
  which rejects the escaped parenthesis CsCode's VSTest filter uses for parameterized cases ("Unexpected Eof"); TUnit takes only
  `--treenode-filter /*/Ns/Class/Method`. `--filter-uid` works for all three, so it is used; the native filters are the fallback for a
  platform too old to list. The TRX is read as for VSTest (class and method; a data row is a case). A project without the TRX extension is read
  from `--output detailed` instead.
* **Debugging** launches the test executable under netcoredbg with the uid filter and `--output detailed` and reads the results from the
  console. Not with `--report-trx`: since MTP 2.4 the TRX extension runs the tests in a **child test host** (observed: the launched process only
  waits for a child `dotnet exec`), where the debugger never stops. Checked for MSTest and NUnit: the debugger stops at a breakpoint in the test
  and the result is reported.
* **F# / VB.** No scanner reads them, so discovery builds the project and asks its runner: xUnit v3's `-list full/json` (`Class`, `Method`:
  F# names as compiled, `Samples.FsTests.adds two numbers`, nested modules `Outer+Inner`), MTP's `--list-tests json`, else
  `dotnet test --list-tests` (display names). An F# test's file and line come from `FSharpTestScanner` (a test attribute, then the next
  `let`/`member`), which also gives the editor's glyphs and lenses in `.fs` files.
* **Symbolic links.** MSBuild is now given the project's physical path (`MsbuildPath`): a build of the same project through `/var/…` and through
  `/private/var/…` (a terminal's `dotnet build` uses the physical path) were two projects to MSBuild, whose incremental clean deleted the other's
  outputs (`runtimeconfig.json` went missing and nothing ran).

## 6. Tasks and NuGet

* **`dotnet watch`.** The tasks.json template written with a new .NET launch.json has `build` and now `watch` (`dotnet watch run --project …`,
  `isBackground`, `"problemMatcher": "$dotnet-watch"`). `$dotnet-watch` is `$msCompile` plus dotnet watch's own error lines
  (`dotnet watch ❌ file(1,44): error CS1002: …`) with background patterns taken from SDK 10's real output: busy from the start; a cycle begins
  with `Files updated:` / `Restart is needed`; it ends with `Waiting for changes`, `… changes applied in 260ms.`, `Unable to apply changes due to
  compilation errors.` or `Fix the error to continue`. So the task can be a `preLaunchTask` and is followed in Run Task.
* **NuGet: Add Package…** picks the project (the active file's, else the only one, else a pick), searches the folder's own package sources
  (`dotnet nuget list source --format short`, i.e. every nuget.config above the folder and the user's) through each source's v3
  `SearchQueryService`, offers the packages with their downloads, then their versions, and runs `dotnet add <project> package <id> --version <v>`
  as a task in a terminal. **Update Package…** picks among the project's references (`dotnet list package --format json`) and a version;
  **Remove Package…** runs `dotnet remove package`; **Restore Packages** runs `dotnet restore`. Sources that need credentials are not searched (no
  credential provider): they are listed in the NuGet output channel.

## 7. Seen in the real application (2026-11-01, macOS arm64)

`CSharpRealAppTests` (`tests/CsCode.App.Tests/RealApp`) runs the real composition in a window (`RealAppHost`) and saves a screenshot of each
check in `.scratch/screenshots`:

| Check | Result |
|---|---|
| CsCode's own repository opens with **Roslyn**: "C#: CsCode.slnx loaded" in the status bar; F12 on `ConcurrentDictionary` | ✅ `ConcurrentDictionary.cs [decompiled]`, read-only, highlighted, with Roslyn's reference lenses (`csharp-roslyn-decompiled.png`) |
| Go to Implementation on a `[GeneratedRegex]` method | ✅ `RegexGenerator.g.cs [generated]` (`csharp-roslyn-generated.png`) |
| The same with **csharp-ls** | ✅ `ConcurrentDictionary.cs [decompiled]` (`csharp-csharp-decompiled.png`); no generated documents |
| An MSTest-runner and an NUnit-runner project (never built before) in the Test Explorer: discovered, Run All, Debug of one test each | ✅ results as expected; each debug run stopped at the breakpoint in the test and reported Passed after Continue (`csharp-test-explorer-*.png`) |
| F5 on a console project under the temp folder (behind `/var` → `/private/var`), launch.json with a `build` preLaunchTask | ✅ the build ran in its task terminal, netcoredbg stopped at the breakpoint, reported on the file as opened (`csharp-f5-breakpoint.png`) |
| UI budgets while Roslyn loads CsCode's solution (typing into the open file every 30 ms until 5 s after the solution loaded; load average 7.4) | ✅ dispatcher p50 0.01 ms, p99 4.0 ms, max 19.2 ms (budget 16 / 50 ms); a keystroke's edit p99 0.9 ms, max 4.2 ms (budget 20 ms) |

The Test Explorer check found two things fixed on the way: a runner project that was never restored has no `IsTestingPlatformApplication`
yet (the packages' props set it), so `EnableMSTestRunner` / `EnableNUnitRunner` / a TUnit reference count too; and a debugged MTP program
printed the platform's telemetry banner (the debug launch now passes `TESTINGPLATFORM_TELEMETRY_OPTOUT` like runs do).

## 8. Deferred (open after the merge of every stage)

* **`dotnet new`: New Project / New Item** — the brief's SHOULD item moved to stage 7 (the New Project page), which owns project creation and
  the template listing (scope change, 2026-11-01). New Item can reuse that listing once stage 7 is merged.
* **.NET tasks through `ITaskProvider`** (`dotnet build`, `test`, `run`, `clean` per project with `$msCompile`) — stage 2's (Rust) contract is on
  main since the C# merge (`CargoTaskProvider` is the example); the .NET provider is open.

Stage 2's per-server extension pattern was cherry-picked from the Rust branch (its commits "Add the per-server LSP extension pattern…" and
"Let server extensions answer server-to-client requests…", at the Rust session's suggestion) and Roslyn is built on it: `RoslynExtension`
(`src/CsCode.Lsp/CSharp/`) with `RoslynSession` as its per-server state and `RoslynCommands` registered through the extension host. One fix
to the pattern came with it: an extension's notification or request handler now receives messages **without params** (Roslyn's
`workspace/projectInitializationComplete` has none, and StreamJsonRpc matched no handler), told to the Rust session, which shipped the same
fix with in-order delivery. Since the C# merge both stages run on main's copy of the pattern: `ServerExtensions.CreateBuiltins()` lists
`RustAnalyzerExtension` and `RoslynExtension`.

## 9. Limits

* Roslyn's `workspace/configuration` sections (`csharp|…`, `dotnet|…`) are answered from settings of the same name: VS Code's C# settings
  (`dotnet.*`) are not mapped, so Roslyn runs with its defaults (decompilation on). `--csharpDesignTimePath` is not passed.
* Roslyn's light bulb "Fix All: Remove unnecessary usings" → Document fails on a using line (the `resolveFixAll` bug above: Sort Usings is at the
  same range); Project and Solution work. Code actions on save are Roslyn's only: csharp-ls was not checked for source actions.
* `workspace/textDocumentContent/refresh` is not handled (a generated document opened before its generator reruns is stale).
* Razor and Blazor still have highlighting only (STRETCH, not started).
* NuGet: no credential providers, no package icons or README, no "outdated" view.

## 10. Tests

```
tools/fetch-roslyn-ls.sh                                                       # Roslyn into .scratch/tools/roslyn-ls (once)
dotnet tests/CsCode.Lsp.Tests/bin/Debug/net10.0/CsCode.Lsp.Tests.dll -class CsCode.Lsp.Tests.RealCSharpServerTests      # both servers, real
dotnet tests/CsCode.Lsp.Tests/bin/Debug/net10.0/CsCode.Lsp.Tests.dll -class CsCode.Lsp.Tests.CSharpServerTests          # no server
CSCODE_CSHARP_EVAL=1 dotnet tests/CsCode.Lsp.Tests/bin/Debug/net10.0/CsCode.Lsp.Tests.dll -class CsCode.Lsp.Tests.CSharpServerEvaluation
dotnet tests/CsCode.Panels.Testing.Tests/bin/Debug/net10.0/CsCode.Panels.Testing.Tests.dll -class CsCode.Panels.Testing.Tests.Providers.RealTestingPlatformTests
dotnet tests/CsCode.Dap.Tests/bin/Debug/net10.0/CsCode.Dap.Tests.dll -class CsCode.Dap.Tests.RealNetCoreDbgMatrixTests -class CsCode.Dap.Tests.RealNetCoreDbgTests
dotnet tests/CsCode.Panels.Debug.Tests/bin/Debug/net10.0/CsCode.Panels.Debug.Tests.dll -class CsCode.Panels.Debug.Tests.RealDebuggerEndToEndTests
dotnet tests/CsCode.App.Tests/bin/Debug/net10.0/CsCode.App.Tests.dll -class CsCode.App.Tests.RealApp.CSharpRealAppTests       # the real-app checks
```

Each real suite skips with its reason when its tool is missing (Roslyn not fetched, csharp-ls not installed, netcoredbg not fetched, the test
framework packages not restorable).
