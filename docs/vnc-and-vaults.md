# VNC and password managers (Phase 3)

This page records the library choices for embedded VNC and for the KeePass and 1Password credential sources,
and how the pieces work.

## Embedded VNC

### Choice: Community.MarcusW.VncClient (core only), with our own Avalonia 12 view

| Candidate | License | Status (Oct 2026) | Verdict |
| --- | --- | --- | --- |
| **Community.MarcusW.VncClient** 2.0.8 | MIT | Maintained fork of MarcusW.VncClient; targets `net10.0`; last release Sept 2026 | **Chosen** |
| MarcusW.VncClient (original) | MIT | No NuGet release for 3+ years; Avalonia adapter only for Avalonia 0.10 | Superseded by the fork |
| Community.MarcusW.VncClient.Avalonia | MIT | Targets Avalonia 11; we are on Avalonia 12 | Not used; its view is ported instead |
| RemoteViewing | MIT | Old, few encodings (no Tight/ZRLE), WinForms control | Too limited |
| VncSharp / VncSharpCore | GPL | Old, WinForms | License |
| noVNC in a WebView | MPL-2.0 | Needs a WebSocket proxy (websockify) | Extra moving parts |
| Own NativeControlHost around a native viewer | varies | Airspace issues like RDP, no clean embedding API | Not worth it |

Why this one:

- **License**: MIT, like Avalonia, FluentAvalonia, and MsRdpEx. Its only dependencies are
  BouncyCastle.Cryptography (MIT) and Microsoft.Extensions.Logging.Abstractions (MIT).
- **Protocol coverage**: RFB 3.3–3.8; Raw, CopyRect, zlib, ZRLE, Tight (JPEG); security types None, VNC Auth,
  VeNCrypt, TLS, RA2/RA2ne (RealVNC), Ultra, and more; continuous updates, fence, desktop resize. The README says it
  was tested against TigerVNC, LibVNCServer (x11vnc), RealVNC, Vino, and UltraVNC.
- **Platform split**: the core library has no UI dependency. Rendering is an interface (`IRenderTarget`), so the
  Avalonia-version problem disappears: `App/Connections/Vnc/VncView.cs` is our own control (based on the MIT
  adapter) that decodes into a `WriteableBitmap`, scales it to the window or shows it 1:1 with scrollbars, and
  sends mouse, wheel, keyboard, and Ctrl+Alt+Del. `VncKeyMapping.cs` is the adapter's key table, unchanged apart from the
  namespace (MIT notice in the file).
- **No airspace**: unlike the RDP ActiveX control, the VNC view is a normal Avalonia control. Menus, dialogs,
  and the connect card can draw above it.

What we verified (self-test and by hand):

- The self-test runs an in-process RFB 3.8 server (`Diagnostics/FakeVncServer.cs`: VNC authentication, raw
  encoding, a four-color test pattern) and checks: connect with the password from KeePass, every quadrant's pixel
  color, remote size, pointer mapping when scaled, Ctrl+Alt+Del arriving as Control_L/Alt_L/Delete, "Nur ansehen"
  sending nothing, missing and wrong password messages, refused port, preview tile on close.
- Against a real TigerVNC server (`Xvnc` in an Alpine container with xterm and busybox `top`), with
  `RM_VNC_HOST=127.0.0.1 RM_VNC_PORT=15900 RM_VNC_PASSWORD=… RM_VNC_KEYS=N`: the session negotiates VNC Auth and
  ZRLE, renders 1280 × 800, and the typed `N` reaches `top` (the process list is re-sorted by PID).
- Not covered by an automated check: mouse buttons and wheel against a server (the code is the ported adapter's;
  only the pointer-to-pixel mapping is tested), and servers that need VeNCrypt/TLS or RealVNC's RA2.

### External viewer fallback

`ExternalVncViewer` starts the first viewer it finds: the path in the `vnc.viewerPath` setting, then TightVNC
(`tvnviewer.exe`), TigerVNC, UltraVNC, RealVNC Viewer. The target is passed as `host::port` (all four understand
it), plus the viewer's view-only switch when "Nur ansehen" is set. **The password is never put on the command
line**, because every process on the machine can read command lines. When one is stored it goes to the clipboard
(not saved in clipboard history, cleared after 30 s) for the viewer's own password prompt. With no viewer
installed, the user gets a clear message. The external viewer is used when the connection says "Externen
VNC-Viewer verwenden", or via "Extern öffnen" in the session window and on its connect card.

### Password handling

The password is read from its source (Credential Manager, KeePass, 1Password) **only when the server asks for
one** (`IAuthenticationHandler`). It stays in memory only so the library can reconnect this window after a
network drop; closing the window drops it. A password typed into the connect card is used once and never saved.

## Password managers as credential sources

Both are `ICredentialSource` implementations in `Core/Credentials/`, found by `PluginCatalog`, read-only
(`IsWritable = false`). A connection stores only a `CredentialRef(source, key)`; **the key is a reference, never
a secret**, and the secret is fetched at connect time.

### KeePass (`keepass`)

Reference key (JSON): `{"database":"C:\\…\\team.kdbx","entry":"Kunden/Server 01","keyFile":"…","field":"…"}`.
`entry` may be the path below the root group (`Gruppe/Untergruppe/Titel`, with or without the root group's name),
a title that is unique in the database, or the entry's UUID. `keyFile` and `field` (default: the password) are optional.

Choice: **our own read-only KDBX reader** (`Core/Credentials/KeePass/`, about 600 lines) on top of
BouncyCastle.Cryptography (MIT) for Argon2, ChaCha20, Salsa20, and Twofish. The alternatives:

| Candidate | License | Verdict |
| --- | --- | --- |
| KeePassLib (from KeePass 2.x) and its NuGet repackagings | GPL-2.0 | Would make the app GPL |
| Pidamg.KeePass.Kdbx | MIT | Preview (0.1.x), no users, only on GitHub Packages |
| keepassxc-cli as an external tool | GPL (separate process) | Needs KeePassXC installed, and the master password must be piped in |
| **Own reader + BouncyCastle** | MIT | **Chosen**: small, read-only, no new license constraints |

Supported: KDBX 3.1 and 4.0/4.1; AES-256, ChaCha20, Twofish; AES-KDF, Argon2d, Argon2id; gzip or uncompressed;
master password and/or key file (KeePass XML 1.0 and 2.0, 32 raw bytes, 64 hex characters, any other file as hash).
The recycle bin and entry history are skipped. Header HMAC and block HMACs (KDBX 4) and block hashes (KDBX 3) are
checked, so a wrong master password or a damaged file is reported as such. Not supported: field references
(`{REF:…}`), the Windows user account as a key component (KeePass "Windows-Benutzerkonto"), KeePassXC challenge-response (YubiKey).

Tests: `tests/RemoteManager.Core.Tests/KeePassTests.cs` reads eight fixture databases covering every
cipher/KDF/version combination. The fixtures were made with pykeepass (`Fixtures/KeePass/make_fixtures.py`):
pykeepass's own test databases are used only as format templates; groups, entries, master password
(`RemoteManager-Test`, test data only), and key file are ours. During development the reader was also run
against pykeepass's original test databases, including the legacy hex key file.

The master password is asked at connect time by `MasterPasswordWindow` (its own top-level window, so it also
works above an RDP session). **Only the composite key (a SHA-256 hash) stays in memory**, for 10 minutes after the
last use, so several connects in a row ask once; the master password itself is cleared right away. Nothing
decrypted is cached: every read opens the file again, so changes in KeePass are picked up. Quitting the app
forgets the key. A wrong master password asks again with a message; "Abbrechen" cancels the connect.

In the connection editor: "Passwort aus KeePass (.kdbx)", database and key file with "Durchsuchen …", the entry
typed or picked with "Auswählen …" (a list of titles, groups, and user names; never passwords), and "Prüfen"
(reads the secret once and drops it). The last database is remembered as the default (`keepass.lastDatabase`).

### 1Password (`1password`)

Reference key: the 1Password secret reference itself, `op://Tresor/Eintrag/Feld` (also with a section or
`?attribute=otp`). Read with the official CLI: `op read --no-newline op://…`. The secret travels over the CLI's
stdout into memory; the command line only carries the reference. `op` is found on PATH, in the WinGet links folder,
or in `Program Files\1Password CLI`. Sign-in and approval are 1Password's own (desktop app integration with
Windows Hello, or `op signin`). stdin is closed, so the CLI cannot hang waiting for a terminal prompt. Reads time out
after 2 minutes so the user has time to approve. Errors are mapped to clear German messages: CLI missing (with the
`winget install AgileBits.1Password.CLI` hint), not signed in, item not found, more than one match, approval dismissed.

Tests: `OnePasswordTests.cs` runs a fake `op` (batch file) through the real process plumbing; the self-test does
the same through the editor and checks that the secret is not on the fake op's command line.

### Not covered yet

- Group credentials (inherited by the connections of a group) can still only use the app's own password; the
  KeePass/1Password choice is per connection.
- The KeePass entry's user name is not used automatically (RDP keeps the user from the connection or group).
- No settings page yet for the KeePass unlock duration or a fixed VNC viewer path (`vnc.viewerPath` exists as a
  setting key without UI).
- VNC through an SSH jump host comes with the SSH tunnel work (`VncSettings.JumpHostId` is kept, not used).
