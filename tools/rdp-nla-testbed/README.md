# RDP NLA testbed

A throwaway RDP server in Docker for the live RDP checks (`--selftest` and `--livetest`). It is a FreeRDP
server that requires network level authentication (CredSSP with NTLM over TLS 1.3), the same stack GNOME Remote
Desktop and KDE's KRdp are built on, so it stands in for a Linux desktop without one. Needs Docker Desktop and
PowerShell 7.3+.

```powershell
. .\tools\rdp-nla-testbed\start.ps1
src\RemoteManager.App\bin\Debug\net10.0-windows\RemoteManager.App.exe --livetest   # only the live RDP checks
.\tools\rdp-nla-testbed\stop.ps1
```

| Variable | Value |
| --- | --- |
| `RM_SPIKE_HOST`, `RM_SPIKE_PORT` | `127.0.0.1`, `13389` |
| `RM_SPIKE_USER`, `RM_SPIKE_PASSWORD` | `rmtest` and the generated password |
| `RM_SPIKE_AUTH_LEVEL` | `0`: connect without the certificate question (the certificate is self-signed) |

Each start generates a new random password; it is passed to the container at start and never written to the
repository. The live checks log every dialog the RDP control opens by itself; `RM_SPIKE_CLOSE_DIALOGS=1` closes
them (answers "no"), so an unattended run cannot hang on one.

To run the same checks against one of your own saved connections, with the password the app has stored for it:
set `RM_SPIKE_CONNECTION` to its name or id instead of the variables above.
