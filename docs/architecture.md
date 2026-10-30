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
Phase 2), `Ssh/*` (terminal and SFTP, see below) and `Vnc/*` (Phase 3). The not-yet-built ones show `PlannedConnectionDialog`. To build one
out, replace its `Open` and add a settings editor; a brand-new type also needs a `ConnectionType` value and
its settings record in `ConnectionSettings.TypeFor`.

## SSH, SFTP, and tunnels (`Core/Ssh`, `App/Connections/Ssh`)

- **Core** (SSH.NET, no UI): `SshLogin` is one server's logon resolved from a connection (user and secret
  through `CredentialService`, the secret only in memory). Methods: password (plus keyboard-interactive), key
  file (OpenSSH or PuTTY; the passphrase is the connection's secret), agent. `SshAgent` speaks the agent
  protocol to the Windows OpenSSH agent (named pipe) and to Pageant (WM_COPYDATA); `SshAgentKeySource` plugs
  agent keys into SSH.NET, which has no agent support.
- `SshRoute` chains jump hosts (`SshSettings.JumpHostId`, outermost first via `SshJumpChain`) through local
  forwards, like `ssh -J`; every hop checks its own host key. `Reach(host, port)` gives a local endpoint for
  any TCP target, which is how RDP runs over SSH (`RdpSettings.JumpHostId`, `SessionWindow.SshTunnel.cs`).
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
  only reachable through it, optional xrdp desktop and Pageant); without it they are skipped.

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
