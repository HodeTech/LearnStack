# `@learnstack/web`

The single tenant-facing Next.js application per
[ADR-0009](../../../docs/decisions/0009-frontend-single-app-first.md).

The operator portal (`learnstack-hub-web`) lives in the separate `learnstack-hub`
repository — not under this `frontend/` directory.

## Route Groups

| Route group | Purpose | Phase that fills it in |
|-------------|---------|------------------------|
| `(public)/` | Tenant-facing public site | 04 / 06 |
| `(studio)/` | Admin + content studio | 04 / 06 |
| `(portal)/` | Learner + instructor portal | 07 |
| `api/` | Thin BFF route handlers (`/healthz` shipped) | 02a+ |

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
# from /frontend
pnpm install
pnpm --filter @learnstack/web dev
```

The app boots on http://localhost:3000. The Next.js BFF healthcheck is at
`/api/healthz`. The .NET API serves its own `/healthz` on http://localhost:5080
during local dev.
