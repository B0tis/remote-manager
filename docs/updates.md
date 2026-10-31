# Installer, updates, and releases

Remote-Manager ships with [Velopack](https://velopack.io) (MIT): a per-user `Setup.exe` (no admin rights), delta
updates, and a release folder that any web server, file share, or GitHub release can serve.

## Where things live

| What | Where | Touched by updates? |
| --- | --- | --- |
| Program | `%LOCALAPPDATA%\Botis.RemoteManager\current` (Setup `--installto <dir>` for another folder) | replaced |
| Updater, packages | `%LOCALAPPDATA%\Botis.RemoteManager\Update.exe`, `packages\` | yes |
| **Your data** | `%LOCALAPPDATA%\RemoteManager` (`remote-manager.db`, thumbnails, `backups\`) | **never** |
| Passwords, tokens | Windows Credential Manager (`RemoteManager:*`) | never |
| Velopack log | `%LOCALAPPDATA%\velopack\velopack_Botis.RemoteManager.log` | written |

The package id is `Botis.RemoteManager`, not `RemoteManager`, on purpose: Velopack installs to
`%LOCALAPPDATA%\<package id>` and deletes that folder on uninstall, so it must never be the data folder.
Uninstalling (Settings > Apps, or `Update.exe uninstall`) removes the program and shortcuts and leaves the data;
installing again picks it up unchanged.

## In the app

- **At startup** an installed build checks the update source quietly, at most once every 20 hours (setting
  `update.lastCheck`). Errors (offline, no token) only go to the trace log. A dev build never checks.
- **"…" > "Nach Updates suchen"** checks now and reports "ist aktuell", the error, or shows the notice.
- **Notice** (top right of the overview): "Remote-Manager 0.4.1 ist verfügbar (installiert: 0.4.0)." with
  **Jetzt aktualisieren** and **Später**. "Später" hides it until the next check.
- **Jetzt aktualisieren**: downloads (progress in the notice). If sessions are open it asks first ("Trennen und
  aktualisieren" / "Abbrechen", the default is cancel); it never restarts silently with open sessions. Then it
  closes the session windows, clears a copied password from the clipboard, **backs up the database** to
  `<data>\backups\remote-manager-<UTC time>-v<old version>.db` (SQLite online backup, the newest three are kept),
  and lets Velopack install and restart. If the backup fails, the update does not start.
- After the restart the migrations run as on every start, and a toast confirms the new version.
- Velopack's "apply a downloaded update on the next start" is switched off (`SetAutoApplyOnStartup(false)`), so an
  update is only ever applied from the app, after the backup.
- **"…" > "Über Remote-Manager …"**: version and build (from the assembly version the release script sets; a
  plain build says `0.0.0-dev`), install and data folder, update source, GitHub token.

Code: `Core/Updates` (backup, source parsing, schedule; unit-tested), `App/Updates` (`IAppUpdater`,
`VelopackUpdater`, `AppUpdates`, `AppVersion`), `Views/MainWindow.Updates.cs`, `Views/AboutView`. The self-test
(`SelfTest.Updates.cs`) uses a fake updater.

## Update source

One string, in this order:

1. the environment variable `REMOTE_MANAGER_UPDATE_SOURCE` (for a fleet: set it once by policy; the About dialog
   then only shows it),
2. the setting `update.source` (About dialog),
3. the default: GitHub Releases of `https://github.com/B0tis/remote-manager`.

| Input | Source | Example |
| --- | --- | --- |
| `https://github.com/<owner>/<repo>` | GitHub Releases of that repository | `https://github.com/B0tis/remote-manager` |
| any other `http(s)://` URL | a web folder with the release files | `https://updates.example.org/remote-manager` |
| a full folder path or `file://` URL | a local or network folder with the release files | `\\fileserver\software\remote-manager` |

### The repository is private: what that means for clients

GitHub only serves releases of a private repository to authenticated requests. Without a token every check
ends in "404 (Not Found)" plus a hint in the app. Three ways out:

1. **A token on every machine.** A fine-grained personal access token with read access to *Contents* of this
   repository only. Paste it in "Über Remote-Manager"; it is stored in the Credential Manager
   (`RemoteManager:update-github-token`), never in the database, a file, or the log, and only sent in the
   `Authorization` header to GitHub. Downside: every client holds a credential for the source repository, and
   it has to be rotated on all of them.
2. **A public release repository** (e.g. `B0tis/remote-manager-releases`) that only holds release assets, not
   the source. Clients need no token; point them at it with the About dialog or `REMOTE_MANAGER_UPDATE_SOURCE`,
   and upload there with `tools/release.ps1 -UploadGitHub -RepoUrl https://github.com/B0tis/remote-manager-releases`.
   Anyone can then download the installer.
3. **A folder or web server** you control: `tools/release.ps1 -UploadTo \\server\share\remote-manager`, and set
   the source to that path (or an IIS/nginx URL serving it). No token anywhere; access is whatever the share or
   server allows. This is what the end-to-end test below used.

## Releasing

```powershell
tools\release.ps1 -Version 0.4.1                                       # build + pack into artifacts\releases
tools\release.ps1 -Version 0.4.1 -UploadTo \\fileserver\software\rm    # + fetch previous (delta) and copy there
tools\release.ps1 -Version 0.4.1 -UploadGitHub                         # + GitHub release v0.4.1 (GITHUB_TOKEN or gh auth token)
```

Steps: `dotnet tool restore` (vpk, pinned in `dotnet-tools.json` to the same version as the Velopack package),
`dotnet publish` Release win-x64 self-contained with `-p:Version=<version>`, signing (below), `vpk pack`
(full package, delta against the previous release in the output folder, `Botis.RemoteManager-win-Setup.exe`,
portable zip), then the upload. Use `-OutputDir` on a drive with space: publish output (~250 MB) is written next
to it. The GitHub token reaches vpk through `VPK_TOKEN`, never as an argument.

### Signing (Authenticode)

On when a certificate is configured, off otherwise; the log says `Signing: ON (certificate …)` or
`Signing: OFF (…) This build is unsigned.`

| Variable | Credential Manager instead | Meaning |
| --- | --- | --- |
| `RM_SIGN_THUMBPRINT` | `RemoteManager:release-sign-thumbprint` (password = thumbprint) | certificate in `Cert:\CurrentUser\My` or `LocalMachine\My` |
| `RM_SIGN_PFX` + `RM_SIGN_PFX_PASSWORD` | `RemoteManager:release-sign-pfx` (user = .pfx path, password = .pfx password) | a .pfx file |
| `RM_SIGN_TIMESTAMP_URL` | | timestamp server, default `http://timestamp.digicert.com` |

Store the PFX password without typing it on a command line (cmdkey prompts when `/pass` has no value):

```
cmdkey /generic:RemoteManager:release-sign-pfx /user:C:\certs\botis.pfx /pass
```

A .pfx is imported into `Cert:\CurrentUser\My` for the run and removed afterwards, so signtool selects it by
thumbprint and the password never appears in a process command line. vpk signs every unsigned exe/dll before
packing and `Setup.exe` after.

### GitHub Actions

`.github/workflows/release.yml` runs the same script on a `v*.*.*` tag or by hand (secrets `SIGN_PFX_BASE64` and
`SIGN_PFX_PASSWORD` for signing). Actions does not start on this account at the moment (billing), so that
workflow is untested; the local script is the verified path.

## End-to-end check (how the update was verified)

On one machine, everything outside the real data folder (`REMOTE_MANAGER_DATA` and the source variable set for
the processes, Setup `--installto`):

1. `release.ps1 -Version 0.4.0 -UploadTo <feed>`; `Setup.exe --silent --installto <install>`; start, close.
2. Add test data (groups, subgroups, connections of five types with tags, notes, settings) and dump every row
   of every table.
3. `release.ps1 -Version 0.4.1 -UploadTo <feed>` (78 KB delta package).
4. Start 0.4.0, open an RDP session, "Nach Updates suchen", notice, "Jetzt aktualisieren": the app asks because
   of the open session; after "Trennen und aktualisieren" it restarts as 0.4.1 (process path and file version),
   the toast confirms, About shows 0.4.1, `backups\` holds the 0.4.0 copy, and the row dump is identical to step 2.
5. `release.ps1 -Version 0.4.2`, update again from 0.4.1 without sessions: 0.4.2 runs, two backups, rows identical.
6. `Update.exe uninstall --silent`: program, shortcuts, and uninstall entry gone, data identical; Setup again:
   the data opens unchanged.

The app was driven through UI Automation (the same buttons and menu items a user clicks); the real
`%LOCALAPPDATA%\RemoteManager` was hashed before and after and did not change.
