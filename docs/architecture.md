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
  stores a password-manager reference (and deletes an own Credential Manager password).
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
Phase 2), `Vnc/VncHandler` (embedded `VncSessionWindow` on Community.MarcusW.VncClient, external viewer as
fallback), `Ssh/*` (Phase 3). The not-yet-built ones show `PlannedConnectionDialog`. To build one
out, replace its `Open` and add a settings editor; a brand-new type also needs a `ConnectionType` value and
its settings record in `ConnectionSettings.TypeFor`.

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
