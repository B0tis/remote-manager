# Sets the RM_SSH_TEST_* variables for an already running testbed (for example in a new shell).
# Dot-source it:  . .\tools\ssh-testbed\env.ps1
$ErrorActionPreference = 'Stop'
$vars = docker inspect rm-ssh-jump --format '{{range .Config.Env}}{{println .}}{{end}}'
$password = ($vars | Where-Object { $_ -like 'RM_TEST_PASSWORD=*' }) -replace '^RM_TEST_PASSWORD=', ''
$env:RM_SSH_TEST_HOST = '127.0.0.1'
$env:RM_SSH_TEST_PORT = '2222'
$env:RM_SSH_TEST_USER = 'rmtest'
$env:RM_SSH_TEST_PASSWORD = $password
$env:RM_SSH_TEST_KEY = Join-Path ([IO.Path]::GetTempPath()) 'rm-ssh-testbed\id_ed25519'
$env:RM_SSH_TEST_TARGET = 'target'
if ((docker ps -q -f name=rm-ssh-rdp) -ne $null) { $env:RM_SSH_TEST_RDP = 'rdp' }
