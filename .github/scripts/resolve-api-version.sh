#!/usr/bin/env bash
# Resolve InformationalVersion for interfold-api.
# Tag push (api-v*) → strip `api-v` prefix (pin / release identity).
# Otherwise → {last api-v* SemVer core}+{short commit id}; no tags → 0.0.0+{id}.
# Local `dotnet` / Dockerfile default stays 0.0.0-dev — this script is CI-only.
set -euo pipefail

if [[ "${GITHUB_REF_TYPE:-}" == "tag" ]]; then
  v="${GITHUB_REF_NAME:?}"
  if [[ "$v" == api-v* ]]; then
    echo "${v#api-v}"
    exit 0
  fi
  echo "Expected api-v* tag for API version resolve, got '$v'" >&2
  exit 1
fi

sha="${GITHUB_SHA:?}"
commit_id="${sha:0:7}"

base="0.0.0"
if latest_tag="$(git describe --tags --abbrev=0 --match 'api-v*' 2>/dev/null)"; then
  base="${latest_tag#api-v}"
fi

echo "${base}+${commit_id}"
