# Starts the RDP NLA testbed and sets the RM_SPIKE_* variables for the live RDP checks in this session.
# Dot-source it:  . .\tools\rdp-nla-testbed\start.ps1   (stop with .\tools\rdp-nla-testbed\stop.ps1)
#
#   rm-rdp-nla  published on 127.0.0.1:13389: a FreeRDP server that requires network level authentication
#               (CredSSP with NTLM), like GNOME Remote Desktop and KRdp. One account, one small X desktop.
#
# The password is generated for each run and lives only in this session and the container.
#Requires -Version 7.3
$ErrorActionPreference = 'Stop'
docker build -q -t rm-rdp-nla-testbed $PSScriptRoot | Out-Null
docker rm -f rm-rdp-nla 2>$null | Out-Null

$password = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(18)).Replace('/', 'x').Replace('+', 'y')
docker run -d --name rm-rdp-nla -p 127.0.0.1:13389:3389 -e RM_TEST_USER=rmtest -e RM_TEST_PASSWORD=$password rm-rdp-nla-testbed | Out-Null

$env:RM_SPIKE_HOST = '127.0.0.1'
$env:RM_SPIKE_PORT = '13389'
$env:RM_SPIKE_USER = 'rmtest'
$env:RM_SPIKE_PASSWORD = $password
# The server's certificate is self-signed: connect without the question, so a run needs nobody at the screen.
$env:RM_SPIKE_AUTH_LEVEL = '0'
# It mirrors one screen of fixed size, like GNOME desktop sharing: the picture is scaled, not resized.
$env:RM_SPIKE_FIXED_DESKTOP = '1'
Start-Sleep -Seconds 3
Write-Host 'RDP NLA testbed up: 127.0.0.1:13389, user rmtest'
