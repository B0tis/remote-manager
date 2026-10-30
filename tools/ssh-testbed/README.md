# SSH testbed

Throwaway OpenSSH servers in Docker for the SSH/SFTP tests (`SshLiveTests` in `RemoteManager.Core.Tests`
and the SSH part of `--selftest`). Needs Docker Desktop and PowerShell 7.3+.

```powershell
. .\tools\ssh-testbed\start.ps1                # jump host + target
. .\tools\ssh-testbed\start.ps1 -WithRdp       # plus an xrdp desktop behind the jump host (RDP over SSH)
. .\tools\ssh-testbed\start.ps1 -WithPageant   # plus Pageant with the test key (PuTTY and Python)
. .\tools\ssh-testbed\env.ps1                  # variables again, in a new shell
.\tools\ssh-testbed\stop.ps1
```

| Container | Reachable at | Purpose |
| --- | --- | --- |
| `rm-ssh-jump` | `127.0.0.1:2222` | Jump host; password and key logon, TCP forwarding allowed |
| `rm-ssh-target` | `target:22`, only through the jump host | SSH/SFTP behind a jump host; htop and vim installed |
| `rm-ssh-rdp` (`-WithRdp`) | `rdp:3389`, only through the jump host | xrdp desktop (`scottyhardy/docker-remote-desktop`, user `ubuntu`) |

Each start generates a new random password and ed25519 key (in `%TEMP%\rm-ssh-testbed`); they are passed to
the containers at start and never written to the repository. The scripts set:

| Variable | Value |
| --- | --- |
| `RM_SSH_TEST_HOST`, `RM_SSH_TEST_PORT` | `127.0.0.1`, `2222` |
| `RM_SSH_TEST_USER`, `RM_SSH_TEST_PASSWORD` | `rmtest` and the generated password |
| `RM_SSH_TEST_KEY` | path of the generated private key |
| `RM_SSH_TEST_TARGET` | `target` |
| `RM_SSH_TEST_RDP` | `rdp` (only with `-WithRdp`) |

Without these variables the live tests are skipped and the self-test logs `SKIP SSH live checks`.
