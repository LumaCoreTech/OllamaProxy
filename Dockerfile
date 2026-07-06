# syntax=docker/dockerfile:1

# ---------------------------------------------------------------------------
# OllamaProxy container image
#
# Multi-stage build:
#   1. "build"   restores and publishes a framework-dependent app.
#   2. "runtime" copies the published output onto the slim ASP.NET runtime.
#
# Secrets (API keys) are NEVER baked into the image. Supply them at run time
# via environment variables, e.g.
#   OllamaProxy__Backends__default__ApiKey=sk-...
# ---------------------------------------------------------------------------

# --- Build stage -----------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so the layer is cached when only source files change.
COPY src/Directory.Build.props \
     src/Directory.Build.targets \
     src/Directory.Packages.props \
     src/
COPY src/OllamaProxy/OllamaProxy.csproj src/OllamaProxy/
RUN dotnet restore src/OllamaProxy/OllamaProxy.csproj

# Copy the remaining source and publish a Release build.
COPY src/ src/
RUN dotnet publish src/OllamaProxy/OllamaProxy.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

# --- Runtime stage ---------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# The data-plane listener (the Ollama-compatible proxy, inner Kestrel instance). appsettings.json pins it
# to http://localhost:11434 via OllamaProxy:ListenUrl so a desktop or service install is never exposed to
# the LAN by default. Inside a container that localhost bind would make the proxy unreachable through
# Docker's port mapping. Override the same option via an environment variable (env vars win over the JSON
# layer) to rebind to all interfaces while keeping the conventional Ollama port, so existing clients
# connect unchanged.
ENV OllamaProxy__ListenUrl=http://0.0.0.0:11434 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_TieredPGO=1

# The control-plane listener (the admin UI, outer chassis Kestrel instance). The chassis binds its own
# address via Admin:Url — independently of the inner proxy's listener above, so the two Kestrel instances
# in the one process never share a port. Rebind it to all interfaces so the admin UI is reachable once its
# port is published; the shipped default stays http://localhost:11435 for desktop/service installs.
ENV Admin__Url=http://0.0.0.0:11435

# Declare both ports. EXPOSE is metadata only: the operator decides what to actually publish (docker run -p)
# and owns access control at the container boundary, because the admin UI has no built-in authentication and
# relies on that boundary once it is bound to all interfaces.
EXPOSE 11434 11435

# Run as the non-root user provided by the base image.
USER $APP_UID

COPY --from=build /app/publish ./

ENTRYPOINT ["dotnet", "OllamaProxy.dll"]
