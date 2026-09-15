#!/usr/bin/env bash
# Detect whether any changed path matches a grep -E pattern between two SHAs.
# Usage: detect-path-changes.sh <base_sha> <head_sha> <grep_E_pattern>
# Prints "true" or "false" to stdout. Exit 0 always (callers decide policy).
set -euo pipefail

base="${1:?base sha required}"
head="${2:?head sha required}"
pattern="${3:?pattern required}"

if [ "$base" = "0000000000000000000000000000000000000000" ]; then
  echo "true"
  exit 0
fi

if git diff --name-only "$base" "$head" | grep -E "$pattern" >/dev/null; then
  echo "true"
else
  echo "false"
fi
