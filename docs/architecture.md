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
- `DataStore.OpenAsync`: opens the database and initializes it once. If a `connections.json` from the
  earlier builds exists, it is imported (ids kept, so existing Credential Manager passwords stay attached)
  and renamed to `connections.json.bak`; otherwise sample connections are added. The `store.initialized`
  setting makes sure this happens only once.
- `ConnectionLibrary`: groups and connections in memory, written through to the store on every change.
  Group paths ("Kunde A / Standort 1"), subgroup membership, moving content up when a group is removed.
  Use it from the UI thread.

## Credentials (`Core/Credentials`)

- `ICredentialSource`: async read/write/delete by key. `WindowsCredentialSource` (id `wincred`, target
  `RemoteManager:<key>`) is the only one now; KeePass and 1Password are planned as further sources.
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

Today: `Rdp/RdpHandler` (embedded `SessionWindow`), `RemoteTools/*` (TeamViewer, AnyDesk, RustDesk; launch in
Phase 2), `Ssh/*` and `Vnc/*` (Phase 3). The not-yet-built ones show `PlannedConnectionDialog`. To build one
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
  with password in the Credential Manager), delete (content moves up); context menu on headers and sidebar groups.
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
| `OneRemoteImporter` | 1Remote/PRemoteM `1Remote.db` (table `Server`, JSON per row) or JSON export (.prma) | none (1Remote has tags) | not taken over (1Remote's own encryption) |
| `RdcManImporter` | RDCMan .rdg, schema 1 and 3 | file and groups, nested | DPAPI of the current user, else reported |
| `MRemoteNgImporter` | confCons.xml (also fully encrypted ones) | containers, nested | with mRemoteNG's default master password (AES-GCM, `MRemoteNgCrypto`), else reported |
| `RdpFileImporter` | .rdp | none | never (`password 51:b:` is machine-bound) |
| `JsonBackupImporter` | own backup | ids kept | references only |

`JsonBackupExporter` writes everything (ids, settings, credential references) but no secret. `ImportService`
merges an `ImportResult`: groups with the same name under the same parent are reused, a known id is replaced in
place (backup restore), an identical connection in the same group is skipped, passwords go to the default
credential source. It adds and updates through `ConnectionLibrary.Add/Update` (not `AddRange`, which reloads every
object) so windows that hold a connection keep a live one. The UI is `Views/ImportView` + `ImportExportCommands`
(the "…" button in the overview). Sample files for the tests are in `tests/RemoteManager.Core.Tests/Samples`.

Inheritance in the editor: the connection editor shows which group logon applies for the group typed right now
(inherited, or replaced by the connection's own) and edits the group's logon in a flyout (`Views/GroupLogonPanel`).

## UI text

The UI is German. Every user-facing string is in `App/Resources/Strings.resx`; the `Strings` class is
generated at build time. XAML uses `{x:Static res:Strings.Key}`, code uses `Strings.Key` (with
`string.Format(CultureInfo.CurrentCulture, ...)` for placeholders). Product names (RDP, TeamViewer) and log
lines are not translated.

## Spike workarounds that must stay

See `docs/spike-results.md`. In short: one RDP session per top-level window; the RDP surface stays hidden
until connect and while the connect card is shown; `SessionWindow` subscribes to the raw `OnDisconnected`
event itself (MsRdpEx 2026.10.26 never raises `Disconnected` for the v11 control); views are disposed
explicitly on close; native popups (`OverlayPopups` off) so menus draw above the session; periodic in-memory
frames so a server disconnect still leaves a preview.
