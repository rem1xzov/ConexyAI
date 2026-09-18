# Deploying ConexyAI with Docker

This document covers the production Docker deployment, the hardened sandbox, and the
security model for launching sandbox containers from inside the backend container.

## 1. The sandbox image

The `bash` tool runs agent commands inside a hardened Docker container. The image must
contain `dotnet`, `npm` and `node` (so the agent can build/run .NET and Node projects).

Build and tag it from the repository root:

```sh
docker build -t conexy-sandbox:latest -f docker/sandbox/Dockerfile docker/sandbox
```

The image is a multi-stage build: `mcr.microsoft.com/dotnet/sdk:9.0` as the base plus a
portable Node.js runtime (NodeSource-style tarball, default Node 20) layered on top.

Because the sandbox can only reference an image that is already on the Docker **host**
(the socket proxy does not allow builds or pulls), you must either:

- push `conexy-sandbox:latest` to a private registry and pre-pull it on the deploy host, or
- build it directly on the deploy host with the command above.

The backend points at it via `Sandbox__Image` (default `conexy-sandbox:latest`).

### Sandbox hardening flags

`DockerSandboxRunner` runs every sandbox container with:

- `--network none` — no network access.
- `--read-only` — read-only root filesystem.
- `--tmpfs /tmp:rw,size=<TmpfsSize>` (default `64m`) — the only writable scratch space.
- `--cap-drop ALL` — no Linux capabilities.
- `--security-opt no-new-privileges` — no privilege escalation via setuid/setgid/sudo.
- `--memory`, `--cpus`, `--pids-limit` — resource caps (currently shared across tiers).
- `--user 1000:1000` — non-root user.
- `--pull never` — the host daemon never pulls images on the sandbox's behalf.
- a single `-v "{workspace}:/workspace:rw"` bind mount — no other host paths are mounted.

`--memory` (512m) and `--pids-limit` (64) are shared for now. A future change should make
them per-tier (Free/Pro/ProMax); the option fields already live in `SandboxOptions` and are
overridable per environment.

### File-size limit (`--ulimit fsize`) caveat

A per-file size limit (`RLIMIT_FSIZE`) is the natural guard against `dd`/huge-log disk
fills, **but it is incompatible with the .NET runtime**: with any finite `RLIMIT_FSIZE`,
`dotnet` aborts with `File size limit exceeded` (SIGXFSZ) on startup. Because the sandbox
exists to run `dotnet` build/run tasks, `Sandbox__FileSizeLimitBytes` defaults to `0`
(disabled / unlimited).

To actually bound disk usage on the host, use a **filesystem quota on the workspace
directory** instead (e.g. an XFS project quota or ZFS dataset quota on
`/srv/conexyai/workspaces`). `/tmp` inside the sandbox is already capped by
`--tmpfs /tmp:rw,size=<TmpfsSize>` (default 64 MiB), and the root filesystem is read-only,
so the only unbounded writable surface is the `/workspace` bind mount.

## 2. Docker socket proxy (critical)

The backend itself runs inside a container on the server. If it mounted `/var/run/docker.sock`
directly, a compromise of the backend would be equivalent to **root on the host**. Instead:

- The backend talks to Docker only through `tecnativa/docker-socket-proxy`, reachable at
  `tcp://docker-socket-proxy:2375` (set via `DOCKER_HOST`).
- The proxy mounts the socket **read-only** and exposes a deny-by-default allowlist of
  Docker Engine API endpoints.

> This was verified with a live run through the proxy (create/start/wait/logs/kill/rm and
> the `docker version` check all succeeded). Two important findings came out of that test:
>
> 1. **Env vars are section-level, not per-verb.** The image (HAProxy 3.x) uses
>    `POST=1` (allow non-GET methods), `CONTAINERS=1` (allow `/containers/*`), `VERSION=1`,
>    `PING=1`, plus section toggles like `EXEC`/`IMAGES`/`BUILD`. There are **no**
>    `CONTAINERS_CREATE`/`CONTAINERS_WAIT`/... vars.
> 2. **Foreground `docker run` attach is broken through HAProxy.** The hijacked `attach`
>    stream does not deliver stdout/stderr back to the CLI, so `DockerSandboxRunner` now uses
>    `docker run -d` + `docker wait` + `docker logs` instead of foreground `docker run`.

The allowlist the runner actually needs:

| Endpoint | Purpose |
| --- | --- |
| `GET /version`, `HEAD/GET /_ping` | `docker version` availability check |
| `POST /containers/create` | create sandbox container |
| `POST /containers/{id}/start` | start it |
| `POST /containers/{id}/wait` | wait for exit |
| `GET /containers/{id}/logs` | read output |
| `GET /containers/{id}/json` | inspect |
| `POST /containers/{id}/kill` | kill on timeout/cancel |
| `DELETE /containers/{id}` | remove (`docker rm -f`) |

Explicitly **denied** (and never enable):

- `EXEC` — `POST /exec/{id}/start` (the actual escape hatch into other containers / host).
- `IMAGES` (enumeration `GET /images/json`, pull `POST /images/create`), `BUILD`.
- `VOLUMES`, `NETWORKS`, `SYSTEM`, `INFO`, `SWARM`, `NODES`, `PLUGINS`, `SECRETS`, `CONFIG`.

> Caveat: `CONTAINERS=1` also allows `POST /containers/{id}/exec` (creating an exec *instance*),
> but that is harmless on its own — running it requires `POST /exec/{id}/start`, which `EXEC=0`
> blocks. With `--pull never`, the runner issues **no** `GET /images/{name}/json`, so `IMAGES=0`
> is enough and no image metadata is exposed.
>
> Future improvement: replace the `docker` CLI subprocess with the `Docker.DotNet` SDK pointed
> at the socket proxy and call exactly `create/start/wait/logs/delete`. That removes the CLI's
> implicit `attach`/`inspect` calls and lets the allowlist shrink to the absolute minimum.

## 3. Workspace path must be a host path

The backend bind-mounts the session workspace into the sandbox with:

```
-v "{workspaceHostPath}:/workspace:rw"
```

`workspaceHostPath` is resolved by the backend **inside its container**, but the `-v` source
is interpreted by the Docker **host** daemon (the request is proxied). For that path to point
at the same directory, the backend container must mount the workspace directory at the *same
path* as on the host.

`docker-compose.prod.yml` does exactly that with a same-path bind mount:

```yaml
- ${WORKSPACE_HOST_PATH:-/srv/conexyai/workspaces}:${WORKSPACE_HOST_PATH:-/srv/conexyai/workspaces}
```

and sets `Workspace__RootPath` to the same value. Create the directories and give them to the
backend's non-root user (uid 1654) before starting:

```sh
sudo mkdir -p /srv/conexyai/workspaces /srv/conexyai/documents
sudo chown -R 1654:1654 /srv/conexyai
```

Do **not** point `Workspace__RootPath` at a path that exists only inside the backend container
(e.g. `/workspaces` when the host directory is `./data/workspaces`), otherwise the sandbox
would mount a non-existent host path.

## 4. Deploying

```sh
cp .env.example .env
# edit .env with real secrets

# build the sandbox image on the host (or pull it from your registry)
docker build -t conexy-sandbox:latest -f docker/sandbox/Dockerfile docker/sandbox

docker compose -f docker-compose.prod.yml --env-file .env up -d --build
```

Services: `postgres` (persistent volume + healthcheck), `backend` (.NET, non-root, healthcheck),
`frontend` (nginx serving the Vite build, proxying `/api` and `/hubs`), and
`docker-socket-proxy`. All use `restart: unless-stopped`. Two networks keep PostgreSQL and the
socket proxy unreachable from the public-facing nginx.

### Secrets must come from the environment

`appsettings.json` must stay free of real secrets. All sensitive values (DeepSeek API key,
JWT signing key, GitHub PAT, WebSearch/SpeechKit keys, DB password) are injected via
`.env` → `docker-compose.prod.yml` → environment variables (the `__`-delimited keys map to
`appsettings` sections). For local development use environment variables or `dotnet user-secrets`;
never paste a real key back into `appsettings.json`. The JWT `SigningKey` placeholder in
`appsettings.json` is intentionally non-secret and is overridden in production by
`JWT_SIGNING_KEY`.

Also note: outside `Development` the `/api/auth/dev-token` endpoint returns 404 and the
60 req/min rate limit is active. Production needs a real token issuer — the symmetric
`dev-token` path is development-only.

## 5. RAG data isolation

Documents are owned by a `UserId`. All reads (list, full-text search, chunk reads) are scoped
to the authenticated user; there is no cross-user document access. A migration
(`AddDocumentUserId`) adds the column + index. Existing rows get `00000000-0000-0000-0000-000000000000`
and become inaccessible (owned by no real user) — safe by default.
