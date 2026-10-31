# Starts the SSH testbed and sets the RM_SSH_TEST_* variables for the self-test in this session.
# Dot-source it:  . .\tools\ssh-testbed\start.ps1   (stop with .\tools\ssh-testbed\stop.ps1)
#
#   rm-ssh-jump    published on 127.0.0.1:2222, the jump host
#   rm-ssh-target  only on the Docker network rm-ssh-net (host name "target"), reachable through the jump host
#
# The password and the key are generated for each run and live only in this session and the containers.
#Requires -Version 7.3
param([switch]$WithRdp, [switch]$WithPageant)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$work = Join-Path ([IO.Path]::GetTempPath()) 'rm-ssh-testbed'
New-Item -ItemType Directory -Force $work | Out-Null

docker build -q -t rm-ssh-testbed $here | Out-Null
docker rm -f rm-ssh-jump rm-ssh-target rm-ssh-rdp 2>$null | Out-Null
docker network create rm-ssh-net 2>$null | Out-Null

$password = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(18)).Replace('/', 'x').Replace('+', 'y')
$key = Join-Path $work 'id_ed25519'
Remove-Item "$key*" -Force -ErrorAction SilentlyContinue
ssh-keygen -q -t ed25519 -N '' -C rm-ssh-testbed -f $key | Out-Null
$pub = (Get-Content "$key.pub" -Raw).Trim()

foreach ($name in 'rm-ssh-jump', 'rm-ssh-target') {
    $alias = if ($name -eq 'rm-ssh-target') { 'target' } else { 'jump' }
    $publish = if ($name -eq 'rm-ssh-jump') { @('-p', '127.0.0.1:2222:22') } else { @() }
    docker run -d --name $name --hostname $alias --network rm-ssh-net --network-alias $alias @publish `
        -e RM_TEST_PASSWORD=$password -e "RM_TEST_PUBKEY=$pub" rm-ssh-testbed | Out-Null
}

if ($WithRdp) {
    # xrdp desktop, only reachable through the jump host as rdp:3389 (user ubuntu, password ubuntu inside the image).
    docker run -d --name rm-ssh-rdp --hostname rdp --network rm-ssh-net --network-alias rdp --shm-size 1g scottyhardy/docker-remote-desktop:latest | Out-Null
    $env:RM_SSH_TEST_RDP = 'rdp'
}

if ($WithPageant) {
    # Pageant only reads .ppk files; to-ppk.py converts the generated key (needs Python).
    python (Join-Path $here 'to-ppk.py') $key "$key.ppk"
    Start-Process (Join-Path $env:ProgramFiles 'PuTTY\pageant.exe') -ArgumentList "`"$key.ppk`""
}

$env:RM_SSH_TEST_HOST = '127.0.0.1'
$env:RM_SSH_TEST_PORT = '2222'
$env:RM_SSH_TEST_USER = 'rmtest'
$env:RM_SSH_TEST_PASSWORD = $password
$env:RM_SSH_TEST_KEY = $key
$env:RM_SSH_TEST_TARGET = 'target'
Start-Sleep -Seconds 2
Write-Host "SSH testbed up: jump 127.0.0.1:2222, target 'target' behind it, key $key"
