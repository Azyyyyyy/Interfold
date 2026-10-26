# syntax=docker/dockerfile:1.7
#
# Multi-arch image for ghcr.io/azyyyyyy/interfold-api. SDK stage uses the
# target platform so Native AOT compiles with a matching-arch clang/zlib
# toolchain (linux-x64 / linux-arm64 / linux-arm) instead of cross-publishing
# from $BUILDPLATFORM.

FROM --platform=$TARGETPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
ARG VERSION=0.0.0-dev
RUN apt-get update \
    && apt-get install -y --no-install-recommends clang llvm zlib1g-dev libc6-dev \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY . .
RUN --mount=type=cache,id=nuget-${TARGETARCH},target=/root/.nuget/packages,sharing=locked \
    case "$TARGETARCH" in \
        amd64) RID=linux-x64 ;; \
        arm64) RID=linux-arm64 ;; \
        arm)   RID=linux-arm ;; \
        *) echo "Unsupported TARGETARCH: $TARGETARCH" >&2; exit 1 ;; \
    esac && \
    dotnet publish hosts/Interfold.Api.Host/Interfold.Api.Host.csproj \
        --configuration Release \
        --runtime "$RID" \
        --self-contained true \
        /p:PublishAot=true \
        /p:DebugType=none \
        /p:CopyOutputSymbolsToPublishDirectory=false \
        /p:StripSymbols=true \
        /p:Version="$VERSION" \
        /p:InformationalVersion="$VERSION" \
        --output /publish \
    && clang -Os -s -o /publish/http-ready-probe /src/scripts/docker/http-ready-probe.c \
    && find /publish \( -name '*.pdb' -o -name '*.dbg' \) -delete

# Non-extra: no ICU (InvariantGlobalization) and no apt/curl/shell.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled AS final
WORKDIR /app
COPY --from=build /publish/ .
# Host bind-mounts (sqlite, certs) are owned by the bootstrapper UID. Chiseled
# defaults to app (1654); stay root so those mounts stay writable.
USER root

ENTRYPOINT ["./Interfold.Api.Host"]
