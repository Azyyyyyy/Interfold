# Builds the repo-root Dockerfile and tags it interfold-api:dev for local
# bootstrap / Aspire testing. Unprefixed :dev tags stay off the registry.
param(
    [string]$Tag = "interfold-api:dev",
    [string]$Version = "0.0.0-dev"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$env:DOCKER_BUILDKIT = "1"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$dockerfile = Join-Path $repoRoot "Dockerfile"
if (-not (Test-Path $dockerfile)) {
    throw "Dockerfile not found at $dockerfile"
}

Write-Host "docker build -t $Tag (VERSION=$Version)"
& docker build `
    --tag $Tag `
    --file $dockerfile `
    --build-arg "VERSION=$Version" `
    $repoRoot
if ($LASTEXITCODE -ne 0) {
    throw "docker build failed with exit $LASTEXITCODE"
}

Write-Host "Built $Tag"
