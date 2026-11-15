#!/bin/sh
# RM_TEST_USER / RM_TEST_PASSWORD: the only account the server accepts (NTLM hash in a SAM file).
set -e
: "${RM_TEST_USER:?}" "${RM_TEST_PASSWORD:?}"
winpr-hash3 -u "$RM_TEST_USER" -p "$RM_TEST_PASSWORD" -f sam > /tmp/sam
Xvfb :0 -screen 0 1280x800x24 -nolisten tcp &
sleep 1
export DISPLAY=:0
xsetroot -solid '#1f4f5a'
openbox &
xterm -geometry 96x24+60+60 -fa Monospace -fs 11 -e sh -c 'echo "Remote-Manager NLA testbed (FreeRDP server)"; exec sh' &
exec freerdp-shadow-cli3 /port:3389 /sec:nla /sam-file:/tmp/sam
