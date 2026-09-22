# ZuloOne Control Plane

Fleet management dashboard and provisioning API for multi-tenant ZuloOne deployments.

- **API** (.NET) — provisions, upgrades, monitors, backs up, and deletes tenants
- **Dashboard** (React) — fleet table, new-tenant wizard, per-tenant actions, health roll-up
- **Registry** (Postgres DB) — tenant state, versions, health
- **Models** — install a chosen distribution pack into a running tenant (snapshot first; auto-restore if import/compile fails). Unused `zuloone:2026.0.N` packs can be deleted from Models or Images → Distribution; prune keeps the newest few.
- **Customer portal** (React, `/portal`) — the people who bought a stand, looking after it themselves: reset a user's password, restart, see what their plan includes, let a colleague in. Separate accounts, separate session scheme, separate bundle from the operator dashboard; **off by default**, and turning it on needs a Cloudflare Access bypass. See [`docs/Portal.md`](docs/Portal.md).

See [`docs/ControlPlane.Deployment.md`](../zulo.one/docs/architecture/ControlPlane.Deployment.md) in the main repo for architecture.

## Quick start

Phase 0 is a manual tenant on docker-compose + Traefik. Phase 1 adds the control plane.

## Structure

- `src/` — .NET API + React dashboard + EF Core migrations
- `docs/` — architecture, API spec, operational runbooks
- `build/` — Docker image, docker-compose for the control plane itself
