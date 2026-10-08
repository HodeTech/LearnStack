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
| `(public)` | Tenant public site (marketing, catalog, blog). | Anonymous by default; auth optional. | Dynamic SSR, no-store per ADR-0053; see Step 8. |
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
import { createConfiguredPublicClient } from '@/server/configured-public-client';

// The configured server caller supplies this transport; never a tenant-ID option.
export function loadCourses(envelope: string | null) {
  const client = createConfiguredPublicClient(envelope);
  if (!client) throw new Error('Invalid public ingress');
  return client.getCourses(); // Locale comes from the authenticated route.
}
```

> **P02d-4 delivered.** `@learnstack/sdk/server` exports an injected
> `createServerSdk(transport)` with four typed public GET wrappers; no global `sdk`
> object, tenant-ID option or module namespace exists. The example is a loader,
> not a complete route. P02d-5/G35 delivers the configured trusted transport in
> [Phase 02d's decision register](../../../docs/roadmap/phase-02d-walking-skeleton.md#the-decision-register);
> that caller precedes P02d-6 public page consumers. Read the verified envelope
> from request-local `headers()`; never create or expose a provenance stamp in a page.

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
The frontend never calls `IHostToTenantResolver`. P02d-4's SDK is a pure injected
transport contract and sets no hop headers. ADR-0053 replaces ADR-0036's older
exact setter
path with one server-only adapter in apps/web. P02d-5 Step 3's Node middleware
verifies the native envelope, bootstraps the live host and applies enabled-locale
entry before rebuilding downstream request headers. The caller verifies the
envelope again. G35 and G36 are recorded in
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

> **Open in Phase 02d.** No i18n library is installed and no catalogue exists. Whether
> ADR-0027 picks the library in Phase 02d, and where the one UI string catalogue
> lives — the corpus names three paths — are G39 in
> [Phase 02d's decision register](../../../docs/roadmap/phase-02d-walking-skeleton.md#the-decision-register);
> the pass that closes it edits this step and add-i18n-key.

### Step 8: Public-site SSR caching

**Accepted P02d-5 G37 — 2026-10-08.** Follow
[ADR-0053](../../../docs/decisions/0053-trusted-public-server-rendering.md): dynamic
public rendering and no-store API transport; no positive `revalidate`, ISR,
`generateStaticParams`, `unstable_cache` or shared bootstrap/data/route cache.
Request-local reuse is isolated to one incoming request. Server Component HMR caching
is disabled. A new server/document request rechecks eligibility; client history is
not a revocation guarantee. Use the configured server caller; never derive tenancy
from a page header or add hop options to the injected SDK. P6 owns page consumers.

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
  job remains deferred until P02d-7/G44/G45 after P6 pages; judge by reading until
  that harness is implemented. Its remaining details are in the
  [decision register](../../../docs/roadmap/phase-02d-walking-skeleton.md#the-decision-register);
  the pass that closes it rewrites this bullet.

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

- **Mounting under the wrong route group.** Public institution routes are dynamic
  and no-store under ADR-0053; Studio/Portal authentication remains Phase 02b.
- **Hand-rolled `fetch`.** Only the configured server adapter has an API transport
  exemption. Page consumers call its SDK; no second header setter is permitted.
- **Reading `host` inside a page.** The API resolves authority. The native ingress
  authenticates connection provenance; middleware only bootstraps and selects the
  enabled route locale. Plain internal headers are not authority.
- **Client Component by default.** Default to Server. Don't sprinkle
  `"use client"` to avoid thinking about boundaries; that's how INP regresses.
- **Trusting frontend permission check.** Hidden buttons are not security; the
  API enforces. The hook is mirror-only.
- **Skipping `loading.tsx` / `error.tsx`.** Required by the standard for every
  route under a group.
- **Custom domain assumption in markup.** The same code paths must serve the
  tenant default host AND custom domains. Don't hardcode `tenant.example.com`.
