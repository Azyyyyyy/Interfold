#!/usr/bin/env bash
# Resolve the bootstrapper release channel stamped into CI binaries.
# Tag push  → the pin tag (vX.Y.Z).
# develop   → bleeding-edge.
# default branch (main) → stable.
# Anything else (PR heads, feature branches) → empty (omit stamp).
set -euo pipefail

if [[ "${GITHUB_REF_TYPE:-}" == "tag" ]]; then
  echo "${GITHUB_REF_NAME:?}"
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
