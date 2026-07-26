#!/usr/bin/env bash
# Shared entrypoint for the Ubuntu/Fedora DinD fixtures.
# Starts dockerd with VFS storage (portable across host overlays) and a TCP socket on 2375 so the
# outer Testcontainers fixture can probe `docker info` over the network.
set -euo pipefail

# Scylla refuses to start unless fs.aio-max-nr is at least 66563. The Docker Desktop /
# WSL2 default (often 65536) is just below the floor and the DinD container - although
# privileged - inherits the host kernel's sysctl tree. Bump it before any child
# container can fail on the floor check. Best-effort: if the sysctl is not writable
# (rootless / hardened host) we keep going; the affected tests will fail loudly later
# instead of silently here.
if [ -w /proc/sys/fs/aio-max-nr ]; then
    current=$(cat /proc/sys/fs/aio-max-nr 2>/dev/null || echo 0)
    if [ "${current:-0}" -lt 1048576 ]; then
        echo 1048576 > /proc/sys/fs/aio-max-nr || true
    fi
fi

mkdir -p /var/lib/docker

dockerd \
    --host=unix:///var/run/docker.sock \
    --host=tcp://0.0.0.0:2375 \
    --storage-driver=vfs \
    --iptables=true &
DOCKERD_PID=$!

# Forward signals so `docker stop` of the outer container shuts dockerd cleanly.
trap 'kill -TERM $DOCKERD_PID; wait $DOCKERD_PID' SIGTERM SIGINT

# Wait for dockerd's socket to exist before tests start probing it.
for i in {1..60}; do
    if docker info >/dev/null 2>&1; then
        break
    fi
    sleep 1
done

wait $DOCKERD_PID
