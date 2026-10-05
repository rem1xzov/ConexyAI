# syntax=docker/dockerfile:1

# ConexyAI backend (.NET 9). Multi-stage build:
#   build  -> dotnet/sdk:9.0   (restore + publish)
#   dockercli -> docker:cli    (static Docker CLI copied into the runtime image)
#   final  -> dotnet/aspnet:9.0 (non-root 'app' user)

# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["ConexyAI.csproj", "./"]
RUN dotnet restore "ConexyAI.csproj"
COPY . .
RUN dotnet publish "ConexyAI.csproj" -c Release -o /app/publish /p:UseAppHost=false

# ---- Docker CLI (used by the sandbox runner via DOCKER_HOST / socket proxy) ----
FROM docker:cli AS dockercli

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

# System packages FIRST, before any COPY of app code. Docker invalidates every layer below a changed
# COPY, and `COPY --from=build /app/publish .` changes on every code change — so keeping apt-get below
# it re-downloaded Chromium (~110 MB) on every deploy.
#
# curl is used by the Docker healthcheck (/api/health).
# GITHUB_ACTION_HOST_GIT: git is required for github_action (clone / branch / commit+push run on the
# backend host). The aspnet image ships without it, so the tool used to fail with a vague
# "Could not read the repository configuration" — install it here. ca-certificates comes with git.
# BROWSER_AUTOMATION: Chromium powers take_screenshot and the browser_* tools. Playwright .NET does not
# ship a browser, so we install the system one and point Playwright at it
# (PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH below). fonts-liberation keeps Cyrillic pages from rendering as
# empty boxes. If the browser is missing or fails to launch, the tools are simply hidden — nothing breaks.
# BuildKit cache mounts keep the downloaded .deb archives between builds even if this layer is
# invalidated (e.g. the package list changes); the lists are not deleted because that is the cache.
RUN --mount=type=cache,target=/var/cache/apt,sharing=locked \
    --mount=type=cache,target=/var/lib/apt,sharing=locked \
    apt-get update \
    && apt-get install -y --no-install-recommends curl git ca-certificates chromium fonts-liberation

ENV PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH=/usr/bin/chromium

# The Docker CLI is required by IDockerSandboxRunner. At runtime DOCKER_HOST must point at
# the docker-socket-proxy service (never the raw /var/run/docker.sock).
#
# NOTE: the proxy keeps EXEC=0 on purpose, so sandbox commands run in a throwaway container per
# command (no `docker exec`). Per-session continuity comes from a mounted state directory instead
# of a long-lived container — see SandboxSessionStore / SandboxIdleCleanupService.
COPY --from=dockercli /usr/local/bin/docker /usr/local/bin/docker

COPY --from=build /app/publish .

# The aspnet image ships with a non-root 'app' user (uid 1654). Give it a writable home
# directory before dropping privileges; workspace/RAG directories are mounted as volumes.
RUN mkdir -p /app/home && chown -R app:app /app

USER app
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    HOME=/app/home

EXPOSE 8080
ENTRYPOINT ["dotnet", "ConexyAI.dll"]
