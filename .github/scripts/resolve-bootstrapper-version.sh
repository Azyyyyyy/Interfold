#!/usr/bin/env bash
# Resolve InformationalVersion for interfold-bootstrap.
# Tag push (bootstrap-v*) → strip `bootstrap-v` prefix (pin identity).
# Otherwise → {last bootstrap-v* SemVer core}+{short commit id}.{run id}.{run attempt};
# no tags → 0.0.0+{id}.{run id}.{run attempt}.
# Run id and attempt give each workflow attempt its own tag. Immutable releases
# lock a tag after publish, so a second develop/main run of the same commit
# publishes a new release.
# Local `dotnet` builds keep the csproj default (0.0.0-dev) — this script is CI-only.
set -euo pipefail

if [[ "${GITHUB_REF_TYPE:-}" == "tag" ]]; then
  v="${GITHUB_REF_NAME:?}"
  if [[ "$v" == bootstrap-v* ]]; then
    echo "${v#bootstrap-v}"
    exit 0
  fi
  echo "Expected bootstrap-v* tag for bootstrapper version resolve, got '$v'" >&2
  exit 1
fi

sha="${GITHUB_SHA:?}"
commit_id="${sha:0:7}"
run_id="${GITHUB_RUN_ID:?}"
run_attempt="${GITHUB_RUN_ATTEMPT:?}"

base="0.0.0"
if latest_tag="$(git describe --tags --abbrev=0 --match 'bootstrap-v*' 2>/dev/null)"; then
  base="${latest_tag#bootstrap-v}"
fi

echo "${base}+${commit_id}.${run_id}.${run_attempt}"
