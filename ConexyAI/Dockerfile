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

# The Docker CLI is required by IDockerSandboxRunner. At runtime DOCKER_HOST must point at
# the docker-socket-proxy service (never the raw /var/run/docker.sock).
#
# NOTE: the proxy keeps EXEC=0 on purpose, so sandbox commands run in a throwaway container per
# command (no `docker exec`). Per-session continuity comes from a mounted state directory instead
# of a long-lived container — see SandboxSessionStore / SandboxIdleCleanupService.
COPY --from=dockercli /usr/local/bin/docker /usr/local/bin/docker

COPY --from=build /app/publish .

# curl is used by the Docker healthcheck (/api/health).
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

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
