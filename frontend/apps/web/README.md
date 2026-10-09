# `@learnstack/web`

The single tenant-facing Next.js application per
[ADR-0009](../../../docs/decisions/0009-frontend-single-app-first.md).

The operator portal (`learnstack-hub-web`) lives in the separate `learnstack-hub`
repository — not under this `frontend/` directory.

## Route Groups

Route groups (`(public)`, `(studio)`, `(portal)`) organize files without
affecting URLs — each surface owns a distinct URL prefix so the three roots
don't collide at `/`:

| Route group        | URL prefix | Purpose                                          | Phase that fills it in |
| ------------------ | ---------- | ------------------------------------------------ | ---------------------- |
| `(public)/`        | `/`        | Tenant-facing public site                        | 02d / 04 / 06          |
| `(studio)/studio/` | `/studio`  | Admin + content studio                           | 04 / 06                |
| `(portal)/portal/` | `/portal`  | Learner + instructor portal                      | 07                     |
| `api/`             | `/api/*`   | Thin BFF route handlers (`/api/healthz` shipped) | 02a+                   |

P02d-4 delivers the API reads; P02d-5 delivers live bootstrap and membership-first
entry under [ADR-0053](../../../docs/decisions/0053-trusted-public-server-rendering.md).
Exact scaffold roots continue only after bootstrap; enabled locale membership takes
precedence. P02d-6 implements localized catalog/course/lesson pages, ordered
plain-text presentation, safe four-color themes and G40 page states. Its
[product closeout](../../../docs/roadmap/phase-02d-walking-skeleton.md#p02d-6-step-4-product-proof-and-accessibility-closeout)
records completed verification, reviews and passing manual accessibility.
P6 awaits maintainer PR review and merge.

There is **no `extensions/` folder for vertical-provided components** — per
[ADR-0018](../../../docs/decisions/0018-tenant-driven-customization-model.md),
tenant-specific renderers are composite renderer keys resolved by
[`src/lib/customization/`](src/lib/customization/) against a closed set.

## Customization Runtime (ADR-0018)

[`src/lib/customization/`](src/lib/customization/) holds the runtime resolver for
the closed primitive + composite renderer sets. A tenant's `TenantPageBlock`
row carries a `renderer_key` string — `resolveRendererKey()` is the only
sanctioned path from that string to a real component. Domain-flavoured keys
(`english.vocabulary-list`, `yoga.asana-card`) are forbidden; tenants compose
primitives or ask LearnStack to add a new composite.

## Local Run

```bash
# from the repository root, after the documented hosts/certificate preparation
make public-env
make public-api    # keep running in its own terminal
make public-web    # run in a second terminal
```

Follow the root [HTTPS Quickstart](../../../README.md#quickstart) for dependencies,
manual host/CA trust steps and leaf certificate paths. The native ingress binds
IPv4 loopback port 3000 and serves configured hosts over HTTPS. `/api/healthz` is
the exact web health path. The paired API origin comes from private local
configuration, never from the visitor's URL. Direct stock `next dev/start` does
not establish trusted provenance and is unsupported.
