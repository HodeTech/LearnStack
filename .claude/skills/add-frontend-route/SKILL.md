---
name: add-frontend-route
description: >
  Add a Next.js App Router route under `frontend/apps/web/src/app/(public)/` /
  `(studio)/` / `(portal)/` with the right route group, tenant + organization +
  locale resolution, Server Component default, permission guards (for studio /
  portal), and SDK-based data fetching. USE FOR: a new public page, Studio screen,
  or learner / instructor portal screen. DO NOT USE FOR: thin BFF proxy endpoints
  (those live in `app/api/`), routes for the operator portal (that's the separate
  `operator-portal` app), or hand-rolled `fetch` to the backend (use the typed
  SDK).
---

# Adding a frontend route

## Purpose

Land a new route in `frontend/apps/web` that respects route-group conventions, tenant + org
resolution at the edge, Server-Component-first rendering, and the typed SDK
contract per
[14-frontend-architecture.md](../../../docs/architecture/14-frontend-architecture.md)
+ [07-frontend-architecture.md](../../../docs/standards/07-frontend-architecture.md).

## When to use

- A new public page (marketing / CMS-rendered).
- A new Studio screen for tenant admin / org admin.
- A new portal screen for learner / instructor.
- A new BFF route handler under `api/` (rare; mostly auth callbacks).

## When not to use

- Operator portal pages — they live in `operator-portal`, a separate repo.
- Calling the API directly from a Client Component without the SDK — forbidden by
  ESLint (`no-restricted-globals` on `fetch`,
  `frontend/packages/config/eslint/index.cjs`).
- Routes that bypass tenant resolution — every authenticated route requires a
  resolved tenant.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Path | Yes | URL path (`/courses/[slug]`, `/dashboard/users`, `/lesson/[id]`). |
| Route group | Yes | `(public)` / `(studio)` / `(portal)`. |
| Auth | Yes | Anonymous (public) / authenticated tenant / org-scoped. |
| Permission key | If guarded | `{module}.{resource}.{action}` from the closed set. |
| Server / Client | Yes | Default Server; Client only for interactivity. |

## Workflow

### Step 1: Pick the route group

| Group | Purpose | Auth | Default render |
|-------|---------|------|----------------|
| `(public)` | Tenant public site (marketing, catalog, blog). | Anonymous by default; auth optional. | SSR + ISR-like cache per `(tenantId, organizationId?, locale, slug)` (under an open gate — see Step 8). |
| `(studio)` | Tenant admin Studio. | Tenant-admin or org-admin. Required at the edge. | SSR, no cache (always fresh). |
| `(portal)` | Learner / instructor portal. | Membership in the resolved tenant. | SSR shell + Client Component for interactivity. |

### Step 2: Create the route folder

```
frontend/apps/web/src/app/
  (studio)/
    dashboard/
      users/
        page.tsx
        loading.tsx
        error.tsx
```

Use the `layout.tsx` already present in the route group; do not add a new layout
unless the screen genuinely needs one.

### Step 3: Server Component shell

```tsx
// page.tsx (Server Component by default)
import { sdk } from "@learnstack/sdk/server";
import { UsersTable } from "./_components/users-table";

export default async function UsersPage({ searchParams }: { searchParams: { q?: string } }) {
  const users = await sdk.identity.listUsers({ query: searchParams.q });
  return <UsersTable initialUsers={users.items} />;
}
```

Rules:

- Default to Server Component. `"use client"` only when the screen needs hooks,
  browser APIs, or third-party client-only libs.
- The SDK (`@learnstack/sdk/server`) is the **only** sanctioned way to call the
  API. Hand-rolled `fetch('/v1/...')` is blocked by lint.
- Pass typed primitives across the RSC → Client boundary — no class instances, no
  closures.

### Step 4: Tenant + organization context (automatic)

The API resolves tenant and organization from the host
([ADR-0036 § Effective host and the trusted hop](../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md#effective-host-and-the-trusted-hop)).
The frontend never calls `IHostToTenantResolver`, and ADR-0036 makes
`frontend/packages/sdk/src/server.ts` the only frontend place that sets the hop
headers. At HEAD that file is a typed stub, and `src/middleware.ts` is a scaffold
that copies the raw host into `x-tenant-id` and sets `x-locale`; it sets no
`x-organization-id`. Their replacement is recorded as G35 and G36 in
[Phase 02d's decision register](../../../docs/roadmap/phase-02d-walking-skeleton.md#the-decision-register).
Don't read `host` directly inside a page.

### Step 5: Authentication + permission gating

For `(studio)` and `(portal)` routes:

- The middleware redirects unauthenticated requests to the Keycloak login.
- Permission check happens at the page level via the `auth()` helper:

```tsx
import { auth } from "@learnstack/auth/server"; // illustrative: no such package exists yet; Phase 02b's session work owns the real helper
import { redirect } from "next/navigation";

export default async function UsersPage() {
  const session = await auth();
  if (!session) redirect("/login");
  if (!session.permissions.includes("identity.user.read")) {
    redirect("/dashboard");   // or render a 403 UI
  }
  // ...
}
```

`auth()` reads from the HttpOnly cookie session set by the BFF; never touch tokens
in a Client Component. The frontend permission check is **mirror-only** — the API
is authoritative.

### Step 6: Feature gating (entitlement-aware UI)

For features gated by plan-projected `FeatureKey`:

```tsx
import { useFeatureFlag } from "@learnstack/sdk/hooks";

export function CustomDomainTab() {
  const enabled = useFeatureFlag(FeatureKeys.CustomDomain);
  if (!enabled) return null;   // hide the tab entirely
  return <CustomDomainSettings />;
}
```

Hide, don't disable. The hook reads the entitlement projection. See
[add-feature-gated-ui](../add-feature-gated-ui/SKILL.md).

### Step 7: Localisation

```tsx
import { useTranslations } from "next-intl";   // or react-intl per the i18n ADR

export default function CoursesPage() {
  const t = useTranslations("courses");
  return <h1>{t("title")}</h1>;
}
```

Translation keys live under `frontend/apps/web/src/i18n/<locale>/courses.json`.
See [add-i18n-key](../add-i18n-key/SKILL.md).

### Step 8: Public-site SSR caching

This step is under an open gate. How `(public)` routes render, and which Next.js
caches they may use, is G37 in
[Phase 02d's decision register](../../../docs/roadmap/phase-02d-walking-skeleton.md#the-decision-register).
The step is rewritten when that gate closes; until then add no `revalidate`,
`generateStaticParams` or `unstable_cache` to a `(public)` route. The cache key is
**not** tenant-bearing automatically. A statically rendered route, which is what
`revalidate` produces when the page reads no request data, is cached by path, and a
public URL carries no tenant
([Frontend Architecture Standards](../../../docs/standards/07-frontend-architecture.md)).
Every cache key carries the tenant, the organization where applicable, and the locale
([Security Standards § Multi-Tenant + Organization Isolation Review Checklist](../../../docs/standards/11-security.md#multi-tenant--organization-isolation-review-checklist)).

### Step 9: Loading + error boundaries

Every route ships its own:

- `loading.tsx` — skeleton shell, not a blank page. No "loading…" spinners for
  expected-fast resources (<250 ms).
- `error.tsx` — graceful boundary; 404 page renders the tenant's brand if a
  tenant was resolved.

### Step 10: Tests

- Component tests (`frontend/apps/web/src/app/(studio)/dashboard/users/page.test.tsx`)
  with Testing Library, per
  [Testing Standards § Frontend Test Types](../../../docs/standards/06-testing.md#frontend-test-types).
  Automated `axe-core` runs through Playwright, owned by
  [Phase 06](../../../docs/roadmap/phase-06-renderer-admin-studio.md) per
  [Testing Standards § End-to-End Tests](../../../docs/standards/06-testing.md#end-to-end-tests);
  the manual keyboard and contrast checks
  [Accessibility Standards § Tooling](../../../docs/standards/16-accessibility.md#tooling)
  and [§ Testing](../../../docs/standards/16-accessibility.md#testing) require are
  recorded in the PR description. The phase that ships a route names its test set in
  its decision register.
- Lighthouse budget check on representative public routes — CI's `lighthouse budget`
  job is a deferred placeholder until Phase 02d activates it; until then judge by
  reading.

## Validation

- `pnpm build` / `next build` succeeds.
- `pnpm lint` is green; specifically the `no-restricted-globals` rule on `fetch`,
  which bans a direct `fetch` call outside the SDK.
- The route renders under the resolved tenant/org/locale and rejects mismatched
  authn.
- A `(studio)` route returns 403 when the actor lacks the required permission;
  the API was already authoritative — confirm.
- The public-route budgets in
  [Performance Standards](../../../docs/standards/15-performance.md) hold on
  representative routes — judged by reading until CI's `lighthouse budget` job is
  active.

## Common pitfalls

- **Mounting under the wrong route group.** `(public)` SSR + ISR is wrong for a
  Studio screen — caching across users is a leak.
- **Hand-rolled `fetch`.** The ESLint rule rejects it; use the SDK.
- **Reading `host` inside a page.** The middleware is the only legal resolver.
- **Client Component by default.** Default to Server. Don't sprinkle
  `"use client"` to avoid thinking about boundaries; that's how INP regresses.
- **Trusting frontend permission check.** Hidden buttons are not security; the
  API enforces. The hook is mirror-only.
- **Skipping `loading.tsx` / `error.tsx`.** Required by the standard for every
  route under a group.
- **Custom domain assumption in markup.** The same code paths must serve the
  tenant default host AND custom domains. Don't hardcode `tenant.example.com`.
