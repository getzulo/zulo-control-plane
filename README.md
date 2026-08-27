# ZuloOne Control Plane

Fleet management dashboard and provisioning API for multi-tenant ZuloOne deployments.

- **API** (.NET) — provisions, upgrades, monitors, backs up, and deletes tenants
- **Dashboard** (React) — fleet table, new-tenant wizard, per-tenant actions, health roll-up
- **Registry** (Postgres DB) — tenant state, versions, health

See [`docs/ControlPlane.Deployment.md`](../zulo.one/docs/architecture/ControlPlane.Deployment.md) in the main repo for architecture.

## Quick start

Phase 0 is a manual tenant on docker-compose + Traefik. Phase 1 adds the control plane.

## Structure

- `src/` — .NET API + React dashboard + EF Core migrations
- `docs/` — architecture, API spec, operational runbooks
- `build/` — Docker image, docker-compose for the control plane itself
