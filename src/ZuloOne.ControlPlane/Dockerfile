# ZuloOne control plane — the fleet API and its operator dashboard in one
# container, on a single origin (:8080).
#
# Runs on zo-cp-1, in the core subnet, deliberately NOT on the host that serves
# tenants: a compromise of the panel is a compromise of every tenant's database,
# so it does not share a machine with customer workloads
# (docs/architecture/ControlPlane.Deployment.md §12).
#
# Build context = repo root:
#   docker build -f src/ZuloOne.ControlPlane/Dockerfile --build-arg VERSION=2026.9.0 \
#     -t zuloone/controlplane:2026.9.0 .
#
# VERSION must be Major.Minor.Patch with no leading zeros and no prerelease
# suffix, for the same reason as the tenant image: AssemblyVersion silently drops
# both, so the image tag and what /health reports would disagree forever.

# ---------------------------------------------------------------- SPA ------
# node:22, not 20: web/package.json pins Vite ^8.2.2, which requires Node
# 20.19+/22.12+. A node:20-alpine tag can resolve below that floor and the build
# fails on a version check that names neither file.
FROM node:22-alpine AS spa
WORKDIR /spa
# Manifest first so `npm ci` is cached until dependencies actually change.
COPY web/package.json web/package-lock.json ./
RUN npm config set fetch-retries 5 \
    && npm config set fetch-retry-maxtimeout 120000 \
    && npm ci --no-audit --no-fund
COPY web/ ./
# Empty base makes the client call its OWN origin, which is what lets one
# container serve the API and the dashboard with no CORS involved.
ENV VITE_API_BASE=""
# A build that emits nothing is not an error to vite, and the resulting image
# serves the SPA fallback for every route — assert the entry point exists.
RUN npm run build && test -s dist/index.html

# ------------------------------------------------------------- BACKEND -----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Directory.Build.props travels WITH the csproj: it sits at the repo root and is
# imported during evaluation, so leaving it out gives a container build that
# quietly differs from a local one.
COPY Directory.Build.props ./
COPY src/ZuloOne.ControlPlane/ZuloOne.ControlPlane.csproj src/ZuloOne.ControlPlane/
RUN dotnet restore src/ZuloOne.ControlPlane/ZuloOne.ControlPlane.csproj

COPY src/ ./src/

# Declared AFTER restore on purpose: an ARG invalidates every layer below it, so
# a version bump reuses the restore instead of redoing it.
ARG VERSION=0.0.0
RUN dotnet publish src/ZuloOne.ControlPlane/ZuloOne.ControlPlane.csproj -c Release \
    -o /app/publish --no-restore -p:Version=$VERSION

# ------------------------------------------------------------- RUNTIME -----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# libgssapi-krb5-2: Npgsql loads it for Kerberos negotiation even when unused.
# curl: the HEALTHCHECK probe below.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 curl \
    && rm -rf /var/lib/apt/lists/*

# pg_dump / pg_restore, which are how a tenant is snapshotted and restored. The
# panel already has a network route to Postgres and read rights on every tenant
# database, so a logical dump needs nothing else — no shell on a database node, no
# agent, no SSH key.
#
# Version 17 SPECIFICALLY, from PGDG rather than Ubuntu. Noble ships
# postgresql-client-16, and pg_dump refuses outright to read a server newer than
# itself:
#
#   pg_dump: error: server version: 17.x; pg_dump version: 16.x
#   pg_dump: error: aborting because of server version mismatch
#
# So the distribution package would fail on every single tenant, at the moment
# somebody needed a backup. Pin the major deliberately: it must be >= the server,
# and a silent jump ahead of the cluster is worth noticing rather than absorbing.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates gnupg \
    && install -d /usr/share/postgresql-common/pgdg \
    && curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc \
         -o /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc \
    && echo "deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.asc] \
https://apt.postgresql.org/pub/repos/apt noble-pgdg main" > /etc/apt/sources.list.d/pgdg.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends postgresql-client-17 \
    && apt-get purge -y gnupg && apt-get autoremove -y \
    && rm -rf /var/lib/apt/lists/* \
    # A glob that matched nothing is not an error to apt, and a missing pg_dump
    # would only surface when a restore was already needed.
    && pg_dump --version | grep -q ' 17\.' \
    && pg_restore --version | grep -q ' 17\.'

# Where snapshots land. A named volume is mounted here by the deployment so that
# dumps outlive a container recreate — an upgrade's rollback is the snapshot taken
# just before it, and losing it during the upgrade would be the worst possible
# moment.
RUN mkdir -p /var/zuloone/snapshots && chown $APP_UID:$APP_UID /var/zuloone/snapshots
ENV Snapshots__Path=/var/zuloone/snapshots

COPY --from=build /app/publish ./
COPY --from=spa /spa/dist ./wwwroot

# Two listeners. The public one carries the API and the dashboard; 5099 is the
# ONLY place password authentication exists.
#
# Both bind to +, not 127.0.0.1. A container-loopback bind would be unreachable
# even through an SSH tunnel, because that loopback is the CONTAINER's, not the
# host's. The isolation comes from how the port is published instead — the
# deployment must map it to the host's loopback only:
#
#   -p 8080:8080 -p 127.0.0.1:5099:5099
#
# Then break-glass is reached with `ssh -L 5099:127.0.0.1:5099 zo-cp-1` and from
# nowhere else. Publish 5099 on 0.0.0.0 and the password endpoint is on the LAN.
ENV ASPNETCORE_URLS="http://+:8080;http://+:5099"
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 8080

# Aimed at the loopback listener, deliberately. The public one may be HTTPS with
# the Cloudflare Origin CA certificate — which nothing but Cloudflare trusts — so
# a probe there would need -k, and a probe with -k proves less than one that does
# not. 5099 is always plain HTTP and always present.
HEALTHCHECK --interval=15s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -fsS http://127.0.0.1:5099/health || exit 1

USER $APP_UID

# The aspnet base image defines no default command. Without this the container
# starts, finds nothing to run, and exits 0 with an empty log — which reads as
# "it ran and finished" rather than "it was never told what to run".
ENTRYPOINT ["dotnet", "ZuloOne.ControlPlane.dll"]
