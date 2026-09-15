#!/usr/bin/env bash
# Resolve InformationalVersion for interfold-bootstrap.
# Tag push  → strip leading v from the tag (pin identity).
# Otherwise → {last v* tag without v}+{short commit id}; no tags → 0.0.0+{id}.
# Local `dotnet` builds keep the csproj default (0.0.0-dev) — this script is CI-only.
set -euo pipefail

if [[ "${GITHUB_REF_TYPE:-}" == "tag" ]]; then
  v="${GITHUB_REF_NAME:?}"
  echo "${v#v}"
  exit 0
fi

sha="${GITHUB_SHA:?}"
commit_id="${sha:0:7}"

base="0.0.0"
if latest_tag="$(git describe --tags --abbrev=0 --match 'v*' 2>/dev/null)"; then
  base="${latest_tag#v}"
fi

echo "${base}+${commit_id}"
