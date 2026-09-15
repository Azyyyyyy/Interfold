#!/usr/bin/env bash
# Resolve the bootstrapper release channel stamped into CI binaries.
# Tag push  → the pin tag (bootstrap-vX.Y.Z).
# develop   → bleeding-edge.
# default branch (main) → stable.
# Anything else (PR heads, feature branches) → empty (omit stamp).
set -euo pipefail

if [[ "${GITHUB_REF_TYPE:-}" == "tag" ]]; then
  v="${GITHUB_REF_NAME:?}"
  if [[ "$v" != bootstrap-v* ]]; then
    echo "Expected bootstrap-v* tag for bootstrapper channel resolve, got '$v'" >&2
    exit 1
  fi
  echo "$v"
  exit 0
fi

ref="${GITHUB_REF:-}"
if [[ "$ref" == "refs/heads/develop" ]]; then
  echo "bleeding-edge"
  exit 0
fi

default_branch="${GITHUB_DEFAULT_BRANCH:-main}"
if [[ "$ref" == "refs/heads/${default_branch}" ]]; then
  echo "stable"
  exit 0
fi

# Unstamped: local/PR artefacts are not a published channel identity.
echo ""
