#!/usr/bin/env bash
# Boot a published Interfold.Api.Host native binary and probe /health/live.
# Used by the publish-aot-smoke CI job. Secrets are ephemeral smoke keys.
set -euo pipefail

bin="${1:?path to Interfold.Api.Host native binary}"
port="${2:-5100}"
url="http://127.0.0.1:${port}/health/live"

if [[ ! -f "$bin" ]]; then
  echo "AOT binary not found: $bin" >&2
  exit 1
fi
chmod +x "$bin"

workdir="$(mktemp -d)"
pid=""
cleanup() {
  if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
    kill "$pid" || true
    wait "$pid" || true
  fi
  rm -rf "$workdir"
}
trap cleanup EXIT

openssl ecparam -name prime256v1 -genkey -noout -out "$workdir/es256.pem"
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 -out "$workdir/rsa.pem"

export ASPNETCORE_ENVIRONMENT=Development
export ASPNETCORE_URLS="http://127.0.0.1:${port}"
export OCTOCON_PERSISTENCE=inmemory
export OCTOCON_INMEMORY_SECRETS_SEED__ENCRYPTION_PEPPER="aot-smoke-pepper"
export OCTOCON_INMEMORY_SECRETS_SEED__AUTH_DEEP_LINK_SECRET="aot-smoke-deep-link-secret-0123456789ab"
export OCTOCON_INMEMORY_SECRETS_SEED__AUTH_JWT_ES256_PRIVATE_PEM
export OCTOCON_INMEMORY_SECRETS_SEED__AUTH_JWT_RSA256_PRIVATE_PEM
OCTOCON_INMEMORY_SECRETS_SEED__AUTH_JWT_ES256_PRIVATE_PEM="$(cat "$workdir/es256.pem")"
OCTOCON_INMEMORY_SECRETS_SEED__AUTH_JWT_RSA256_PRIVATE_PEM="$(cat "$workdir/rsa.pem")"

"$bin" &
pid=$!

for _ in $(seq 1 60); do
  if curl -fsS "$url"; then
    echo
    echo "AOT smoke OK: $url"
    exit 0
  fi
  if ! kill -0 "$pid" 2>/dev/null; then
    echo "API host exited before becoming live" >&2
    wait "$pid" || true
    exit 1
  fi
  sleep 1
done

echo "Timed out waiting for $url" >&2
exit 1
