# syntax=docker/dockerfile:1.7
#
# Multi-arch image for ghcr.io/azyyyyyy/interfold-api. SDK stage is pinned to
# $BUILDPLATFORM so `dotnet publish -r linux-$ARCH` cross-publishes natively
# (no QEMU); publish now lives here, not in CI, so each per-arch runtime image
# gets its own RID assets.

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY . .
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages,sharing=locked \
    case "$TARGETARCH" in \
        amd64) RID=linux-x64 ;; \
        arm64) RID=linux-arm64 ;; \
        arm)   RID=linux-arm ;; \
        *) echo "Unsupported TARGETARCH: $TARGETARCH" >&2; exit 1 ;; \
    esac && \
    dotnet publish host/Interfold.Api.Host/Interfold.Api.Host.csproj \
        --configuration Release \
        --runtime "$RID" \
        --self-contained false \
        -p:UseAppHost=false \
        --output /publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /publish/ .

ENTRYPOINT ["dotnet", "Interfold.Api.Host.dll"]
