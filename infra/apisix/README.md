# APISIX Gateway (Dev)

LearnStack's only tenant-facing ingress, per
[ADR-0015 (API Gateway: APISIX)](../../docs/decisions/0015-api-gateway-apisix.md).
Standalone YAML-reload mode — no etcd, no admin-UI-driven config drift; the
files in this directory are the source of truth.

## Access

| Endpoint | Address | Purpose |
|----------|---------|---------|
| HTTP gateway | `http://localhost:9080` | Public + authenticated routes (per `apisix.yaml`) |
| HTTPS gateway | `https://localhost:9443` | TLS-terminated routes (no cert in dev) |
| Admin API | `http://localhost:9180` | Route + plugin admin (dev only; `admin` key `learnstack-dev-admin-key`) |
| Prometheus metrics | `http://localhost:9091` | Per-route + plugin metrics scraped by Prometheus (Phase 11) |
| Dashboard | `http://localhost:9000` | Browser UI (dev only; user `admin` / pass `learnstack-dev-dashboard-pass`) |

## Plugin chain

`infra/apisix/config.yaml` declares the universe of plugins; routes pick
from it per-request order:

```
real-ip → cors → openid-connect → limit-req → request-id → proxy-rewrite → upstream
                                                                            ↓
                                                              prometheus (response)
```

`mtls` is **reserved** — Phase 02c (LearnStack Hub) activates it against the
LearnStack-internal CA for the `/api/internal/*` surface.

## Routes shipped today (`apisix.yaml`)

| Priority | URI | Methods | Plugin chain | Upstream |
|----------|-----|---------|--------------|----------|
| 1 | `/healthz` | GET | `cors`, `limit-req` (10 r/s), `prometheus` | `host.docker.internal:5080` |
| 99 | `/api/v*/**` | OPTIONS | `cors` only (preflight bypass) | `host.docker.internal:5080` |
| 100 | `/api/v*/**` | GET/POST/PUT/PATCH/DELETE | `cors`, `limit-req` (100 r/s), `request-id`, `prometheus` | `host.docker.internal:5080` |

`openid-connect` is **commented out** in route 100; Phase 03 wires the
plugin against the `learnstack` Keycloak realm discovery endpoint. The
backend re-validates the JWT (defence in depth — gateway compromise must
not bypass auth).

The `/api/internal/*` route is a **disabled placeholder**; Phase 02c
activates it with `mtls` + `ip-restriction` against the Hub-issued client
cert.

## Upstream addressing — why `host.docker.internal:5080`

The .NET API host runs **outside the container network** during active
dev (`dotnet run` on the developer's workstation). Docker Desktop's
`host.docker.internal` alias resolves to the host loopback; Linux developers
without Docker Desktop need to add:

```yaml
apisix:
  extra_hosts:
    - "host.docker.internal:host-gateway"
```

…in their local override (the DX packet (07) ships `make dev` with the
cross-platform case handled).

When the .NET host moves inside compose in a later environment profile,
the upstream nodes shift to `learnstack-api:5080` — same `apisix.yaml`,
single one-line change.

## Hot-reload

APISIX watches `apisix.yaml` and re-applies the route table on file change.
A malformed YAML push takes the gateway down, so changes go through CI YAML
validation before they reach the running gateway.

## Dev credentials are dev credentials

Every credential in this directory (admin key, dashboard JWT secret,
dashboard user) is dev-only. Production loads them from Vault via the
component metadata pattern + restricts the admin API to an
internal-only listener.

## What does NOT live here

- Production TLS cert (Let's Encrypt via the same adapter family ADR-0022
  picks for custom domains) — Phase 11.
- Hub-side APISIX route block (`hub.learnstack.dev`) — Phase 02c, lives in
  the separate `learnstack-hub` repo.
- The .NET `AddJwtBearer` middleware (the backend's defence-in-depth
  JWT re-validation) — Phase 02b.
- Hangfire dashboard `/admin/hangfire*` gating — Phase 08a when Hangfire
  itself lands.
