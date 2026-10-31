# Removes the SSH testbed containers and network and clears the RM_SSH_TEST_* variables.
docker rm -f rm-ssh-jump rm-ssh-target rm-ssh-rdp 2>$null | Out-Null
docker network rm rm-ssh-net 2>$null | Out-Null
Remove-Item Env:RM_SSH_TEST_* -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([IO.Path]::GetTempPath()) 'rm-ssh-testbed') -Recurse -Force -ErrorAction SilentlyContinue
