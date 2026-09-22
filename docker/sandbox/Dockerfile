# syntax=docker/dockerfile:1

# ConexyAI sandbox image: .NET SDK 9.0 + Node.js (node/npm) + Python 3 (python/pip) so agent
# build/run/install tasks (dotnet/npm/pip) work inside the isolated bash sandbox.
#
# Build & tag (run from the repository root):
#   docker build -t conexy-sandbox:latest -f docker/sandbox/Dockerfile docker/sandbox
# Push to a private registry (or build on the deploy host) so the backend can use it:
#   docker tag conexy-sandbox:latest <registry>/conexy-sandbox:latest
#   docker push <registry>/conexy-sandbox:latest
#
# Image size / build time: the base is the .NET SDK (already large); Node adds ~100MB and Python
# adds ~150MB on top. That is the price of a sandbox where the agent can actually build and install
# things; keep it in mind before adding more toolchains here.

# Stage 1: fetch a portable Node.js runtime.
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS node
ARG NODE_VERSION=20.18.1
RUN arch="$(uname -m)" \
    && case "$arch" in \
         x86_64) node_arch="x64" ;; \
         aarch64) node_arch="arm64" ;; \
         *) echo "Unsupported architecture: $arch" >&2; exit 1 ;; \
       esac \
    && apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl xz-utils \
    && rm -rf /var/lib/apt/lists/* \
    && curl -fsSL "https://nodejs.org/dist/v${NODE_VERSION}/node-v${NODE_VERSION}-linux-${node_arch}.tar.xz" -o /tmp/node.tar.xz \
    && mkdir -p /opt/nodejs \
    && tar -xJf /tmp/node.tar.xz -C /opt/nodejs --strip-components=1 \
    && rm /tmp/node.tar.xz \
    && /opt/nodejs/bin/node --version \
    && PATH="/opt/nodejs/bin:$PATH" /opt/nodejs/bin/npm --version

# Stage 2: final image = .NET SDK + Node.js from stage 1 + Python 3 from the distribution.
FROM mcr.microsoft.com/dotnet/sdk:9.0
ARG NODE_VERSION=20.18.1

# PYTHON_SANDBOX: добавлено 2026-09-23 — pip-пакеты агента.
# Deliberately the distribution's Python (Debian bookworm ships 3.11) instead of a source build or
# a copied python:3.x tree: it installs in seconds, keeps the image ~1GB smaller than a compiled
# interpreter, and — the actual deciding factor — it keeps receiving security updates through apt.
# 3.11 is in security-only maintenance upstream, which is fine for a sandbox; if a specific newer
# version is ever required, add a `python:3.13-slim-bookworm` stage and COPY /usr/local from it
# (same Debian release, so the shared libraries line up).
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        python3 \
        python3-pip \
        python3-venv \
    && rm -rf /var/lib/apt/lists/* \
    && python3 --version \
    && pip3 --version

ENV PATH="/tmp/.local/bin:/tmp/.npm-global/bin:/opt/nodejs/bin:${PATH}" \
    NODE_VERSION="${NODE_VERSION}" \
    # The sandbox is run with --read-only and a tmpfs /tmp. Pointing HOME and every
    # package-manager cache at /tmp keeps dotnet/npm/pip first-run writes on the writable tmpfs
    # instead of the read-only root filesystem. The backend overrides all of these to /state when
    # it mounts the per-session state directory (see DockerSandboxRunner), which is what makes the
    # caches survive across commands; these values are the fallback for a stateless container.
    HOME=/tmp \
    DOTNET_CLI_HOME=/tmp \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    NPM_CONFIG_CACHE=/tmp/.npm \
    # `npm install -g` otherwise writes into the read-only node prefix.
    NPM_CONFIG_PREFIX=/tmp/.npm-global \
    XDG_CACHE_HOME=/tmp/.cache \
    PYTHONUSERBASE=/tmp/.local \
    PIP_CACHE_DIR=/tmp/.cache/pip \
    # PEP 668: Debian marks the system interpreter as externally managed, so a bare `pip install`
    # is refused. Combined with PIP_USER this installs into ~/.local (inside the writable state
    # directory) instead of the read-only system site-packages.
    PIP_USER=1 \
    PIP_BREAK_SYSTEM_PACKAGES=1 \
    # Python skips writing __pycache__ into the read-only rootfs anyway; this just avoids noise.
    PYTHONDONTWRITEBYTECODE=1

COPY --from=node /opt/nodejs /opt/nodejs

CMD ["sh"]
