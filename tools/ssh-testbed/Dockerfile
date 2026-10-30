# Throwaway OpenSSH server for Remote-Manager's SSH/SFTP self-test (see README.md). Not for production.
FROM alpine:3.22
RUN apk add --no-cache openssh openssh-sftp-server htop vim ncurses-terminfo-base bash \
    && ssh-keygen -A \
    && adduser -D -s /bin/bash rmtest \
    && mkdir -p /home/rmtest/.ssh && chown rmtest:rmtest /home/rmtest/.ssh && chmod 700 /home/rmtest/.ssh \
    && printf '%s\n' \
        'PasswordAuthentication yes' \
        'KbdInteractiveAuthentication yes' \
        'PubkeyAuthentication yes' \
        'AllowTcpForwarding yes' \
        'GatewayPorts no' \
        'Subsystem sftp internal-sftp' \
        > /etc/ssh/sshd_config.d/rmtest.conf \
    && sed -i 's|^Subsystem.*sftp-server.*||' /etc/ssh/sshd_config
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh
EXPOSE 22
ENTRYPOINT ["/entrypoint.sh"]
