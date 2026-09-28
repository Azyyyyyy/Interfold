#!/usr/bin/env bash
set -euo pipefail

# Builds the repo-root Dockerfile and tags it interfold-api:dev for local
# bootstrap / Aspire testing. Unprefixed :dev tags stay off the registry.
#
# Usage:
#   scripts/docker/build-api-dev.sh
#   TAG=interfold-api:dev VERSION=0.0.0-dev scripts/docker/build-api-dev.sh

TAG="${TAG:-interfold-api:dev}"
VERSION="${VERSION:-0.0.0-dev}"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"

export DOCKER_BUILDKIT=1

echo "docker build -t ${TAG} (VERSION=${VERSION})"
docker build \
  --tag "${TAG}" \
  --file "${ROOT}/Dockerfile" \
  --build-arg "VERSION=${VERSION}" \
  "${ROOT}"

echo "Built ${TAG}"
