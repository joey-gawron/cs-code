# Remote editing — `CsCode.Remote` + `CsCode.Server`

VS Code Remote-SSH model: the GUI runs locally; the **workspace** — files, search, file watching, language servers, **terminals, debug adapters
and tasks** — lives on a remote machine reached with the **system `ssh`** client, **inside a Docker container** (`docker exec`) or **in a WSL
distribution** (`wsl.exe`, Windows). The same headless server doubles as the **out-of-process language host** (`cscode.languageHost = process`).
Phases R1–R4 of `docs/briefs/remote.md` and the wave-3 brief (W3) are implemented; what is not is listed under [Limits](#limits-and-known-gaps).

* [Architecture](#architecture) · [Protocol](#protocol) · [Bootstrap](#server-bootstrap) · [Security model](#security-model) · [Threading](#threading-and-isolation)
* [Client services](#client-services) · [Language bridging (R2)](#language-bridging-r2) · [Terminals, debugging and tasks (R3)](#terminals-debugging-and-tasks-r3)
* [Reconnect and persistence (R4)](#reconnect-and-persistence-r4) · [Transports](#transports-ssh-docker-wsl) · [Server bundles](#server-bundles)
* [Process language host](#process-language-host) · [Shell glue](#shell-glue) · [Setup and settings](#setup-and-settings) · [Limits](#limits-and-known-gaps) · [Tests](#tests)

## Architecture

```
 CsCode.App (GUI)                                CsCode.Remote (client, Core only)                         CsCode.Server (cscode-server, headless)
 Explorer / editor / quick open ─┐                                                                         Core + Platform + Lsp, its own data dir
                                 ├─ Platform FileSystemRouter ── ssh: ─► RemoteFileSystem ──┐               fs/* ─► Platform LocalFileSystem (atomic writes, trash)
                                 ├─ Platform FileWatcherRouter ─ ssh: ─► RemoteWatcher ─────┤  JSON-RPC 2.0 watch/* ─► Platform FileWatcher
                                 ├─ WorkspaceService.FindFiles ─ ssh: ─► RemoteSearch ──────┤  Content-Length search/* ─► Platform walker + parallel text scan
 IDocumentManager events ────────┴────────────────────────────────────► DocumentSync ──────┤  over ssh -T  doc/* ─► MirrorDocument ─► IDocumentManager ─► LanguageServerManager
 ILanguageFeatureRegistry ◄── bridge providers ◄── LanguageBridge ◄────────────────────────┤  (or a local lang/registered ◄── MirroringFeatureRegistry
 IDiagnosticService ◄──────────────────────────── DiagnosticsMirror ◄─────────────────────┤   child process) diag/publish ◄── IDiagnosticService (coalesced)
 IWindowService / INavigationService / Output ◄── UiForwarding ◄──────────────────────────┤                window/* nav/* output/* ◄── ClientWindow/Navigation/OutputService
 ITerminalService ── ITerminalBackendProvider ─► RemoteTerminals ─┐ chan/* (offsets, acks) ├─ term/* ─► Terminal module's resolver + a PTY (UnixPtyHost)
 IDebugService ── stream descriptor + path mapper ─► RemoteDebugging ┤ ────────────────────┤─ dap/* ─► Dap's adapter factories + DapTransportFactory (gdb, js-debug, netcoredbg)
 ITaskService (decorated) ─────────────────────► RemoteTaskService ─┘                      └─ task/* ─► Dap's TaskService (tasks.json, problem matchers → diag/publish)
 Find in Files engine ── ITextSearchProvider ──► RemoteSearch ── search/text ─────────────────────────► parallel scan on the server, matches only
```
Persistent mode (R4, the default over ssh/docker/wsl): what ssh runs is a small **relay** (`cscode-server --stdio --persist <id>`) that connects to
the session's **daemon** (`--daemon <id>`, detached, listening on a private Unix socket) and starts it when needed. The daemon holds everything
above across dropped connections for `remote.ssh.reconnectGracePeriod` seconds.

* `CsCode.Remote` (client library, Avalonia-free, references Core only): protocol (`RpcPeer`, `RemoteJson`, `UriMapper`, messages), transports (`ProcessChannel`,
  `ShellServerTransport` over `SshRemoteShell`/`LocalRemoteShell`, `LoopbackTransport`), installer (`ServerScripts`, `InstallDecision`, `ServerBundleSet`),
  askpass, the connection state machine (`RemoteClient`, `RemoteConnectionManager`), the `ssh:` providers, the bridges, `ProcessLanguageHost`, and
  `RemoteShellContributions` (commands + status indicator, written against Core services only).
* `CsCode.Server` (`cscode-server`, an Exe): `ServerHost` composes `AddCsCodeCore/Platform/Lsp` with `CSCODE_HOME` = its data dir, replaces the window,
  navigation and output services with forwarders to the client and the feature registry with a mirroring one, and serves the protocol on stdio.
  One server process = one client connection; it exits at stdin EOF (ssh gone, client gone) or on `server/shutdown`.
* Additive hooks outside these projects: Core `IRemoteConnectionManager`, `IFileSystemProvider`, `IFileWatcherProvider`, `IFileSearchProvider` +
  `FileSearchOptions`; Platform `FileSystemRouter`, `FileWatcherRouter`, the search-provider hook of `WorkspaceService.FindFilesAsync`; Lsp: the in-process
  manager serves only `file:`/`untitled:` documents; App: `AddCsCodeRemote()`, `RemoteShellContributions.Start()`, the File menu entry, `ssh://` command-line
  targets, `cscode --askpass`, the remote connection in the graceful shutdown.
* Wave 3 (R3/R4) hooks, all additive: Core `ITerminalBackendProvider`/`ITerminalBackend` (Terminal: `BackendPtyHost`, `TerminalService.Backends`),
  `DebugAdapterStreamDescriptor`/`IDebugAdapterStreamConnection`/`IDebugPathMapper` (Dap: `StreamTransport`, `DebugSessionOptions.PathMapper`,
  `IBreakpointSyncHost.AdapterPathOf`, `DapPlatform.PathMappers`), `ITextSearchProvider` (Panels.Search: `SearchEngine` takes them, `RootSet` keys
  non-`file:` roots), `StatusBarItemModel.BackgroundColorKey` (App status bar; Ui publishes `statusBarItem.remoteBackground/Foreground`),
  `LaunchRequest.RemoteTargets` + `IpcMessage.Remotes` (Sessions; App: `LauncherEntry`, `SessionWindowController` opens them and reconnects a restored
  remote folder), the Editor's watch of non-`file:` documents (T12) and the tab title's unescaped remote names.

## Protocol

JSON-RPC 2.0 with LSP-style `Content-Length` framing on System.Text.Json (`RpcPeer`, own implementation — see "Decisions"). The server writes the
greeting line `CSCODE-SERVER-READY v1` before the framed stream; the client skips anything a login shell printed before it (motd, banners).

| Area | Methods (client → server unless marked) |
|---|---|
| lifecycle | `hello` (protocol version + capabilities: `gzip`, `watch`, `search`, `languages`; server version, OS/arch, pid, home, data dir), `$/ping`, `server/shutdown`, `server/status` (open documents, watches, searches, uploads, registrations, memory, threads), `$/cancelRequest` (both ways) |
| files | `fs/stat` (with an etag = mtime ticks + size), `fs/readDir`, `fs/read` (≤ 256 KB range), `fs/write` (≤ 256 KB, optional expected etag), `fs/writeBegin` / `fs/writeChunk` / `fs/writeCommit` / `fs/writeAbort` (chunked upload, committed with one atomic write), `fs/mkdir`, `fs/delete`, `fs/rename`, `fs/copy` |
| watch | `watch/start` (answers once the OS watch is armed), `watch/stop`; server → client `watch/changes` |
| search | `search/files`, `search/text` start a session; `search/next` pulls the next batch (natural back-pressure); `search/cancel` |
| workspace | `workspace/setFolders`, `settings/setUser` |
| documents (R2) | `doc/open` (full text + client version), `doc/change` (the editor's own incremental changes and versions), `doc/close`, `doc/saved`, `doc/language` — handled inline, in order |
| languages (R2) | server → client `lang/registered` / `lang/unregistered`; `lang/list`, `lang/invoke` (registration id + op + document + args), `lang/willSave`, `lang/fileOperation`, server → client `diag/publish`, `lsp/servers`, `commands/changed`; `lsp/control`, `lsp/addDefinition`, `lsp/removeDefinition`, `commands/execute` |
| UI (R2, server → client) | `window/showMessage`, `window/quickPick`, `window/inputBox`, `window/openExternal`, `window/clipboard`, `window/status`, `window/progress` (+ client → server `window/progressCancel`), `nav/open`, `nav/applyEdit`, `output/append` |
| channels (R3/R4) | `chan/data` (both ways: bytes at an absolute offset), `chan/ack` (client → server: consumed up to an offset), server → client `chan/closed` (final offset, exit code, reason), `chan/close` (end the source after a grace period), `chan/attach` (after a reconnect: resume from the client's offset; answers what the server received of the input and how much output it could not keep), `chan/keep` (the channels the client still has; the rest end) |
| terminals (R3) | `term/create` (the unresolved `TerminalCreateOptions` + size → channel, pid, program, directory), `term/resize`, `term/info` (foreground process) |
| debugging (R3) | `dap/resolve` (the server's configuration providers), `dap/start` (adapter for a configuration → channel), `dap/connect` (another connection to an adapter server: js-debug's child sessions) |
| tasks (R3) | `task/list`, `task/start` (→ run id), `task/wait` (long poll, re-issued after a reconnect), `task/cancel` |
| documents (R4) | `doc/keep` (after a reconnect: the documents the client still has open) |

* Protocol **v2** (R3/R4). `hello` answers an `InstanceId` (random per server process): a reconnect that finds the same one resumed the same server.
* **Core models cross the wire as they are** (they are records). `RemoteJson` adds the converters System.Text.Json lacks: `Uri` (also as a dictionary
  key, `WorkspaceEdit.Changes`) mapped per connection, value tuples as two-element arrays (`ParameterInformation.LabelRange`, file-operation pairs — without
  it they silently serialised as `{}`), and `ResourceOperation` as a `kind`-discriminated union. Nulls are omitted. Round trips of every model are tested.
* **URI mapping** (`UriMapper`): the server only ever sees `file:` URIs of its machine; the client sees `ssh://[user@]host[:port]/abs/path`. Client URIs use
  one escaping rule (`Uri.EscapeDataString` per segment — the Explorer's), server URIs exactly Platform's `FileUri.FromPath`; both are pinned by tests,
  so a diagnostic or definition that comes back equals the URI the editor opened. Opaque `JsonElement` data (completion `data`, command arguments) is
  never touched. Windows remote paths (`C:/x` ⇄ `/C:/x`) are mapped (unit-tested) though Windows servers are not supported yet.
* **Errors** keep their .NET type across the wire (`FileNotFound`, `DirectoryNotFound`, `UnauthorizedAccess`, `NotSupported`, `Timeout`, conditional-write
  conflicts as `RemoteFileModifiedException`); a lost connection fails every pending call with `RemoteDisconnectedException` (an `IOException`).
* **Large payloads** move in 256 KB chunks, up to 8 in flight per transfer, gzip-compressed when the server advertised `gzip` and it pays off (a chunk
  expanding beyond 512 KB is refused). A read whose chunks report different etags (the file changed mid-read) starts over (3 tries).
* **Bounded everywhere**: message size 64 MB (a bigger announcement closes the link), incoming queue 4096 (the reader waits: back-pressure into the
  transport), outgoing queue 100,000 (overflow closes the link instead of growing or blocking), uploads ≤ 512 MB and 8 at a time (idle ones expire after 2 min),
  16 search sessions (expire when not pulled for 2 min), 512 watches, originals kept for resolve: the last 8 resolvable lists.
* **Every call is cancellable with a timeout** (default 60 s; per call shorter): a cancelled or timed-out call completes at once, without waiting for the
  other end, and sends `$/cancelRequest`; the handler's token is cancelled there.

## Server bootstrap

Before anything runs on the remote, `ssh -G` resolves the effective settings for the destination (the user's ssh_config applied); they go to the
Remote log ("Resolved box: me@10.0.0.5:22 via bastion"), and a configuration ssh rejects fails the connection right there with ssh's message.

`ShellServerTransport` then runs two **fixed** POSIX sh scripts (`ServerScripts`) through an `IRemoteShell` — `ssh … -- host sh -c '<script>' cscode '<arg>'…` or,
in tests, `/bin/sh -c <script> cscode <arg>…` locally:

1. **Start** (`$1` install root, `$2` data dir, `$3` marker, `$4` force, `$5` persistent session id, `$6` its grace period, `$7…` candidate hashes): execs
   `<root>/bin/<hash>/cscode-server --stdio --data-dir … [--persist <id> --grace <s>]` for the first installed hash (touching its directory), else prints
   `CSCODE-NOT-INSTALLED` + `os`, `arch`, `home`, `dotnet --list-runtimes` and exits 85. An installed server costs **one** ssh connection.
2. **Decide** (`InstallDecision`, pure, unit-tested): the self-contained bundle for the remote's RID; else the portable framework-dependent bundle when the
   remote has a .NET 10 runtime; else a clear refusal (unsupported platform / no runtime / no bundles / non-POSIX login shell).
3. **Install** (`$1` root, `$2` hash, `$3` sha256, `$4` force; the bundle on **stdin**): `umask 077`, private `mktemp -d` under the root, `cat` the
   tar.gz, verify the SHA-256 (`sha256sum`, else `shasum -a 256`), extract, `chmod 755`, rename into `bin/<hash>` (atomic on one file system), remove the
   temp dir on any exit; then delete older installs keeping the two most recently used plus this one (only 16-hex-digit names are ever deleted).
4. Start again.

The bundles are described under [Server bundles](#server-bundles). The client finds them in `remote.ssh.serverBundles`, then
`{InstallDir}/server-bundles`, then a source tree's `.scratch/server-bundles`. The server sets `InvariantGlobalization` (a remote may lack libicu) and
checks OpenSSL at start: .NET on Linux hashes through libssl, and a host without it (`debian:*-slim` containers, for one) would abort the process
the first time a workspace opens — instead the start fails with "No usable version of libssl was found" and the client adds what to install.

## Security model

* **No shell strings from data.** The only shell command line is ssh's remote command, built as `sh -c '<constant script>' cscode '<arg>'…` with every
  argument single-quoted by `PosixShell.Quote` (`'` → `'\''`; NUL refused). Fuzz-tested against a real `/bin/sh` with `; $() \` ' " & | newline \ * ? ~ #`.
  Install hashes are validated as 16 lower-case hex digits. Bundles travel on stdin and are checksum-verified before anything is extracted.
* **ssh arguments**: an argument vector (never a local shell), the destination after `--`, host/user validated (no leading `-`, whitespace, control
  characters, `/`, `@`), extra options only as `-o Key=Value`. The user's ssh configuration applies (ProxyJump, IdentityFile, agent, ControlMaster, 2FA);
  `remote.ssh.configFile` passes `-F`.
* **docker / wsl**: the same fixed scripts, as an argument vector for `docker exec -i [-u user] <container> sh -c <script> cscode <args…>` /
  `wsl.exe -d <distro> [-u user] --exec sh -c <script> cscode <args…>`; container and distribution names are validated (letters, digits, `_ . -`,
  never a leading `-`). The docker test container has no network and no mounts.
* **Persistent servers (R4)**: the daemon's socket is in a 0700 directory (`<data dir>/run`, or `$XDG_RUNTIME_DIR/cscode-server` when that path is too
  long) with mode 0600, next to a lock file only the live daemon holds; session ids are 32 random hex digits (validated server-side). Only the same user
  can reach a daemon. The daemon is started through `/bin/sh -c 'exec "$0" "$@" </dev/null >/dev/null 2>&1 &'` (no string from data) and calls
  `setsid()`: it holds none of ssh's pipes and is not in the relay's process tree.
* **Prompts (askpass)**: per connection attempt an `AskpassServer` creates a directory with mode 0700 (in `$XDG_RUNTIME_DIR`, else the temp dir), a Unix
  socket (0600) and a 0700 wrapper script `exec '<cscode>' --askpass "$@"`, and a random 256-bit one-time token. ssh gets `SSH_ASKPASS=<script>`,
  `SSH_ASKPASS_REQUIRE=force` and the socket/token variables; `cscode --askpass "<prompt>"` sends token + prompt; the server compares the token in constant
  time, asks through `IWindowService` (a Yes/No message for host-key confirmations, a masked input box for passphrases/passwords/PINs, an unmasked one for
  codes and user names) and answers. Nothing is stored; the directory, socket and token are deleted when the attempt ends.
* **CsCode never reads `~/.ssh` keys or `known_hosts` itself.** The only file under `~/.ssh` it reads is `~/.ssh/config` (or `remote.ssh.configFile`), and
  only to list `Host` names when the user runs *Connect to Host…* — off the UI thread, never written.
* The remote server writes only below its install root and data directory (and whatever the language servers it starts write).

## Threading and isolation

The ARCHITECTURE §6 rules, as implemented and tested:

| Rule | Implementation | Test |
|---|---|---|
| the caller only enqueues | `RpcPeer.InvokeAsync`/`Notify` capture the immutable params and return; serialisation and socket writes run on the connection's **writer thread**, response deserialisation on its **reader thread**; `RemoteFileSystem` hops off the caller (`PoolHop`) before encoding chunks; connect/disconnect/host listing hop before any settings, disk or process work | `File_system_calls_cost_the_caller_only_an_enqueue`, `Every_bridged_provider_method_costs_the_caller_under_100_microseconds` (avg < 0.1 ms, throwing synchronization context never used), `Connecting_and_disconnecting_return_to_the_caller_at_once` (< 1 ms), `Calls_cost_the_calling_thread_only_an_enqueue…` |
| typing never waits | `DocumentSync`'s document-event handlers only check the URI and enqueue to its own FIFO thread | `Typing_costs_the_editing_thread_an_enqueue_while_the_server_is_slow` (< 50 µs per keystroke over the baseline while the server takes 5 ms per change) |
| own threads, not the shared pool | per connection: reader, writer, handler thread(s) ("Remote lane"); docsync thread; connect-time process I/O on short-lived dedicated threads; server: 4 handler threads, IO lane for range reads, below-normal text-search threads | — |
| ordered where it matters | document notifications run **inline on the server's reader**, so a request read after a change sees it; server → client notifications run on one handler thread (watch events, diagnostics in order) | `Inline_notifications_are_applied_in_order…` |
| bounded, coalesced producer → UI traffic | diagnostics coalesced per document (50 ms, latest state, ≤ 200 documents per message); output forwarded every 250 ms, ≤ 64 KB per channel per flush; status/progress capped | — |
| slow network | a 500 ms round trip slows answers, never the caller; calls pipeline | `A_slow_link_slows_answers_but_never_the_caller` |
| child processes | the local server starts through `nice -n 10` (its language servers inherit it); ssh and the local server are tracked with `IMemoryMonitor.TrackChildProcess` (the shell kills survivors at exit) | process-host test checks the language server is a child of the language host |

## Client services

* `RemoteConnectionManager` (`IRemoteConnectionManager`): one connected host at a time; builds the ssh transport from settings (per-host overrides),
  wires the participants, logs to the **"Remote"** output channel (status lines and ssh's stderr).
* `RemoteClient`: the state machine `Disconnected → Connecting → Bootstrapping → Connected`, drop → `Reconnecting` (backoff 0.5 s doubling to 30 s,
  ±20 % jitter, `remote.ssh.reconnectAttempts` = 8) → `Connected` or `Failed`; bootstrap errors that retrying cannot fix (no bundle, checksum, protocol)
  fail at once. On every (re)connect each `IRemoteParticipant` resynchronises: watches are re-armed (subscribers get a `Changed(root)` rescan for the
  outage), workspace folders and user settings are re-sent, open documents re-sent in full (and `doc/keep` closes the others), channels resume. A
  lost connection, or a reconnect whose resynchronisation failed half-way, is **detached** (no `server/shutdown`): a persistent server waits for the next
  attempt; only a deliberate disconnect (and the app's exit) shuts the server down. `RemoteSession.Resumed` and the status message say when a reconnect
  reached the same server process.
* `RemoteFileSystem` (`IFileSystemProvider`, schemes `ssh`, `docker`, `wsl`): same contract and exceptions as the local file system; directory listings cached
  (`DirectoryCache`: LRU 2048 directories, 3 s TTL, invalidated by watcher events and own changes, `IMemoryTrimmable`); conditional writes
  (`WriteFileAsync(uri, bytes, expectedEtag)` → `RemoteFileModifiedException`, `StatWithEtagAsync`). While the host is connecting or reconnecting a call
  **waits** for the connection (`RemoteOptions.ReconnectWait`, 60 s) instead of failing — a save during a short outage lands after the reconnect, and a
  restored session's editors load as soon as the connection is up. Not connected (no host, failed, closed) → an `IOException` naming the host.
* `RemoteWatcher` (`IFileWatcherProvider`): handles with a `Ready` task (completes when the server's OS watch is armed). The editor watches open remote
  documents through it (T12): a file changed on the server reloads a clean document and asks about a dirty one, as for local files.
* `RemoteSearch` (`IFileSearchProvider` behind quick open, and `ITextSearchProvider` behind **Find in Files**: the server walks and scans in parallel,
  only matches cross the network — see [below](#terminals-debugging-and-tasks-r3)).

## Language bridging (R2)

The server hosts the real `ILanguageServerManager` against its files. Its registry (`MirroringFeatureRegistry`) numbers every registration; each is
announced (`lang/registered`: feature, selector, owner, trigger characters / legend) and the client registers one **bridge provider** per registration
in the local `ILanguageFeatureRegistry` — the server's selector restricted to the `ssh` scheme — forwarding every call as `lang/invoke`. All 21 feature
interfaces are bridged (completion + resolve, hover, definition kinds, references, highlights, rename + prepare, formatting (document/range/on-type),
signature help, code actions + resolve, document/workspace symbols, folding, selection ranges, semantic tokens (full/range), inlay hints, code lens +
resolve, colours, linked editing, call/type hierarchy). Resolve calls name the original item (`OriginToken`): language clients resolve by the
instance they produced, so the server keeps the last 8 resolvable lists. Remote failures and timeouts (5 s interactive, 35 s background) answer "no
result" like a slow local server; cancellation propagates. Bridges, mirrored diagnostics (owner `remote:<host>`) and forwarded commands disappear with
the connection.

Also bridged: diagnostics (`diag/publish` → `IDiagnosticService`), will-save and file-operation participants, LSP-contributed commands (registered
locally as forwarders unless a local command has the id), `window/*` (messages with actions, quick picks — the server's items keep their `Tag`,
input boxes — `Validate` is applied on the server, which re-asks with the error as prompt, progress with cancel, status messages, open external,
clipboard), `nav/open` and `nav/applyEdit` (workspace edits come back with client URIs), and server output channels (prefixed `Remote: `).
User settings flow client → server (`settings/setUser`, debounced re-send on change); workspace settings are read on the server.
The in-process language client ignores `ssh:` documents (Lsp hook), so a remote file never starts a local server.

## Terminals, debugging and tasks (R3)

All three run on the server, next to the files, and stream through **channels**: resumable byte streams with absolute offsets in both directions.
Output is kept in a replay ring per channel (up to 2 MB, grown on demand: a quiet terminal holds a few KB); while a client is connected a producer
more than 512 KB ahead of the client's acknowledgements waits
(the remote program is throttled, exactly as a full local PTY throttles it); while nobody is connected nothing waits and older output is counted as
lost. Input is numbered and the client keeps its last 1 MB (also grown on demand), so after a reconnect the server gets exactly what it missed. A channel sends only to the
connection that created or reattached it, so the replay always comes before newer output.

* **Terminals** (`RemoteTerminals`, the Core `ITerminalBackendProvider` the Terminal module asks for every new terminal): while the workspace's
  primary folder is on the connected host, a terminal runs there — `term/create` sends the *unresolved* `TerminalCreateOptions`; the server resolves
  the profile, shell, environment and working directory with the Terminal module's own resolver (its settings are the client's, mirrored) and runs a
  PTY. The client side is an `IPtyHost` (`BackendPtyHost`): output is delivered on the terminal's own thread, input/resize/kill only enqueue, the
  foreground process name is the server's (refreshed at most once a second). Debuggees started with `runInTerminal` follow the same rule. A local
  workspace's terminals stay local.
* **Debugging** (`RemoteDebugging`): the client's debug service keeps its DAP client, breakpoints and UI; the adapter runs on the server. Registered
  for every debug type after the built-ins: its **factory** (asked first) answers a `DebugAdapterStreamDescriptor` for a remote folder (else nothing);
  its **configuration provider** (run last) sends the configuration to the server, whose own providers resolve it there (cargo artifacts, cwd…);
  `dap/start` makes the server's built-in factory start netcoredbg / js-debug / GDB *on the server* (a download offer is forwarded as a message) and the
  adapter's stream becomes a channel. Its **path mapper** (`IDebugPathMapper`) sends breakpoints by their remote paths, maps stack frames' paths back to
  the workspace's `ssh:` URIs, and makes `${workspaceFolder}`/`${file}` the remote paths. Adapter servers (js-debug) are reached again by their child
  sessions through `dap/connect` (the stream connection's `SharedEndpoint`).
* **Tasks** (`RemoteTaskService` wraps the Dap module's `ITaskService`): tasks of a remote folder are listed and run by the server's task runner
  (shell/process tasks, `dependsOn`, problem matchers into the server's diagnostics → mirrored into the Error List, output into "Remote: Tasks").
  `task/start` + `task/wait` (long poll): a run outlives the connection that started it and the client waits for it again after a reconnect to the
  same server; cancelling kills the task's process tree there. preLaunchTask/postDebugTask of a remote debug session go the same way.
* **Find in Files** (`ITextSearchProvider`): when every searched folder is remote, the panel's engine sends the query (pattern, case, whole word,
  `files.exclude` + `search.exclude` for pruning, ignore files, the result cap, the file size limit) and reads no remote file itself; matches come back
  in batches and get the same include/exclude filtering, cap and preview rules as local matches. Documents with unsaved changes are searched from
  memory. Multi-line regexes still go through the file system (the server scans line by line).

## Reconnect and persistence (R4)

* **Persistent server** (default; `remote.ssh.reconnectGracePeriod` seconds, 10800 like VS Code's reconnection grace time; 0 turns it off): what ssh (or
  docker exec / wsl.exe) runs is a **relay** — `cscode-server --stdio --persist <id>` — that connects to the session's **daemon** over its Unix socket,
  starting it when it does not run, writes the greeting and copies bytes both ways. The daemon serves one client connection at a time (a newer one
  replaces an older, half-open one) and keeps language servers, open documents, terminals, debug adapters and task runs between connections. It exits
  when the client disconnects deliberately (`server/shutdown`), on SIGTERM, or after the grace period without a client.
* **Reconnect after network loss with state kept**: the client reconnects with backoff; `hello` returns the same `InstanceId`, so it resumes: channels
  reattach (`chan/attach`, replay, resend), tasks are waited for again, documents are re-sent in full (the server replaces its copies; `doc/keep`
  closes what the client closed meanwhile), watches re-armed, diagnostics/servers/commands re-announced by the server. A debug session that was
  stopped at a breakpoint steps on after the reconnect; a terminal's program keeps running and nothing it printed is lost or repeated (tested with
  four random link cuts during a flood). If the server did not keep the state (a new instance), terminals and adapters end with the reason.
* **Persistence across a client restart**: a saved session whose folder is remote reconnects when it is restored (`remote.openFolder`; the editors load
  as soon as the connection is up) — tested with real session processes. The persistent session id per host is recorded in
  `{GlobalStorageDir}/remote/sessions/` with its owner's pid: a client that finds the previous owner dead (a crash) reuses the id and reattaches to the
  server it left running (warm language servers); two live windows never share one. Terminals are not restored after a restart (the accepted default):
  a reattached server ends the channels the new client does not claim (`chan/keep`).

## Transports: ssh, docker, wsl

One authority shape names all three: `ssh://[user@]host[:port]/path`, `docker://[user@]container/path`, `wsl://[user@]distro/path` (*Remote: Connect to
Host…* and the command line accept the `docker://` and `wsl://` forms). Every transport runs the same fixed start/install scripts, so bootstrap, upload,
persistence and everything above work the same; the status-bar badge reads "SSH: …", "Docker: …" or "WSL: …".

* **ssh**: the system client as described above.
* **Docker** (`DockerRemoteShell`): `docker exec -i` into a *running* container (`remote.docker.path`); the server installs under the container user's
  `$HOME/.cscode-server`. Tested against a real container (`mcr.microsoft.com/dotnet/runtime-deps:10.0`, pinned by digest).
* **WSL** (`WslRemoteShell`): `wsl.exe -d <distro> --exec sh -c …` (`remote.wsl.path`). Written behind the same seam and unit-tested for its argument
  vector, but **not run** (no Windows host here); WSL distributions are Linux, so the linux-x64/arm64 bundles apply.

URIs lower-case container/distribution names (as any URI host); the authority keeps the typed case for the docker/wsl command lines, and URIs match it
case-insensitively. Docker container names are case-sensitive: use lower-case names (or IDs) for containers you reopen from a saved session.

## Server bundles

`tools/publish-server.sh --all` builds self-contained single-file bundles for **linux-x64, linux-arm64, osx-x64, osx-arm64 and win-x64** plus the portable
(framework-dependent) one, into `.scratch/server-bundles/` with `manifest.json` (flavour, rid, file, sha256, install hash = first 16 hex digits of the
sha256). The runtime, apphost and targeting packs come **from NuGet only** (`NetCoreTargetingPackRoot` pointed at an empty directory): a distribution's
SDK may patch its bundled packs for itself — the Nix SDK's linux-x64 apphost had the ELF loader `/nix/store/…-glibc/lib/ld-linux-x86-64.so.2`, so that
bundle started only on machines with that store path (found by the Docker test); the script now refuses a Linux bundle whose loader is in `/nix/store`.
`tools/fetch-server-runtime-packs.sh` restores the 11 packs (version 10.0.12) and checks each against nuget.org's published SHA-512 and size. The macOS
apphosts carry an ad-hoc code signature (the SDK signs them on Linux; checked for `LC_CODE_SIGNATURE`). win-x64 holds `cscode-server.exe`; the
client does not bootstrap Windows remotes yet (POSIX scripts), so that bundle is built for a future PowerShell installer.

**Trimmed** (partial mode: only the framework's assemblies, which are marked trimmable; ours and the NuGet libraries stay whole; System.Text.Json keeps
its reflection serializer), measured on 2026-10-29:

| Bundle (tar.gz) | untrimmed | trimmed |
|---|---|---|
| linux-x64 | 33.1 MB | 11.0 MB |
| linux-arm64 | 33.0 MB | 10.7 MB |
| osx-x64 | 34.7 MB | 10.9 MB |
| osx-arm64 | 32.7 MB | 10.4 MB |
| win-x64 | 34.9 MB | 10.7 MB |
| portable (not trimmable) | 2.8 MB | — |

Correctness: the whole `CsCode.Remote.Tests` suite (153 tests: files, watch, search, TypeScript LSP through the bridge, GDB and js-debug sessions with
child sessions, terminals, tasks, the persistent daemon, chaos, the installer and real-ssh bootstrap) passes against the trimmed linux-x64 server
(`CSCODE_TEST_SERVER=<extracted bundle>/cscode-server`, `CSCODE_SERVER_BUNDLES=<dir>`), and the Docker test runs the trimmed bundle in a container.
`--no-trim` builds untrimmed bundles if a path the tests do not cover turns out to need a trimmed framework member.

## Process language host

`cscode.languageHost = process` (applies on restart): `AddCsCodeRemote()` wraps the `ILanguageServerManager`, `IWillSaveParticipant` and
`IFileOperationParticipant` registrations made before it, so they resolve to `ProcessLanguageHost` — the in-process `LanguageServerManager` is never
constructed. `ProcessLanguageHost` starts `cscode-server` (the build that ships next to the app) as a below-normal-priority child over stdio with the
identity URI mapping and the same participants; it mirrors the server list (`lsp/servers`, for the status bar), forwards start/stop/restart/stop-all and
`AddDefinition`, restarts the child with backoff if it dies, and stops it (tree killed) on dispose. Data dir: `{GlobalStorageDir}/language-host`.

## Shell glue

`RemoteShellContributions.Start()` (called by `ShellController`) registers: `remote.connect` *Remote: Connect to Host…* (quick pick of the ssh_config `Host`
names without wildcards, `Include`s followed, + *Add New SSH Host…*, which also takes `docker://container` / `wsl://distro`), `remote.openFolder`
*Remote: Open Remote Folder…* (File menu; takes a remote URI from the command line: `cscode ssh://me@box/home/me/src`, also through the launcher in
background mode — T5), `remote.disconnect`, `remote.reconnect`, `remote.reinstallServer`, `remote.showLog`; the remote debug factory/provider; and the
remote indicator at the left of the status bar, drawn as a **badge** in the theme's `statusBarItem.remoteBackground` / `statusBarItem.remoteForeground`
(VS Code's ids; white on `#16825D` in the VS 2022 themes, 4.8:1) in every state (`$(remote)` when disconnected → click to connect;
`$(sync~spin) SSH: host` connecting/reconnecting; `$(remote) SSH: host` connected; `$(error) SSH: host (disconnected)` → click to reconnect). A
cancellable progress notification while connecting and an error message with *Retry* / *Show Log*. The shell's graceful shutdown disposes the
connection (server and ssh stopped).

## Setup and settings

1. Build the bundles once: `bash .scratch/notes/with-nix-env.sh tools/publish-server.sh` (or with a normal `dotnet`: `tools/publish-server.sh`).
2. Make sure `ssh <host>` works non-interactively or with prompts (keys, agent, ~/.ssh/config). The remote needs a POSIX login shell, `tar`, `gzip`,
   `sha256sum` or `shasum`, OpenSSL (libssl) and glibc on Linux x64/arm64 or macOS x64/arm64 (or any OS with a .NET 10 runtime for the portable bundle).
3. *File ▸ Open Remote Folder…* or the status-bar remote button.

| Setting | Default | |
|---|---|---|
| `remote.ssh.path` | `ssh` | ssh client |
| `remote.ssh.configFile` | `""` | `-F` file (empty: ssh's defaults); also the file *Connect to Host…* lists |
| `remote.ssh.options` | `[]` | extra `-o Key=Value` |
| `remote.ssh.connectTimeout` | 30 | seconds (`ConnectTimeout`) |
| `remote.ssh.serverInstallPath` | `""` | remote install root (empty: `~/.cscode-server`) |
| `remote.ssh.serverDataPath` | `""` | remote data dir (empty: `<install root>/data`) |
| `remote.ssh.serverBundles` | `""` | local bundle directory |
| `remote.ssh.reconnectAttempts` | 8 | |
| `remote.ssh.useAskpass` | `true` | prompts in CsCode |
| `remote.ssh.reconnectGracePeriod` | 10800 | seconds the server keeps running for a client that lost its connection; 0 = it ends with the connection |
| `remote.docker.path` | `docker` | docker CLI for `docker://` remotes |
| `remote.wsl.path` | `wsl.exe` | for `wsl://` remotes (Windows) |
| `remote.ssh.hosts` | `{}` | per-host overrides of the keys above: `{"box": {"serverInstallPath": "/opt/cscode"}}` |
| `cscode.languageHost` | `inProcess` | `process`: language client in a local child process |

`RemoteOptions` (DI, not settings) carries what the host program or tests supply: ssh environment changes, the askpass command and directory, bundles,
a process marker, reconnect policy, clock, call timeout, the reconnect wait, the grace period and a fixed server session id, a transport factory, the local
server path/data dir/environment, and a language-host override. The `remote.ssh.*` server settings (install/data path, bundles, reconnect) apply to the
docker and wsl transports too.

## Decisions

* **Own JSON-RPC peer instead of StreamJsonRpc** (the brief named StreamJsonRpc): the binding threading model needs reader/writer/handlers on dedicated
  threads, deferred serialisation off the caller, inline ordered notifications, bounded queues with defined overflow behaviour and calls that complete
  at once on cancel/timeout; StreamJsonRpc runs its reader on the thread pool and completes a cancelled call only when the other side answers (see LSP.md).
  It is ~500 lines and covered by its own tests.
* **Pull-based search sessions** rather than streamed notifications: back-pressure without a client-side buffer.
* **Method names + DTO records instead of typed service interfaces** (`IRemoteFileSystem`, `IRemoteSearch`… in the brief): the protocol is
  `RemoteMethods` + the records in `Protocol/Messages.cs`; the client-facing types are the Core contracts themselves (`IFileSystemProvider`,
  `IFileWatcherProvider`, `IFileSearchProvider`, the feature-provider interfaces), so no second interface layer was added.
* **Document versions are the editor's**: the server mirror takes the client's version numbers, so stale-result checks agree on both ends.
* **Remote stays Core-only**: every R3 seam is a Core contract (terminal backends, stream debug adapters + path mapping, text search providers, the
  status-bar background), so the Terminal, Dap and Panels.Search modules learn nothing about the Remote module, and the server composes those modules
  (it references Terminal and Dap) the way it composes Lsp.
* **DAP over a byte tunnel, not a proxied debug service**: the debug UI and DAP client stay local and unchanged; only the adapter's stream is tunnelled,
  plus path mapping at the three places paths cross (variables, breakpoints, stack frames). Proxying `IDebugService` would duplicate the session model.
* **Persistence with a relay and a daemon** rather than a session layer inside one ssh-bound process: ssh's lifetime ends with the network, so the
  state must live in a process that ssh does not own. Per-feature resume (channels with offsets, task long polls, document resync) on top of the
  existing reconnect-and-resynchronise design, instead of making the whole RPC stream replayable.

## Limits and known gaps

* A file operation waits for a reconnect only up to `ReconnectWait` (60 s); an operation in flight at the moment of a drop fails (the caller retries —
  the editor's save leaves the document dirty). Writes are not replayed automatically (an etag-conditional write would be; it is not done).
* WSL is untested (no Windows host). macOS remotes are untested (bundles built and signed ad hoc, not run). Windows remotes (cmd/PowerShell login shells)
  and csh/fish login shells are not supported by the bootstrap; the win-x64 bundle exists for a future PowerShell installer.
* Remote debugging was run end to end with GDB (C) and js-debug (Node.js, with its child session); netcoredbg goes through the same factory path but was not
  run remotely. Debug types from `cscode.debuggers` are not run on the remote (only the built-in adapters). `${env:…}` in launch.json resolves on the
  client. A remote session's client-side providers run first with the `ssh:` folder (they mostly do nothing there), then the server's.
* A debug session survives a reconnect only if the outage is shorter than the adapter request timeout for requests issued during it (none are in a
  stopped session); DAP output lost from the 2 MB ring (a very chatty program during a long outage) ends the session, since DAP framing cannot skip.
* Terminal shell integration, split terminals and tasks-in-terminals (W2, merged separately) were not available here: a remote terminal runs whatever
  the server's resolver resolves; once W2 lands, the server's resolver injects shell integration the same way, and tasks shown in terminals will run in
  remote terminals (the task decorator here runs them through the server's task runner).
* Remote workspace settings apply on the server (language servers, terminals, tasks) but not to client-side editor options.
* Find in Files over a remote workspace counts only the files with matches in its "files searched" figure (the server's own count is not passed on).
* Docker: a container must already run (no image or compose management), and the image needs glibc, OpenSSL, `tar`, `gzip`, `sha256sum` (Microsoft's
  `dotnet/runtime-deps` images have them; `debian:*-slim` lacks libssl). Container names reopen case-insensitively from URIs (see Transports).
* A persistent server whose client crashed keeps running for the grace period (3 h by default), like VS Code's; its language servers and terminals with it.
* Not done from the brief's R1 detail list: content prefetch for open tabs (each open reads on demand, chunked and pipelined), JSON-RPC batch
  arrays (requests pipeline instead), the remote icon overlay in the Explorer title (the Explorer belongs to the shell), and the "Show Log" action on the
  progress notification itself (it is on the failure message; `Remote: Show Log` is a command).
* The badge keeps one colour pair in every state (VS Code's); the state shows through the icon and text. Its hover in the status bar draws the generic
  hover brush over the status bar background, not a darker badge.
* A server killed with SIGKILL orphans the language servers it started (a clean exit or a dropped connection stops them).
* Remote diagnostics are mirrored per document as the merged set of all server-side owners (one owner per connection on the client).

## Tests

```
bash .scratch/notes/with-nix-env.sh tools/publish-server.sh --all  # once: the installer, real-ssh, docker and T5 tests need the bundles (else they skip)
tools/fetch-docker-test-image.sh                                     # once, for the Docker test (else it skips); remove the image afterwards
bash .scratch/notes/with-nix-env.sh dotnet test tests/CsCode.Remote.Tests [-e CSCODE_JSDEBUG=<js-debug dir>]
bash .scratch/notes/with-nix-env.sh dotnet test tests/CsCode.App.Tests --filter "FullyQualifiedName~RemoteCompositionTests|FullyQualifiedName~RemoteLaunchProcessTests"
# against a published (e.g. trimmed) server instead of the build's dll:
bash .scratch/notes/with-nix-env.sh dotnet test tests/CsCode.Remote.Tests -e CSCODE_TEST_SERVER=<extracted bundle>/cscode-server -e CSCODE_SERVER_BUNDLES=<bundle dir>
```
Run without `SSH_AUTH_SOCK` and without a display (the tests set `SSH_AUTH_SOCK` empty for every ssh they start regardless).

* **R3** (`R3Tests`, real loopback server): a remote workspace's terminal is the server's child, starts in the workspace folder, round-trips input/output,
  resize (`stty size`), Ctrl+C and the foreground process name, and reports its exit code; killing it ends its process group and background jobs on the
  server; a local workspace's terminal stays local; a missing shell is shown in the terminal. Tasks list/run on the server with exit codes, output in
  "Remote: Tasks" and cancellation that ends the process tree. GDB debugs a C program on the server: the breakpoint set on the `ssh:` URI (a file name
  with a space) is hit, the stop location and frames map back to the `ssh:` URI, variables evaluate, output arrives, gdb ends with the session.
  js-debug (with `CSCODE_JSDEBUG`) debugs a Node.js program whose child session reaches the same adapter server.
* **R4** (`R4Tests`, `R4ChaosTests`, a real persistent server: relay + daemon): a cut link reconnects to the same server process (resumed) and a running
  terminal continues with every line once and in order, with input typed during the outage delivered; a GDB session stopped at a breakpoint steps on after
  a cut and a task survives one; a save during the outage waits and lands; a deliberate disconnect ends the daemon at once; a server whose client never
  comes back exits after its grace period; a restarted client (same session id) reattaches to the server a crashed one left; the session registry
  reuses only a dead owner's id. Chaos: four random cuts during a terminal flood, a 1 MB file-read loop and a typist: nothing lost or repeated, one server
  instance throughout, a keystroke costs the typing thread < 100 µs on average; 20 persistent connect/disconnect cycles leave no relay, daemon, thread or
  descriptor behind.
* **Find in Files** (`Panels.Search.Tests/RemoteTextSearchTests`, `LoopbackFileSystemTests`, App composition): matches from the provider are filtered like
  local ones (include/exclude, cap) and no remote file is read; dirty documents come from memory; long-line previews keep the match; the real server
  prunes excluded and ignored folders; the real engine in the app returns the server's matches.
* **Transports** (`TransportSeamTests`, `DockerTransportTests`): docker/wsl authorities (case, hostile names), the exact argument vectors; a real container
  (pinned image, named `cscode-remote-test-<random>`, no network, no mounts, removed by ID even on failure) bootstrapped through `docker exec` — upload,
  install, persistent start — then files, quick open, server-side search and a terminal in the container, and the server gone after a disconnect; also
  with a stdio server (`CSCODE_TEST_DOCKER_STDIO=1`).
* **Badge** (`ShellContributionTests`, App composition): the indicator carries the remote colour keys in every state and the status bar draws it with the
  theme's brush (`#16825D` in VS 2022 Dark).
* **T5 / restart** (`App.Tests/Background/RemoteLaunchProcessTests`, real `cscode` session processes): `cscode --session R ssh://tester@fakehost/<ws>` goes
  through the launcher to the session, which bootstraps the server through an ssh stand-in script (no sshd) and opens the folder; after the session quits
  and starts again, it reconnects and reopens the folder. Unit: the launcher forwards `RemoteTargets` in its show request, the IPC codec keeps them.
* **T12** (App composition): a remote document changed on the server reloads; its tab shows `my notes.txt`, not `my%20notes.txt`.

* **Unit**: protocol peer (round trip, error types, inline ordering, cancel/timeout without waiting, size limit, preamble skipping, queue overflow, caller
  cost), codec round trips of every Core model with URI mapping, URI mapping (spaces, `#`, `%`, unicode, IPv6, ports, users, Windows remotes, equality with
  Platform's `FileUri` and with Explorer-built URIs), shell-quoting fuzz against a real `/bin/sh`, ssh argument vectors and hostile destinations, `ssh -G`
  parser (incl. a real `ssh -G -F <temp>`), ssh_config host listing with `Include`, installer decision + probe parsing + manifests, reconnect state
  machine with a manual clock (drop → reconnecting → connected, attempts spent → failed, disconnect during backoff), mirror documents vs the editor's
  edits (fuzz), routers, askpass (token, permissions, quoting, classification).
* **Loopback integration** (a real `cscode-server` child process behind an `ssh://` authority, the real Core + Platform + Lsp + Remote composition):
  file operations through the router, error types, 3 MB transfers both ways with gzip, conditional writes, remote workspace + quick open honouring
  `.gitignore` and `files.exclude`, server-side text search, watcher events, TypeScript diagnostics/completion/resolve/hover/definition through the
  bridge (real `typescript-language-server` on PATH), settings mirror, full resync after the server is killed, UI forwarding, process language host
  (TypeScript runs under the child), chaos (500 ms RTT + bandwidth cap, a link cut mid-transfer → reconnect, a SIGSTOPped server → timeouts → recovery),
  50 connect/disconnect cycles of the state machine with `LeakAssert` and thread counts, 10 real connect/watch/read/disconnect cycles with process,
  thread and descriptor counts, no process left (marker scan of `/proc`) after every test, and the language servers a server started exit with it.
* **Installer** (the real scripts in `/bin/sh` with the published bundles): upload + verify + install + start, reuse, forced reinstall, portable bundle on
  the local .NET runtime, corrupted bundle refused, old installs cleaned up (keeping two), unsupported platform.
* **Real SSH**: an own `sshd` (from `/usr/sbin` or the Nix store) on a free loopback port with throwaway host/client keys, `authorized_keys`, `sshd_config`
  and `ssh_config` in a temp dir (`SetEnv HOME/XDG/PATH` for the session); `ssh -F <temp> … IdentitiesOnly … UserKnownHostsFile=<temp>`; never `~/.ssh`,
  never the agent; sshd stopped by PID. `ssh -G` resolution, bootstrap (32 MB upload over ssh) + files + quick open + watcher + TypeScript diagnostics,
  a broken ssh configuration failing with ssh's message, askpass with a host-key
  confirmation and a passphrase-protected key (the server binary's `--askpass` mode as the helper), and a wrong passphrase failing with ssh's reason.
  Skips with a reason when no sshd exists.
* **App composition** (`tests/CsCode.App.Tests/RemoteCompositionTests.cs`, headless, real app start/exit): a remote folder opens in the Explorer, a file is
  opened, edited and saved through the editor and the save command onto the remote disk, an Explorer create goes remote, quick open walks the remote,
  the status-bar indicator follows the connection; `cscode.languageHost = process` runs the child and the app's exit stops it; `ssh://` command-line targets;
  `cscode --askpass` answers without starting the GUI.
