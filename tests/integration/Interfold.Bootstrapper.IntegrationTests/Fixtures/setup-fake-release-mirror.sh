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
# Real ELF payload so a subsequent update-self --rollback remains runnable.
cp -f "$shared_bootstrapper" /tmp/interfold-bootstrap
chmod +x /tmp/interfold-bootstrap
tar -czf "$release_root/latest/interfold-bootstrap-linux-x64.tar.gz" -C /tmp interfold-bootstrap
hash="$(sha256sum "$release_root/latest/interfold-bootstrap-linux-x64.tar.gz" | awk '{print $1}')"
echo "$hash  interfold-bootstrap-linux-x64.tar.gz" > "$release_root/latest/SHA256SUMS"
python3 -m http.server "$port" --directory "$release_root" >"$log_file" 2>&1 &
echo $! > "$pid_file"
