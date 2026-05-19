# APISIX Gateway (Dev)

LearnStack's only tenant-facing ingress, per
[ADR-0015 (API Gateway: APISIX)](../../docs/decisions/0015-api-gateway-apisix.md).
File-driven standalone mode — `apisix.yaml` is the only source of truth,
hot-reloaded on file change. No etcd, no Admin API, no dashboard.

## Access

| Endpoint | Address | Purpose |
|----------|---------|---------|
| HTTP gateway | `http://localhost:9080` | Public + authenticated routes (per `apisix.yaml`) |
| HTTPS gateway | `https://localhost:9443` | TLS-terminated routes (no cert in dev) |
| Prometheus metrics | `http://localhost:9091` | Per-route + plugin metrics scraped by Prometheus (Phase 11) |

### Why no Admin API and no dashboard?

APISIX 3.2+ moved file-driven standalone behind `deployment.role:
data_plane`, which **does not expose an Admin API**. The companion
`apisix-dashboard` reads routes from etcd; without etcd it cannot show
anything. Both are intentionally out of this compose stack because
ADR-0015 commits to "standalone YAML hot-reload, no etcd dependency."

`apisix.yaml` is therefore the only place a developer changes routes;
diff-review of that file replaces the dashboard for dev workflow. The
etcd-backed deployment is reconsidered when a real admin-UI requirement
appears, behind its own ADR.

## Plugin chain

`infra/apisix/config.yaml` declares the universe of plugins; routes pick
from it per-request order:

```
real-ip → cors → openid-connect → limit-req → request-id → proxy-rewrite → upstream
                                                                            ↓
                                                              prometheus (response)
```

**mTLS is NOT a route-level plugin** in APISIX — it is configured on the
SSL/SNI object (`client.ca` / `client.depth`). The Phase 02c
`/api/internal/*` surface enforces mTLS through an `ssls:` entry plus
route-level `ip-restriction`; see the commented stub at the bottom of
`apisix.yaml`.

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

The `/api/internal/*` route is a **disabled placeholder** documenting the
SSL-object + ip-restriction shape Phase 02c activates.

## Upstream addressing — `host.docker.internal:5080`

The .NET API host runs **outside the container network** during active
dev (`dotnet run` on the developer's workstation). Docker Desktop's
`host.docker.internal` alias resolves to the host loopback; the
`extra_hosts: host.docker.internal:host-gateway` entry on the `apisix`
service (via the `*host-gateway` YAML anchor in `dev.yml`) makes the
alias available on Linux too — **no manual override needed**.

When the .NET host moves inside compose in a later environment profile,
the upstream nodes shift to `learnstack-api:5080` — same `apisix.yaml`,
single one-line change.

## Hot-reload

APISIX watches `apisix.yaml` and re-applies the route table on file change.
A malformed YAML push takes the gateway down, so changes go through CI YAML
validation before they reach the running gateway.

## Dev posture

No credentials live in this directory — file-driven standalone mode has
no Admin API to protect, no dashboard to log into. The only sensitive
surface is the SSL cert pair that Phase 11 introduces when TLS lands.

## What does NOT live here

- etcd-backed deployment shape — reconsidered behind its own ADR if /
  when a UI-driven admin requirement appears.
- `apisix-dashboard` companion — see "Why no Admin API and no dashboard?"
  above.
- Production TLS cert (Let's Encrypt via the same adapter family ADR-0022
  picks for custom domains) — Phase 11.
- Hub-side APISIX route block (`hub.learnstack.dev`) — Phase 02c, lives in
  the separate `learnstack-hub` repo.
- The .NET `AddJwtBearer` middleware (the backend's defence-in-depth
  JWT re-validation) — Phase 02b.
- Hangfire dashboard `/admin/hangfire*` gating — Phase 08a when Hangfire
  itself lands.
