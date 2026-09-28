#!/usr/bin/env bash
# Stage a private bootstrapper copy + local HTTP release mirror for update-self DinD tests.
# Args: shared_bootstrapper private_bootstrapper release_root port pid_file log_file
set -euo pipefail

shared_bootstrapper="${1:?}"
private_bootstrapper="${2:?}"
release_root="${3:?}"
port="${4:?}"
pid_file="${5:?}"
log_file="${6:?}"

cp -f "$shared_bootstrapper" "$private_bootstrapper"
chmod +x "$private_bootstrapper"
rm -rf "$release_root"
mkdir -p "$release_root/latest"
echo '9.9.9-test' > "$release_root/latest/version.txt"
# Stage under this mirror's release_root so parallel SelfUpdate DinD tests
# do not race a shared /tmp/interfold-bootstrap while tar is reading it.
stage_dir="$release_root/.stage"
mkdir -p "$stage_dir"
cp -f "$shared_bootstrapper" "$stage_dir/interfold-bootstrap"
chmod +x "$stage_dir/interfold-bootstrap"
tar -czf "$release_root/latest/interfold-bootstrap-linux-x64.tar.gz" -C "$stage_dir" interfold-bootstrap
rm -rf "$stage_dir"
hash="$(sha256sum "$release_root/latest/interfold-bootstrap-linux-x64.tar.gz" | awk '{print $1}')"
echo "$hash  interfold-bootstrap-linux-x64.tar.gz" > "$release_root/latest/SHA256SUMS"
python3 -m http.server "$port" --directory "$release_root" >"$log_file" 2>&1 &
echo $! > "$pid_file"

# Wait until the listener accepts connections — otherwise update-self races Connection refused.
for _ in $(seq 1 30); do
  if curl -sf "http://127.0.0.1:${port}/latest/version.txt" >/dev/null; then
    exit 0
  fi
  sleep 0.2
done
echo "fake release HTTP server failed to become ready on port ${port}" >&2
cat "$log_file" >&2 || true
kill "$(cat "$pid_file")" 2>/dev/null || true
exit 1
