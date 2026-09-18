# syntax=docker/dockerfile:1

# ConexyAI sandbox image: .NET SDK 9.0 + Node.js (node/npm) so agent build/run tasks
# (dotnet/npm/node) work inside the isolated bash sandbox.
#
# Build & tag (run from the repository root):
#   docker build -t conexy-sandbox:latest -f docker/sandbox/Dockerfile docker/sandbox
# Push to a private registry (or build on the deploy host) so the backend can use it:
#   docker tag conexy-sandbox:latest <registry>/conexy-sandbox:latest
#   docker push <registry>/conexy-sandbox:latest

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

# Stage 2: final image = .NET SDK + Node.js from stage 1.
FROM mcr.microsoft.com/dotnet/sdk:9.0
ARG NODE_VERSION=20.18.1

ENV PATH="/opt/nodejs/bin:${PATH}" \
    NODE_VERSION="${NODE_VERSION}" \
    # The sandbox is run with --user 1000:1000, --read-only and a tmpfs /tmp. Pointing
    # HOME and every package-manager cache at /tmp keeps dotnet/npm first-run writes on
    # the writable tmpfs instead of the read-only root filesystem.
    HOME=/tmp \
    DOTNET_CLI_HOME=/tmp \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    NPM_CONFIG_CACHE=/tmp/.npm \
    XDG_CACHE_HOME=/tmp/.cache

COPY --from=node /opt/nodejs /opt/nodejs

CMD ["sh"]
