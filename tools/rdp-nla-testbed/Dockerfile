# A FreeRDP server that requires network level authentication (CredSSP with NTLM), like GNOME Remote Desktop
# and KDE's KRdp, which are built on the same library. It shares a small X desktop (Xvfb).
FROM ubuntu:24.04
RUN apt-get update \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
      freerdp3-shadow-x11 winpr3-utils xvfb openbox xterm x11-xserver-utils \
 && rm -rf /var/lib/apt/lists/*
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh
EXPOSE 3389
ENTRYPOINT ["/entrypoint.sh"]
