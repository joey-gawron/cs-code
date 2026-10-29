# Remote editing — `CsCode.Remote` + `CsCode.Server`

VS Code Remote-SSH model: the GUI runs locally; the **workspace** — files, search, file watching and the language servers — lives on a remote
machine reached with the **system `ssh`** client. The same headless server doubles as the **out-of-process language host**
(`cscode.languageHost = process`). Phases R1 and R2 of `docs/briefs/remote.md` are implemented; R3 (debug bridging, tasks, terminal) and R4
(reconnect persistence beyond the basics, chaos hardening, WSL/Docker) are not.

* [Architecture](#architecture) · [Protocol](#protocol) · [Bootstrap](#server-bootstrap) · [Security model](#security-model) · [Threading](#threading-and-isolation)
* [Client services](#client-services) · [Language bridging (R2)](#language-bridging-r2) · [Process language host](#process-language-host) · [Shell glue](#shell-glue)
* [Setup and settings](#setup-and-settings) · [Limits](#limits-and-known-gaps) · [Tests](#tests)

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
 IWindowService / INavigationService / Output ◄── UiForwarding ◄──────────────────────────┘                window/* nav/* output/* ◄── ClientWindow/Navigation/OutputService
```

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

1. **Start** (`$1` install root, `$2` data dir, `$3` marker, `$4` force, `$5…` candidate hashes): execs `<root>/bin/<hash>/cscode-server --stdio --data-dir …`
   for the first installed hash (touching its directory), else prints `CSCODE-NOT-INSTALLED` + `os`, `arch`, `home`, `dotnet --list-runtimes` and exits 85.
   An installed server costs **one** ssh connection.
2. **Decide** (`InstallDecision`, pure, unit-tested): the self-contained bundle for the remote's RID; else the portable framework-dependent bundle when the
   remote has a .NET 10 runtime; else a clear refusal (unsupported platform / no runtime / no bundles / non-POSIX login shell).
3. **Install** (`$1` root, `$2` hash, `$3` sha256, `$4` force; the bundle on **stdin**): `umask 077`, private `mktemp -d` under the root, `cat` the
   tar.gz, verify the SHA-256 (`sha256sum`, else `shasum -a 256`), extract, `chmod 755`, rename into `bin/<hash>` (atomic on one file system), remove the
   temp dir on any exit; then delete older installs keeping the two most recently used plus this one (only 16-hex-digit names are ever deleted).
4. Start again.

`tools/publish-server.sh` builds the bundles into `.scratch/server-bundles/` (or `--out`): `cscode-server-linux-x64.tar.gz` (self-contained single file,
compressed, ~32 MB; more RIDs with `--rid` when the SDK has their runtime pack — this script never downloads), `cscode-server-portable.tar.gz`
(framework-dependent + an sh launcher, ~2.4 MB) and `manifest.json` (flavour, rid, file, sha256, install hash = first 16 hex digits of the sha256). The
client finds bundles in `remote.ssh.serverBundles`, then `{InstallDir}/server-bundles`, then a source tree's `.scratch/server-bundles`.
**Not trimmed**: the server (de)serialises Core records with reflection-based System.Text.Json and composes DI services by reflection; neither is trim-safe
without source-generated contexts for every model. The server sets `InvariantGlobalization` (a remote may lack libicu).

## Security model

* **No shell strings from data.** The only shell command line is ssh's remote command, built as `sh -c '<constant script>' cscode '<arg>'…` with every
  argument single-quoted by `PosixShell.Quote` (`'` → `'\''`; NUL refused). Fuzz-tested against a real `/bin/sh` with `; $() \` ' " & | newline \ * ? ~ #`.
  Install hashes are validated as 16 lower-case hex digits. Bundles travel on stdin and are checksum-verified before anything is extracted.
* **ssh arguments**: an argument vector (never a local shell), the destination after `--`, host/user validated (no leading `-`, whitespace, control
  characters, `/`, `@`), extra options only as `-o Key=Value`. The user's ssh configuration applies (ProxyJump, IdentityFile, agent, ControlMaster, 2FA);
  `remote.ssh.configFile` passes `-F`.
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
  outage), workspace folders and user settings are re-sent, open documents re-sent in full.
* `RemoteFileSystem` (`IFileSystemProvider`, scheme `ssh`): same contract and exceptions as the local file system; directory listings cached
  (`DirectoryCache`: LRU 2048 directories, 3 s TTL, invalidated by watcher events and own changes, `IMemoryTrimmable`); conditional writes
  (`WriteFileAsync(uri, bytes, expectedEtag)` → `RemoteFileModifiedException`, `StatWithEtagAsync`). Not connected → an `IOException` naming the host.
* `RemoteWatcher` (`IFileWatcherProvider`): handles with a `Ready` task (completes when the server's OS watch is armed).
* `RemoteSearch` (`IFileSearchProvider` behind quick open and the Find in Files file list, plus `SearchTextAsync` for server-side text search).

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

## Process language host

`cscode.languageHost = process` (applies on restart): `AddCsCodeRemote()` wraps the `ILanguageServerManager`, `IWillSaveParticipant` and
`IFileOperationParticipant` registrations made before it, so they resolve to `ProcessLanguageHost` — the in-process `LanguageServerManager` is never
constructed. `ProcessLanguageHost` starts `cscode-server` (the build that ships next to the app) as a below-normal-priority child over stdio with the
identity URI mapping and the same participants; it mirrors the server list (`lsp/servers`, for the status bar), forwards start/stop/restart/stop-all and
`AddDefinition`, restarts the child with backoff if it dies, and stops it (tree killed) on dispose. Data dir: `{GlobalStorageDir}/language-host`.

## Shell glue

`RemoteShellContributions.Start()` (called by `ShellController`) registers: `remote.connect` *Remote: Connect to Host…* (quick pick of the ssh_config `Host`
names without wildcards, `Include`s followed, + *Add New SSH Host…*), `remote.openFolder` *Remote: Open Remote Folder…* (File menu; takes an `ssh://` URI
from the command line: `cscode ssh://me@box/home/me/src`), `remote.disconnect`, `remote.reconnect`, `remote.reinstallServer`, `remote.showLog`; the remote
indicator at the left of the status bar (`$(remote)` when disconnected → click to connect; `$(sync~spin) SSH: host` connecting/reconnecting;
`$(remote) SSH: host` connected; `$(error) SSH: host (disconnected)` → click to reconnect); a cancellable progress notification while connecting and an
error message with *Retry* / *Show Log*. The shell's graceful shutdown disposes the connection (server and ssh stopped).

## Setup and settings

1. Build the bundles once: `bash .scratch/notes/with-nix-env.sh tools/publish-server.sh` (or with a normal `dotnet`: `tools/publish-server.sh`).
2. Make sure `ssh <host>` works non-interactively or with prompts (keys, agent, ~/.ssh/config). The remote needs a POSIX login shell, `tar`, `gzip`,
   `sha256sum` or `shasum`, and Linux x64 (or any OS with a .NET 10 runtime for the portable bundle).
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
| `remote.ssh.hosts` | `{}` | per-host overrides of the keys above: `{"box": {"serverInstallPath": "/opt/cscode"}}` |
| `cscode.languageHost` | `inProcess` | `process`: language client in a local child process |

`RemoteOptions` (DI, not settings) carries what the host program or tests supply: ssh environment changes, the askpass command and directory, bundles,
a process marker, reconnect policy, clock, call timeout, a transport factory, the local server path/data dir/environment, and a language-host override.

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

## Limits and known gaps

* R3/R4 are not implemented: no debugging, tasks or terminal on the remote; no saves queue while disconnected (a save then fails with an `IOException` naming
  the host); no reattach to a server that outlived its client; WSL/Docker transports absent; no persistence of the remote folder across app restarts
  beyond the recent-folders list.
* Find in Files: file enumeration runs on the server, but the Search panel (another module) still scans the content it reads through `IFileSystem` — one
  round trip per file. `RemoteSearch.SearchTextAsync` (server-side scanning) exists and is tested; wiring it into the panel is an integration step.
* The editor's external-change detection watches `file:` documents only, so a remote file changed on the server is not offered for reload (the Explorer
  does update). Remote workspace settings apply on the server (language servers) but not to client-side editor options.
* Windows remotes (cmd/PowerShell login shells) and csh/fish login shells are not supported; only linux-x64 bundles build offline here (other RIDs need
  their runtime packs).
* Not done from the brief's R1 detail list: content prefetch for open tabs (each open reads on demand, chunked and pipelined), JSON-RPC batch
  arrays (requests pipeline instead), the remote icon overlay in the Explorer title (the Explorer belongs to the shell), and the "Show Log" action on the
  progress notification itself (it is on the failure message; `Remote: Show Log` is a command).
* The status-bar indicator shows state through icons and text; it has no blue/green/orange/red background (`StatusBarItemModel` has no background
  colour, and a foreground colour key could be unreadable on some themes).
* A server killed with SIGKILL orphans the language servers it started (a clean exit or a dropped connection stops them).
* Remote diagnostics are mirrored per document as the merged set of all server-side owners (one owner per connection on the client).

## Tests

```
bash .scratch/notes/with-nix-env.sh tools/publish-server.sh        # once: the installer and real-ssh tests need the bundles (else they skip)
bash .scratch/notes/with-nix-env.sh dotnet test tests/CsCode.Remote.Tests
bash .scratch/notes/with-nix-env.sh dotnet test tests/CsCode.App.Tests --filter FullyQualifiedName~RemoteCompositionTests
```
Run without `SSH_AUTH_SOCK` and without a display (the tests set `SSH_AUTH_SOCK` empty for every ssh they start regardless).

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
  the status-bar indicator follows the connection; `cscode.languageHost = process` runs the child and the app's exit stops it; `ssh://` command-line targets.
