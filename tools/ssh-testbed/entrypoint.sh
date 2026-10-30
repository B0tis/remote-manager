#!/bin/sh
# Password and public key come from the environment at container start, never from the image.
set -e
if [ -n "$RM_TEST_PASSWORD" ]; then echo "rmtest:$RM_TEST_PASSWORD" | chpasswd; fi
if [ -n "$RM_TEST_PUBKEY" ]; then
  echo "$RM_TEST_PUBKEY" > /home/rmtest/.ssh/authorized_keys
  chown rmtest:rmtest /home/rmtest/.ssh/authorized_keys && chmod 600 /home/rmtest/.ssh/authorized_keys
fi
exec /usr/sbin/sshd -D -e
