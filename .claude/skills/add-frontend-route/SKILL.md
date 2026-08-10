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
  ESLint (`no-restricted-imports`).
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
| `(public)` | Tenant public site (marketing, catalog, blog). | Anonymous by default; auth optional. | SSR + ISR-like cache per `(tenantId, organizationId?, locale, slug)`. |
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

The Next.js middleware (`src/middleware.ts`) resolves the host via
`IHostToTenantResolver` and sets:

- `x-tenant-id` header
- `x-organization-id` header (when the host maps to a specific organization)
- `x-locale` header

The SDK reads these from the request context automatically; you don't pass them.
Don't read `host` directly inside a page; the resolution is the middleware's
contract.

### Step 5: Authentication + permission gating

For `(studio)` and `(portal)` routes:

- The middleware redirects unauthenticated requests to the Keycloak login.
- Permission check happens at the page level via the `auth()` helper:

```tsx
import { auth } from "@learnstack/auth/server";
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

For `(public)` routes that render CMS content:

```tsx
export const revalidate = 60;   // ISR-like; tenant publishes invalidate via webhook
```

Cache key includes tenant + org + locale + slug automatically because the SDK
threads them through.

### Step 9: Loading + error boundaries

Every route ships its own:

- `loading.tsx` — skeleton shell, not a blank page. No "loading…" spinners for
  expected-fast resources (<250 ms).
- `error.tsx` — graceful boundary; 404 page renders the tenant's brand if a
  tenant was resolved.

### Step 10: Tests

- Component test (`frontend/apps/web/src/app/(studio)/dashboard/users/page.test.tsx`) with
  `axe-core` for accessibility.
- Lighthouse budget check on representative public routes (CI).

## Validation

- `pnpm build` / `next build` succeeds.
- `pnpm lint` is green; specifically the `no-restricted-imports` rule that bans
  raw `fetch('/v1/...')` from any component.
- The route renders under the resolved tenant/org/locale and rejects mismatched
  authn.
- A `(studio)` route returns 403 when the actor lacks the required permission;
  the API was already authoritative — confirm.
- Lighthouse budgets (LCP < 2.5s, INP < 200ms, CLS < 0.05) green on
  representative routes.

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
