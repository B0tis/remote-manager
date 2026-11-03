# Architecture

Remote-Manager is a Windows desktop app: C# on .NET 10, Avalonia 12 with FluentAvalonia, embedded RDP through
Devolutions MsRdpEx. The plan is in `docs/plan-source.txt`; this page describes how the code is laid out and
where new work plugs in.

## Projects

| Project | Target | Contents |
| --- | --- | --- |
| `src/RemoteManager.Core` | `net10.0`, no UI | Model, SQLite store, library, credentials, status checks, import/export interfaces |
| `src/RemoteManager.App` | `net10.0-windows`, x64 | Avalonia UI, connection handlers, sessions, self-test |
| `tests/RemoteManager.Core.Tests` | `net10.0`, xUnit v3 | Tests for Core (store, migration, inheritance, status, discovery) |

`global.json` switches `dotnet test` to Microsoft.Testing.Platform. Run everything with:

```
dotnet build RemoteManager.slnx
dotnet test --solution RemoteManager.slnx
src\RemoteManager.App\bin\Debug\net10.0-windows\RemoteManager.App.exe --selftest
```

The self-test writes `selftest.log` and probe screenshots to `%LOCALAPPDATA%\RemoteManager\spike`. Set
`REMOTE_MANAGER_DATA` to a folder to run the app (or the self-test) against other data than your own.
The VNC checks use an in-process test server; `RM_VNC_HOST` (with `RM_VNC_PORT`, `RM_VNC_PASSWORD`, `RM_VNC_KEYS`)
adds a session against a real VNC server.

## Model (`Core/Model`)

- `Connection`: id, name, `ConnectionType`, host (or remote ID), port (0 = type default, see `DefaultPorts`),
  description, notes, tags, favorite, group id, sort order, last connected, preview path, user name, domain,
  `CredentialRef`, and `Settings`.
- `ConnectionType`: `Rdp`, `TeamViewer`, `AnyDesk`, `RustDesk`, `Ssh`, `Sftp`, `Vnc`. Stored by name.
- `Settings` is a typed record per type (`Model/Settings`), stored as JSON in one column, so a new option
  needs no migration:
  - `RdpSettings`: display, multimon, keyboard, NLA, authentication level, admin/restricted admin/Remote
    Credential Guard, gateway, redirections, RemoteApp, start program, external mstsc, jump host.
  - `SshSettings` (SSH and SFTP): auth method, key path, jump host, port forwards, host key, remote folder.
  - `VncSettings`: view only, scaling, quality, external viewer, jump host.
  - `RemoteToolSettings` (TeamViewer, AnyDesk, RustDesk): alias, mode, extra arguments.
  `ConnectionSettings.TypeFor` maps a type to its record; changing `Connection.Type` resets settings of the wrong type.
- `ConnectionGroup`: nested through `ParentId`, with sort order, collapsed state, and its own user, domain,
  and `CredentialRef`.
- `CredentialRef(Source, Key)` points at a secret. **No secret is ever stored in the model or the database.**

## Storage (`Core/Storage`, `Core/Library`)

- `SqliteConnectionStore`: plain SQL on `Microsoft.Data.Sqlite`, file `%LOCALAPPDATA%\RemoteManager\remote-manager.db`.
  Tables `connection_groups`, `connections`, `connection_tags`, `app_settings` (key/value, for tool paths and view state).
- `SqliteMigrator`: numbered scripts in `Storage/Migrations/NNNN_name.sql` (embedded), applied in order;
  the version is `PRAGMA user_version`. To change the schema, add the next number. Never edit a shipped script.
  Today: `0001_initial`, `0002_connection_history`, `0003_sync`. `UpgradeTests` opens databases written by the
  p1-foundation build (`tests/.../Fixtures/Upgrade/p1-foundation.db`) and by the Phase 3 build on main
  (`main-phase3.db`, schema 2) to prove old data upgrades in place.
- `DataStore.OpenAsync`: opens the database and initializes it once. If a `connections.json` from the
  earlier builds exists, it is imported (ids kept, so existing Credential Manager passwords stay attached)
  and renamed to `connections.json.bak`; otherwise sample connections are added. The `store.initialized`
  setting makes sure this happens only once.
- `ConnectionLibrary`: groups and connections in memory, written through to the store on every change.
  Group paths ("Kunde A / Standort 1"), subgroup membership, moving content up when a group is removed.
  Use it from the UI thread.

## Credentials (`Core/Credentials`)

- `ICredentialSource`: async read/write/delete by key. `WindowsCredentialSource` (id `wincred`, target
  `RemoteManager:<key>`) is the writable default. `KeePassCredentialSource` (id `keepass`, key = JSON with
  database, entry, optional key file) and `OnePasswordCredentialSource` (id `1password`, key = `op://…` reference)
  are read-only: the connection stores only the reference, the secret is fetched at connect time. Failures throw
  `CredentialSourceException` (locked, canceled, entry missing, CLI missing); the app turns them into German
  messages (`App/Credentials/CredentialErrors`). Details and library choices: `docs/vnc-and-vaults.md`.
- In the editor, `App/Credentials/PasswordSourcePanel` picks the source; `CredentialService.UseReferenceAsync`
  stores a password-manager reference (and deletes an own Credential Manager password). Groups use the same
  panel (group editor and the editor's group-logon flyout) and `UseGroupReferenceAsync`, so a group's KeePass
  entry or 1Password item is inherited like a stored group password.
- `CredentialService.Resolve(connection)`: the connection's own user/domain/secret when it sets a user or a
  secret, otherwise those of the nearest ancestor group that does. User, domain, and secret are inherited
  together. A connection's own password is stored under its id; a group's under `group-<id>`.

## Status (`Core/Status`)

- `IStatusProbe` per type; `TcpPortProbe` connects to the port: connected = online, refused = offline,
  timeout/DNS/unreachable = unknown (a blocked check must not look like a dead host).
- `StatusChecker` runs probes on the thread pool with a hard timeout per check and bounded parallelism and
  raises `StatusChanged` off the UI thread. The main window marshals results to the UI thread, checks at
  start, every two minutes, and on the refresh button.

## Extension points

Each extension point is an interface plus discovery by `PluginCatalog`: every public, non-abstract class with
a public parameterless constructor is picked up. **Adding an implementation means adding files, not editing
a list**, so parallel work does not collide.

| Extension | Interface | Folder | Discovered in |
| --- | --- | --- | --- |
| Connection type | `IConnectionHandler` (base `ConnectionHandler`) | `App/Connections/<Type>/` | App |
| Type settings UI | `ISettingsEditor` (returned by the handler) | next to the handler | via handler |
| Import | `IImporter` | `Core/ImportExport/` | Core, App |
| Export | `IExporter` | `Core/ImportExport/` | Core, App |
| Credential source | `ICredentialSource` | `Core/Credentials/` | Core, App |
| Schema change | numbered `.sql` file | `Core/Storage/Migrations/` | embedded |
| Start parameter summary (detail panel) | `IStartParameterSummary` | next to the handler | App |

`AppHost` (App) creates everything at startup: library, `CredentialService`, `ConnectionHandlerRegistry`,
importers, exporters, `StatusChecker`. Windows reach it through `AppHost.Current`.

### How a connection type plugs in

The main window, editor, and status checks only talk to `ConnectionHandlerRegistry`. A handler supplies:

- look: `DisplayName` (badge), `Initials`, `AccentColor`;
- editor fields: `HostPlaceholder`, `HasPort`, `UsesUserName`, `PasswordHint`, and `CreateSettingsEditor()`
  for the type's settings record;
- status: `StatusProbe` (or null for "Unbekannt") and `StatusHint`;
- connect: `Open(connection, host)` returns a session window (it must implement `ISessionWindow`; the main
  window tracks it, reuses it on the next open, and closes it with the app) or null after an external launch.
  `IConnectionHost` gives access to copy-password, toasts, `MarkConnected`, and `Save`.

Today: `Rdp/RdpHandler` (embedded `SessionWindow`), `RemoteTools/*` (TeamViewer, AnyDesk, RustDesk; launched
externally, see below), `Vnc/VncHandler` (embedded `VncSessionWindow` on Community.MarcusW.VncClient, external
viewer as fallback), `Ssh/*` (terminal and SFTP, see below). Every type is built now; `PlannedConnectionDialog`
remains for a new type that has no `Open` yet. To build one
out, replace its `Open` and add a settings editor; a brand-new type also needs a `ConnectionType` value and
its settings record in `ConnectionSettings.TypeFor`.

## Overview (`App/Views`)

- **Sections:** in "Alle" and in a group, tiles and list show group headers (`GroupHeaderItem`: chevron, count,
  online summary when collapsed), each followed by its own connections, depth-first; ungrouped connections
  come last under "Keine Gruppe". `SectionWrapPanel` lets a header span a whole tile row. Collapsing is
  `ConnectionGroup.IsCollapsed` (saved; the sidebar tree uses the same flag). Favorites, recent, and tags
  stay flat.
- **List:** column headers sort ascending, descending, then back to the group order (setting `overview.sort`);
  a sorted list is flat. Below 900 px the group and tag columns hide. Both views use multi-select.
- **Drag and drop** (`MainWindow.DragDrop.cs`): connections onto a header or sidebar group (into it) or onto a
  connection (before/after it); a group header onto another header (upper part: before it, otherwise into
  it) or onto "Alle" (top level). `MainWindow.Drop` holds the rules; `ConnectionLibrary.MoveConnections` /
  `MoveGroup` renumber and save the order.
- **Groups** (`MainWindow.Groups.cs`, `GroupEditor`): create, edit (name, parent, description, inherited logon
  with password in the Credential Manager), delete; context menu on headers and sidebar groups, Delete on a
  selected sidebar group. `GroupDeleteDialog` asks once for an empty group; for a group with content it offers
  "move content to the parent" (default) or "delete everything" (`GroupRemoval`). `ConnectionLibrary.RemoveGroup`
  writes the change in one transaction; the next sync sends a tombstone for every removed group and connection.
- **Detail panel** (`ConnectionDetailPanel`): a column, not an overlay. Preview, actions, host, logon (with the
  group it is inherited from), status with check time, start parameters (`IStartParameterSummary`), notes
  (saved when focus leaves), history; bulk actions for several selected connections. Toggle with Strg+I
  (setting `overview.details`).
- **History** (`Core/History/ConnectionHistory`, migration 0002): an entry per logon (`MarkConnected`), ended
  when the session window closes. Feeds the detail panel and the launcher ranking.
- **Quick launcher** (`QuickLauncherWindow`, `Services/GlobalHotkey`): a system-wide hotkey (RegisterHotKey on
  the main window, default Strg+Alt+R, setting `launcher.hotkey`, changeable in the launcher) or Strg+K opens
  it. `Core/Search/FuzzySearch` ranks over name, host, tags, group, description, and type plus usage from the
  history. Enter connects, Strg+Enter shows the connection in the overview, Esc closes.
- **Sidebar note:** FANavigationView re-posts its selection animation until the selected item has an
  indicator; selecting a freshly created or hidden (collapsed parent) item starves the UI thread. The main
  window therefore rebuilds the sidebar only when groups or tags changed, clears the selection before
  replacing items, selects again after layout, and never selects an item inside a collapsed group.

## Live preview (`Core/Preview`, `Views/MainWindow.LivePreview.cs`)

- While an embedded RDP or VNC session is connected, its tile and the detail panel show a picture refreshed every
  N seconds with a "Live · vor 20 s" badge. Interval in the "…" menu › Live-Vorschau (Aus, 10 s, 30 s, 1 min,
  5 min; setting `preview.live.interval` in seconds, default 30, "0" = off). Per connection:
  `RdpSettings.LivePreview` / `VncSettings.LivePreview` ("Live-Vorschau für diese Verbindung", on by default).
- `LivePreviewScheduler` (Core, unit-tested with a fake source) holds the open sessions as `ILivePreviewSource`
  and is ticked every 5 s by a UI timer: it captures sessions that are live, allowed, not minimized, and due;
  never one twice at once; a failed or blank capture keeps the last image. `LiveAt` drives the badge.
- Sources: `SessionWindow.LivePreview.cs` copies the RDP surface with the snapshot path (`SessionCapture`,
  PrintWindow, no focus change) on the UI thread and skips minimized windows (the client stops painting);
  `VncSessionWindow.LivePreview.cs` copies the in-memory frame (also while minimized). `LivePreviewImage` scales
  to cover 528 x 280 (twice the tile) and encodes the PNG on the thread pool, then replaces the preview file in
  one step. A full snapshot (disconnect, close, manual) written meanwhile wins, so the final picture stays.
- SSH terminals have no live preview.

## RDP start (`App/Connections/Rdp`, `Core/Rdp`)

- Embedded: `SessionWindow` puts every `RdpSettings` option onto the MsRdpEx client through
  `RdpClientConfigurator` (server, port, user, NLA, authentication level, admin session, gateway, redirections,
  audio, start program, multimon; Restricted Admin / Remote Credential Guard through `IMsRdpExtendedSettings`)
  and then calls `RdpClientView.ConnectConfigured`. `RdpClientView.Connect` is not used: it overwrites NLA and
  clipboard redirection with its own defaults. Every option is set on every connect (the client is reused).
- External: "Mit mstsc öffnen" and every RemoteApp go through `ExternalRdpLauncher`: `RdpFileMapping.ToRdpFile`
  writes a temporary .rdp file (UTF-16, never a password), Restricted Admin / Remote Credential Guard / multimon /
  admin become mstsc switches, and the stored password is written as `TERMSRV/<host>` (domain password, persisted
  for the logon session only) unless the user already has such an entry. File and entry are removed when mstsc exits.
- The gateway's own password is a `CredentialRef` in `RdpSettings.GatewayCredential` (key `gateway-<guid>`).

## Import and export (`Core/ImportExport`)

| Importer | Reads | Groups | Passwords |
| --- | --- | --- | --- |
| `OneRemoteImporter` | 1Remote `1Remote.db` (table `Servers`, column `Json`), PRemoteM (table `Server`, column `JsonConfigString`), or JSON export (.prma); the database is read from a temporary copy (with its `-wal`), so a running 1Remote does not block it; damaged rows are skipped with a warning | none (1Remote has tags) | not taken over (1Remote's own encryption); an encrypted SSH `PrivateKey` is reported, only a real file path becomes the key path |
| `RdcManImporter` | RDCMan .rdg, schema 1 and 3 | file and groups, nested | DPAPI of the current user, else reported |
| `MRemoteNgImporter` | confCons.xml (also fully encrypted ones) | containers, nested | with mRemoteNG's default master password (AES-GCM, `MRemoteNgCrypto`), else reported |
| `RdpFileImporter` | .rdp | none | never (`password 51:b:` is machine-bound) |
| `JsonBackupImporter` | own backup | ids kept | references only |

`JsonBackupExporter` writes everything (ids, settings, credential references) but no secret. `ImportService`
merges an `ImportResult`: groups with the same name under the same parent are reused, a known id is replaced in
place (backup restore), an identical connection in the same group is skipped, passwords go to the default
credential source. It adds and updates through `ConnectionLibrary.Add/Update` (not `AddRange`, which reloads every
object) so windows that hold a connection keep a live one. The UI is `Views/ImportView` + `ImportExportCommands`
(the "…" button in the overview). The import handlers catch every exception: a file problem is shown in the dialog,
anything else too, plus a crash report. Sample files for the tests are in `tests/RemoteManager.Core.Tests/Samples`.

Inheritance in the editor: the connection editor shows which group logon applies for the group typed right now
(inherited, or replaced by the connection's own) and edits the group's logon in a flyout (`Views/GroupLogonPanel`).

## Remote tools (`Core/RemoteTools`, `App/Connections/RemoteTools`)

- `RemoteToolCommandLine` builds the call per tool from `RemoteToolSettings` (pure, unit-tested): TeamViewer
  `-i ID [-p] [-m fileTransfer|vpn] [-q] [-a]`, AnyDesk `ID [--file-transfer] [--full-screen] [--plain]
  [--with-password]`, RustDesk `--connect|--file-transfer|--port-forward ID [--password]`, then the free
  arguments (`{id}` placeholder, split like a command line). IDs are validated (never start with `-`); free
  arguments with a password switch are rejected. `Preview` masks the password.
- Password route: AnyDesk stdin (never on the command line); TeamViewer/RustDesk the clipboard (cleared after
  30 s); their command line only with `CredentialOnCommandLine`, which shows a warning in the editor and a toast
  at launch. `RemoteToolLauncher` starts through `ProcessStartInfo.ArgumentList`, never a shell.
- `RemoteToolLocator`: path from `app_settings` (`tools.path.<Type>`) if the file exists, else registry (App
  Paths, uninstall entries) and Program Files. Missing tool: `MissingToolDialog` with "Pfad festlegen" and the
  download page. Paths, the TeamViewer token, and the AnyDesk import live in `RemoteToolsDialog` (header button).
- TeamViewer Web API: `TeamViewerApiClient` (`/devices`, `/groups`, bearer script token from the Credential
  Manager key `teamviewer-api-token`). `TeamViewerSync` matches by device id, then remote ID; it updates alias
  (and the name while it equals the old alias) and never notes, tags, description, group, or password. New
  devices go to "Neu aus TeamViewer / <group>"; devices gone from the account are marked `RemovedAtSource`.
  Online status: `CachedRemoteStatusProbe` reads `TeamViewerAccount.StatusCache` and refreshes it at most
  every 90 s while a token is stored. Sync markers are fields of `RemoteToolSettings`, so no migration.
- AnyDesk: `AnyDeskCsvImporter` (`IImporter`) reads the my.anydesk I client export by header; the dialog drops
  IDs that already exist and puts the rest in "Import aus AnyDesk".
- `IFileTransferHandler`: handlers that implement it get "Dateiübertragung" in the context menu.

## SSH, SFTP, and tunnels (`Core/Ssh`, `App/Connections/Ssh`)

- **Core** (SSH.NET, no UI): `SshLogin` is one server's logon resolved from a connection (user and secret
  through `CredentialService`, the secret only in memory). Methods: password (plus keyboard-interactive), key
  file (OpenSSH or PuTTY; the passphrase is the connection's secret), agent. `SshAgent` speaks the agent
  protocol to the Windows OpenSSH agent (named pipe) and to Pageant (WM_COPYDATA); `SshAgentKeySource` plugs
  agent keys into SSH.NET, which has no agent support.
- `SshRoute` chains jump hosts (`SshSettings.JumpHostId`, outermost first via `SshJumpChain`) through local
  forwards, like `ssh -J`; every hop checks its own host key. `Reach(host, port)` gives a local endpoint for
  any TCP target, which is how RDP and VNC run over SSH (`RdpSettings.JumpHostId`, `VncSettings.JumpHostId`).
  `SessionWindow.SshTunnel.cs` and `VncSessionWindow` hold the tunnel for the session; mstsc, RemoteApps, and
  the external VNC viewer get the local end from `SshConnect.OpenTunnelAsync`, which closes when the process exits.
  Connections behind a jump host get no TCP status probe (`SshJumpChain.JumpHostOf`): their host is usually
  not reachable from here and would show as offline.
- Host keys: the accepted `SHA256:` fingerprint is stored in `SshSettings.HostKeyFingerprint`. An unknown or
  changed key aborts the handshake with `SshHostKeyException`; the app asks (`HostKeyWindow`, a separate window
  because the terminal and RDP surfaces are native), stores the key on yes, and connects again.
- **Terminal** (`SshTerminalWindow`): xterm.js 6 in `Avalonia.Controls.WebView` (WebView2, profile in
  `<data>\WebView2`). The page (`Connections/Ssh/Terminal`, copied to `terminal\` in the output) talks JSON
  over `chrome.webview.postMessage`; output goes to it base64 encoded and batched. The page reports
  "ready" only once it has a real size, so the PTY never starts at 2x1 while the view is hidden.
- **SFTP** (`SftpWindow`, also "Dateien" in the terminal): plain Avalonia; transfers in `SftpTransfer`
  (recursive, progress, never overwrites local files). Upload by button or drop from Explorer.
- Tests: `SshLiveTests` and the self-test use the Docker testbed in `tools/ssh-testbed` (jump host, a target
  only reachable through it, optional xrdp desktop and Pageant); without it they are skipped. VNC over SSH
  tunnels from the jump container back to a `FakeVncServer` on this machine (`host.docker.internal`).

## Installer and updates (`Core/Updates`, `App/Updates`)

Velopack: per-user Setup to `%LOCALAPPDATA%\Botis.RemoteManager` (never the data folder), `VelopackApp.Run()` first
in `Program.Main` with auto-apply off. `AppUpdates` holds the source (`REMOTE_MANAGER_UPDATE_SOURCE`, setting
`update.source`, default GitHub Releases), the GitHub token (Credential Manager), the throttled startup check, and
the database backup before applying (`DatabaseBackup`, last three in `<data>\backups`). The overview's notice and
"…" menu entries are in `MainWindow.Updates.cs`, the About dialog in `Views/AboutView`; `IAppUpdater` lets the
self-test use a fake. Releases: `tools/release.ps1` (publish, optional signing, vpk pack, upload). Details,
private-repository options, and the end-to-end check: `docs/updates.md`.

## Device sync (`Core/Sync`, `App/Sync`)

Off until the user turns it on (header button "Synchronisierung"). One encrypted file, `remote-manager.sync`, in a
folder the user picks (OneDrive, Syncthing, a share); every PC merges its own records with it.

- **What syncs** (`SyncFieldMap`): groups and connections with name, type, host, port, description, notes, tags,
  favorite, group/parent, order, user, domain, credential reference, and every type option as its own field
  (`settings.display`). Not synced: secrets (never in the model), Credential Manager contents, status, history,
  last-connected time, previews, collapsed state. Deletions go out as tombstones (a record without fields).
- **File** (`SyncCrypto`): header (magic, version, Argon2id parameters, 16-byte salt, key check, nonce) plus the
  AES-256-GCM encrypted snapshot JSON; the header is authenticated as associated data. Nothing readable outside
  the ciphertext (tests search the bytes for names, hosts, ids, and the passphrase in UTF-8 and UTF-16). The key
  check (HMAC of a fixed label) separates "wrong passphrase" from "damaged file". **KDF: Argon2id**, 64 MiB, 3
  passes, 4 lanes (RFC 9106's second recommendation), via the BouncyCastle package Core already uses for KeePass.
  Chosen over PBKDF2 because it is memory-hard: a stolen sync file (it sits in a cloud folder) cannot be attacked
  cheaply on GPUs, which PBKDF2 at any iteration count still allows. It takes about one second, once per process
  (the key is cached in memory). The parameters are in the header, so they can be raised later. The passphrase
  lives only in the Credential Manager (`RemoteManager:sync-passphrase`); the salt is made with the file.
- **Merge** (`SyncMerge`, pure): per record, three-way between this PC's record, the file's version, and the base
  (what this PC last agreed with the file; table `sync_base`, migration 0003). A change on one side wins; edits
  to different fields merge; the same field edited on both sides, or an edit against a delete, becomes a
  conflict (table `sync_conflicts`). A conflicting field is held back: the file keeps the other value, this PC
  keeps its own, until the user decides. Every written version has a new revision id and the ids it was built
  on (`History`), so a version from a conflict copy or a rolled-back file is recognized as newer, older, or
  parallel; the base also keeps the version it replaced, which gives a parallel copy its common ancestor.
- **Engine** (`SyncEngine`): read and decrypt (retrying a file that a sync client is still writing; a file that
  cannot be read is never overwritten), merge the main file, then every conflict copy (`remote-manager.sync-conflict-…`,
  `remote-manager-PC.sync`, `… conflicted copy …`), write the result if it changed, apply it to the library
  (`ConnectionLibrary.ApplyExternalChanges`, objects updated in place, dangling group references and parallel
  move cycles repaired), store bases and conflicts, delete the merged copies. A record the user edited during the
  write keeps the edit and its old base.
- **Transport** (`ISyncTransport`): today only `FolderSyncTransport`. Writes go to a temp file in the same folder,
  then a rename; right before the rename the file is compared with the version that was read (another device
  wrote: start over). Locked files are retried. A server transport implements the same four methods.
- **App** (`SyncService`): settings `sync.enabled`, `sync.folder`, `sync.device`, `sync.lastSync`; syncs at start,
  four seconds after a change, every five minutes, and on "Jetzt synchronisieren". `SyncDialog` (folder,
  passphrase, what a joining PC does with its own connections: add or replace) checks the passphrase before saving.
  `SyncConflictsWindow` lists conflicts with both versions side by side: "Diese behalten", "Andere behalten",
  and for a connection edited on both PCs "Beide behalten (Kopie)" (the copy gets no password reference).
- **Self-test** (`SelfTest.Sync.cs`): PC A, B, and C as three data folders with one sync folder under %TEMP%,
  passphrases under per-run Credential Manager keys.

## Crash reports (`Core/Diagnostics/CrashLogFile`, `App/Diagnostics/CrashLog`)

Unhandled exceptions on the UI thread (`Dispatcher.UIThread.UnhandledException`), in the AppDomain, and unobserved
tasks are written to `<data>\logs\crash-<time>-<pid>.log` (`AppPaths.Logs`, so `REMOTE_MANAGER_DATA` applies): time,
source, version, OS, runtime, and the exception with its stack. No settings, connection data, or secrets. The last
20 reports are kept. The handlers only log; they do not keep a crashing app alive. The WebView2 start abort that
`SshTerminalWindow` marks as handled is not logged.

## UI text

The UI is German. Every user-facing string is in `App/Resources/Strings.resx`; the `Strings` class is
generated at build time. XAML uses `{x:Static res:Strings.Key}`, code uses `Strings.Key` (with
`string.Format(CultureInfo.CurrentCulture, ...)` for placeholders). Product names (RDP, TeamViewer) and log
lines are not translated. Core has no UI, but its import warnings are shown as they are; they come from
`Core/Resources/ImportStrings.resx` (internal generated class, German as well).

## Self-test isolation

`SelfTest.Isolation.cs`: every Credential Manager target and TERMSRV host the self-test writes ends in a per-run
id (`TestId`), so runs in other worktrees or CI never touch each other's entries. Clipboard checks run under the
session-wide mutex `Local\RemoteManager.SelfTest.Clipboard` and read the clipboard back with retries.

## Spike workarounds that must stay

See `docs/spike-results.md`. In short: one RDP session per top-level window; the RDP surface stays hidden
until connect and while the connect card is shown; `SessionWindow` subscribes to the raw `OnDisconnected`
event itself (MsRdpEx 2026.10.26 never raises `Disconnected` for the v11 control); views are disposed
explicitly on close; native popups (`OverlayPopups` off) so menus draw above the session; periodic in-memory
frames so a server disconnect still leaves a preview.
