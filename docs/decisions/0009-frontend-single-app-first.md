# ADR 0009: Frontend — Single Next.js App First

## Status

Accepted

## Decision

LearnStack starts with a **single Next.js application** under `frontend/`, with route segments separating concerns:

- `app/(public)/` — tenant-facing public site
- `app/(studio)/` — admin and content studio
- `app/(portal)/` — learner and instructor portal
- `app/api/` — thin BFF proxies only

Shared packages (`packages/ui`, `packages/sdk`, `packages/config`) are extracted only when duplication becomes real.

The multi-app split (`apps/web`, `apps/studio`, `apps/portal`) is **deferred** until measured triggers fire.

## Context

The earlier draft assumed a three-app frontend monorepo from day one. With a small team and a shared design system, three apps multiply coordination cost: three builds, three deploys, three sets of typed clients, three sets of UI primitives drifting apart.

A single Next.js app with route segments offers:

- One install, one build, one deploy in the early phase.
- One source of truth for design tokens, layout, tenant context.
- Lower drag while the product shape is still forming.

Multi-app deployments are valuable when:

- Build time becomes a per-team blocker.
- Deploy cadence differs significantly between surfaces (e.g. studio ships daily, portal weekly).
- Independent teams own each surface and want isolated CI / preview environments.
- Public-site SEO or performance budget requires a sharply smaller bundle than studio/portal can afford.

None of these triggers are present in the early phases.

## Consequences

- `frontend/` holds one `package.json` + one Next.js app.
- Route segments are the unit of separation; their internal coupling is the same as inside any app.
- Authentication, tenant resolution, and design tokens are shared modules under `lib/`.
- The path to multi-app split is **mechanical**: extract `packages/ui`, then `packages/sdk`, then move `(studio)` to `apps/studio`, then `(portal)` to `apps/portal`.
- A migration ADR will record the split when it happens.

## Triggers That Reverse This Decision

Any one of these is sufficient cause to revisit:

- Public-site bundle is forced above its performance budget by studio/portal code paths.
- Single-app build time exceeds 10 minutes on CI.
- Separate teams own studio and portal with different release cadences.
- A surface needs a fundamentally different deployment target (e.g. portal on a paid runtime, public on edge static).

## References

- [Frontend Architecture](../architecture/14-frontend-architecture.md)
- [Frontend Architecture Standards](../standards/07-frontend-architecture.md)
- Superseded ADR file: [_redirects/0006-frontend-single-app-first.md](_redirects/0006-frontend-single-app-first.md) (kept for old links).
