# ADR 0015: API Gateway with APISIX

## Status

Accepted

## Date

2026-05-18

## Decision

LearnStack uses **Apache APISIX** as its API gateway in front of the .NET API and Hub API.

- **Deployment mode**: standalone (YAML hot-reload, no etcd dependency).
- **Plugin chain (request order)**: `real-ip` → `cors` → `openid-connect` (JWT) →
  `limit-req` (rate limit) → `request-id` (correlation id) → `proxy-rewrite` → backend.
- **Defense-in-depth**: Gateway validates JWT signature + expiry; backend re-validates JWT
  with tenant context resolution. A failed JWT at the gateway never reaches the backend.
- **Route table**: declarative YAML, hot-reloaded on file change. Two priority bands:
  public routes (priority 1: health, localization, OpenAPI), authenticated routes
  (priority 100: `/api/v{version}/**`).

## Context

LearnStack runs as a modular monolith with two backend HTTP surfaces:

- `LearnStack.Host` — main API, modules expose endpoints under `/api/v{version}/{module}/...`.
- `learnstack-hub` — Hub API (ADR-0019), separate codebase, operator-facing,
  `https://hub.learnstack.dev/api/v1/...`.

Front-end surfaces:

- `apps/web` — public site + studio + portal (one Next.js app, ADR-0009).
- `apps/hub-web` — Hub operator portal.

Direct internet exposure of the backend Kestrel listener is undesirable for the same reasons
every production system rejects it: no TLS termination at a managed layer, no per-route rate
limiting, no centralised auth pre-filter, no per-route observability, no edge-level
correlation-id injection.

We need a gateway that:

1. Terminates TLS for the public API surface.
2. Validates JWTs at the edge (defence in depth; backend re-validates).
3. Provides per-route rate limiting (public vs authenticated vs write routes).
4. Handles CORS preflight without leaking it to the backend.
5. Injects `X-Correlation-Id` (or propagates it from the client).
6. Supports two backend upstreams (LearnStack API + Hub API) with route-based selection.
7. Is self-hostable, OSS, and production-grade.
8. Has a low operational footprint for dev environments (no etcd, no complex bootstrap).

Nexora's experience with APISIX (see
`Nexora/docs/decisions/0003-deployment-strategy.md` and
`Nexora/docs/operations/HELM_INSTALLATION.md`) demonstrated:

- Standalone mode (YAML hot-reload, no etcd) is perfect for dev and small production
  deployments; etcd-backed mode is reconsidered when dynamic config from a UI becomes
  necessary.
- The `openid-connect` plugin with Keycloak realm discovery is a one-config setup.
- The `request-id` plugin injects correlation ID end-to-end with zero per-route config.
- CORS preflight separation (higher-priority OPTIONS-only route) is the standard pattern to
  prevent the auth plugin from rejecting preflight requests.

## Decision drivers

1. **OSS + self-hosted** — fits LearnStack's preference (ADR-0002).
2. **No etcd dependency in standalone mode** — minimal operational surface for dev and
   smaller deployments. etcd-backed deployment available when admin UI is needed.
3. **Mature plugin ecosystem** — JWT, rate limit, CORS, prometheus metrics, gzip, request-id
   are all built-in and stable.
4. **Lua-extensible** — custom plugins possible if needed, written in Lua (low risk for a
   small team).
5. **High performance** — APISIX is built on OpenResty + nginx; benchmarks show
   sub-millisecond latency overhead.
6. **Proven in the Nexora pattern**, modular monolith multi-tenant SaaS; same architecture
   style.
7. **Active CNCF Graduated project** — long-term maintenance signal.

## Considered options

### Option A — Apache APISIX standalone (chosen)

- Standalone deployment, YAML route config hot-reloaded on file change.
- Plugins: `real-ip`, `cors`, `openid-connect`, `limit-req`, `request-id`, `proxy-rewrite`,
  `response-rewrite`, `prometheus`, `gzip`.
- Per-route Keycloak JWT validation.

**Pros:**
- Mature, OSS, self-hostable.
- Zero etcd dependency in standalone mode.
- Plugin chain covers all required concerns.
- Already proven in Nexora.

**Cons:**
- Lua under the hood; harder to debug than a .NET gateway like YARP for engineers unfamiliar
  with OpenResty.
- Hot-reload requires a file-watcher pattern; CI must validate the YAML before deploy.

### Option B — Traefik

- Cloud-native gateway, written in Go, widely deployed in Kubernetes.

**Pros:**
- Excellent Kubernetes integration via CRDs.
- Simpler config for basic routing.
- Built-in Let's Encrypt support.

**Cons:**
- JWT validation requires third-party middleware (`traefik-plugin-jwt`) not as battle-tested
  as APISIX `openid-connect`.
- Less granular rate-limiting compared to APISIX `limit-req` / `limit-count` separation.
- No first-class CORS-preflight separation pattern (must hand-roll).

### Option C — NGINX (vanilla or NGINX Plus)

- The classic reverse proxy.

**Pros:**
- Universal, battle-tested, every operator knows it.

**Cons:**
- JWT validation requires NGINX Plus (commercial) or `lua-resty-openidc` (manual setup).
- Rate limiting is module-config-heavy, not declarative.
- Custom-plugin story is weaker than APISIX.

### Option D — YARP (Yet Another Reverse Proxy, .NET-native)

- .NET-native gateway from Microsoft.

**Pros:**
- Same runtime as the backend; same observability, same logging, same deployment.
- C# config; no Lua learning curve.

**Cons:**
- YARP runs as part of the .NET process or as a separate .NET app; both options give up
  some defense-in-depth (a Kestrel failure could affect the gateway).
- Plugin ecosystem is small compared to APISIX.
- No precedent in our sister project (Nexora uses APISIX).

### Option E — No gateway (backend directly exposed)

**Cons:**
- TLS termination, rate limiting, CORS, correlation ID injection, JWT pre-filter — all of
  these would need to be added to the backend or to a managed cloud LB. Increases per-route
  config burden, dilutes defense-in-depth, and makes Self-Hosted on-prem deployments much
  more complex.

## Decision outcome

Adopt **Option A**: Apache APISIX in standalone mode.

### Plugin chain (production)

```yaml
# Request enters APISIX → plugin chain → upstream
plugins:
  - real-ip                # Determine X-Forwarded-For client IP
  - cors                   # CORS preflight + allow-origin enforcement
  - openid-connect         # JWT validation against Keycloak (bearer_only)
  - limit-req              # Rate limiting (leaky bucket)
  - limit-count            # Per-key request count windows
  - request-id             # X-Correlation-Id injection
  - proxy-rewrite          # Header normalisation
  - response-rewrite       # Backend response rewrite (rarely used)
  - prometheus             # Metrics exporter
  - gzip                   # Response compression
```

### Route table — priority bands

```yaml
# Public routes — priority 1 (matched first)
- id: 1
  uri: /health
  plugins: [limit-req]                # 10 req/s, public
- id: 2
  uri: /api/v*/localization/*
  plugins: [cors, limit-req]          # 50 req/s, public + CORS
- id: 3
  uri: /openapi/*
  plugins: []                         # Dev only

# Authenticated routes — priority 100 (matched after public)
- id: 100
  uri: /api/v*/**
  methods: [GET, POST, PUT, PATCH, DELETE]
  plugins:
    - openid-connect                  # Required
    - cors
    - limit-req                       # 100 req/s authenticated
    - request-id

# CORS preflight separation — priority 99 (one tick before authenticated)
- id: 99
  uri: /api/v*/**
  methods: [OPTIONS]
  plugins: [cors]                     # OPTIONS bypasses openid-connect
```

### Hub gateway routes

Hub has its own subdomain (`hub.learnstack.dev`); APISIX serves a separate host block:

```yaml
- id: 200
  host: hub.learnstack.dev
  uri: /api/v*/**
  plugins:
    - openid-connect                  # Hub realm, not main realm
    - cors                            # Hub CORS allow-list (operator portal origin only)
    - limit-req                       # Stricter limit on Hub API
    - request-id
```

### mTLS for Hub-to-LearnStack internal API

Hub calls into LearnStack `POST /api/internal/tenants/{id}/entitlements` (and similar
inverse-direction calls). APISIX terminates external TLS but **does not** front the
`/api/internal/*` path; that path is bound to an internal-only listener with mTLS enforced
between Hub and LearnStack. See ADR-0019.

### Defense-in-depth — why JWT is validated twice

1. **At APISIX**: signature + expiry + audience + issuer. Failure → 401 returned by gateway
   without backend roundtrip.
2. **At LearnStack backend** (`AddJwtBearer` middleware): same checks again + tenant claim
   resolution + permission policy evaluation.

The double-check is intentional. If APISIX is misconfigured or bypassed (internal traffic,
ops-debug access), the backend still rejects unauthenticated requests. The backend never
trusts the gateway alone.

## Architecture tests / CI gates

- `apisix.yaml` validated against APISIX YAML schema in CI.
- Hangfire dashboard route (`/admin/hangfire*`) gated by both APISIX (BasicAuth or JWT)
  and backend (`HangfireAuthFilter.IsInRole("platform-admin")`). Architecture test fails if
  the backend filter is bypassed in any environment.
- A separate CI step deploys APISIX in a Docker compose harness with the production YAML
  and runs a smoke suite (anonymous routes pass, authenticated routes return 401 without
  token, JWT-validated routes pass).

## Consequences

### Positive

- TLS termination, rate limiting, JWT pre-filter, CORS preflight, correlation ID injection
  all centralised at one layer.
- Backend Kestrel never sees unauthenticated traffic (except `/health` and
  `/api/v*/localization/*`).
- Same gateway works for dev (single-node Docker), production (HA pair behind L4), and
  self-hosted (single instance per deployment).
- Plugin chain is declarative YAML; reviewable diff like any other infra artifact.

### Negative

- One more runtime to operate (APISIX itself + its YAML config).
- Lua plugins (if we ever write custom ones) require an OpenResty mindset.
- File-watch hot-reload: a malformed YAML push can take the gateway down; CI must validate.

### Neutral

- Hub uses a separate APISIX host block (or, in larger deployments, a separate APISIX
  instance for Hub). The decision is operational, not architectural.

## Implementation notes

- Phase 01 — Repository tooling: APISIX in `docker-compose.yml`; `infra/apisix/` with
  `config.yaml` (deployment mode) and `apisix.yaml` (routes).
- Phase 02a — Platform kernel: APISIX route table covers all known endpoints; CORS
  allow-list for `localhost:3000` (web) and `localhost:3001` (studio); the
  mTLS-guarded route set for `/api/internal/*` is reserved as a separate APISIX route
  group (endpoints arrive in Phase 02c).
- Phase 03 — Identity: APISIX `openid-connect` plugin wired to Keycloak `learnstack`
  realm discovery endpoint; Hub uses `learnstack-hub` realm.
- Phase 11 — Production hardening:
  - TLS termination at APISIX (Let's Encrypt via ACME plugin or cert-manager in K8s).
  - `KC_HOSTNAME_BACKCHANNEL_DYNAMIC=true` (Keycloak split-issuer handling for Docker
    network vs browser).
  - Production CORS allow-list (no `localhost`).
  - Tenant-aware rate limiting (consumer-based limit key when Hub provides per-tenant
    consumer keys).

The full route table, plugin chain, mTLS internal API setup, and operational checklist live
in [30-api-gateway.md](../architecture/30-api-gateway.md).

## References

- ADR-0019 — LearnStack Hub: Hub's gateway-fronted topology.
- ADR-0020 — Triple Deployment Model.
- [30-api-gateway.md](../architecture/30-api-gateway.md) — architecture deep dive.
- [11-security.md](../standards/11-security.md) — defense-in-depth security model.
- [20-infrastructure-stack.md](../standards/20-infrastructure-stack.md) — gateway operating
  rules.
