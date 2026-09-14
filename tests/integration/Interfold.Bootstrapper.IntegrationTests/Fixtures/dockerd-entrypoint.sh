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

mkdir -p /var/lib/docker /etc/docker

# Bootstrapper stacks emit four compose networks each (postgres, scylla, edge-api,
# edge-web). A full explicit-test session inside one session-shared DinD exhausts
# Docker Desktop's default pool without extra /16 allocations.
DEFAULT_ADDRESS_POOLS='[
  {"base":"172.17.0.0/16","size":24},
  {"base":"172.18.0.0/16","size":24},
  {"base":"172.19.0.0/16","size":24},
  {"base":"172.20.0.0/16","size":24},
  {"base":"172.21.0.0/16","size":24},
  {"base":"172.22.0.0/16","size":24},
  {"base":"172.23.0.0/16","size":24},
  {"base":"172.24.0.0/16","size":24}
]'

# Default: vfs — portable across hosts whose overlay2 setup fights nested mounts.
# Opt-in containerd image store (DIND_CONTAINERD_SNAPSHOTTER=1) reproduces the
# "compose images → No such image after local tag rebuild" failure surface that
# UpdateImagesPhase must tolerate (compose#14014). Cassandra-mode DinD enables this.
if [ "${DIND_CONTAINERD_SNAPSHOTTER:-0}" = "1" ]; then
    printf '%s\n' "{\"features\":{\"containerd-snapshotter\":true},\"default-address-pools\":${DEFAULT_ADDRESS_POOLS}}" \
        > /etc/docker/daemon.json
else
    printf '%s\n' "{\"storage-driver\":\"vfs\",\"default-address-pools\":${DEFAULT_ADDRESS_POOLS}}" \
        > /etc/docker/daemon.json
fi

dockerd \
    --host=unix:///var/run/docker.sock \
    --host=tcp://0.0.0.0:2375 \
    --config-file=/etc/docker/daemon.json \
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
