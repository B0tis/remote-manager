# Removes the RDP NLA testbed container and clears the RM_SPIKE_* variables.
docker rm -f rm-rdp-nla 2>$null | Out-Null
Remove-Item Env:RM_SPIKE_* -ErrorAction SilentlyContinue
