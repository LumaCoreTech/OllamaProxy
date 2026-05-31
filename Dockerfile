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
COPY Directory.Build.props ./
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

# Listen on the conventional Ollama port so existing clients connect unchanged.
ENV ASPNETCORE_HTTP_PORTS=11434 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_TieredPGO=1
EXPOSE 11434

# Run as the non-root user provided by the base image.
USER $APP_UID

COPY --from=build /app/publish ./

ENTRYPOINT ["dotnet", "OllamaProxy.dll"]
