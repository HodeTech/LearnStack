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

Land a new route in `frontend/apps/web` that respects route-group conventions,
API-owned tenant/organization resolution, Server Component rendering and the typed
SDK contract per
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
| Path | Yes | Public URL `/{locale}/courses/{slug}`; future authenticated paths follow their owning phase. |
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

The shipped public tree is `(public)/[locale]/courses/`, with course detail at
`[slug]/page.tsx` and lesson detail at `[slug]/lessons/[lessonSlug]/page.tsx`.
`courses` and `lessons` are fixed section segments. The only localized status
namespace is `[locale]/status/not-found`. A new public segment also needs an
explicit update to the closed `contentPath` / `publicEntry` admission policy and
its tests; adding a Next folder alone leaves it refused. Phase 06 owns broader
localized section names.

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
// app/(public)/[locale]/courses/page.tsx
import { notFound } from 'next/navigation';
import { PublicCatalog } from '@/components/public/catalog';
import { PublicState } from '@/components/public/state';
import { requirePublicResource } from '@/server/public-resource';
import { getPublicUi } from '@/server/public-ui';

export default async function CoursesPage() {
  const resource = await requirePublicResource();
  const ui = await getPublicUi();
  if (resource.kind === 'failure') {
    return <PublicState state={resource.state}
      recoveryPath={resource.request.route.path}
      locale={ui.locale} direction={ui.direction} t={ui.t} />;
  }
  if (resource.kind !== 'catalog') notFound();
  return <PublicCatalog resource={resource} ui={ui} />;
}
```

> **P02d-4 delivered.** `@learnstack/sdk/server` exports an injected
> `createServerSdk(transport)` with four typed public GET wrappers; no global `sdk`
> object, tenant-ID option or module namespace exists. P02d-5/G35 delivers the
> configured trusted transport in
> [Phase 02d's decision register](../../../docs/roadmap/phase-02d-walking-skeleton.md#the-decision-register);
> that caller precedes P02d-6 public page consumers. Pages use the shared resource
> loader; they do not read envelope headers or create another bootstrap/client.
> Keep loader helpers outside `page.tsx`; Next page exports are restricted to
> supported route exports such as the default component and `generateMetadata`.

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
Don't read `host` directly inside a page. The
[public entry matrix](../../../docs/roadmap/phase-02d-walking-skeleton.md#public-entry-matrix)
is authoritative: bootstrap the live host before locale/redirect decisions;
enabled-locale membership precedes `courses` shorthand and exact `/studio` or
`/portal` scaffold handling. Disabled/malformed/unknown prefixes fail closed;
no locale header, cookie or query supplies authority. The API remains the only
tenant/organization owner. Continue the request-local validated `traceparent`
through bootstrap and the configured caller; Phase 11 owns participation/sampling.

**ADR-0054 native/URL controls delivered — 2026-10-09.**
[The replacement contract](../../../docs/decisions/0054-bounded-public-renderer-admission.md)
requires native GET/HEAD admission on every HTTP callback path, including
middleware-exempt health/assets/scaffolds, with masked no-store refusal of other
methods and bodyless HEAD. Production upgrades close; development retains only
validated GET HMR. The [remediation record](../../../docs/roadmap/phase-02d-walking-skeleton.md#remediation-step-2--native-ingress-and-url-boundary)
owns validation and both independent review rounds. Do not add
Server Actions/write routes or WebSocket consumers without an explicit owning
Phase 02b/Phase 06
admission decision. Preserve signed raw-target route/locale authority when
accounting for pinned Next's URL projection. Redirects retain inert query values,
duplicates and order with equivalent percent encoding; only verified live host,
accepted HTTPS port and local path select the destination.

### Step 5: Authentication + permission gating

Authentication below is the Phase 02b target, not current scaffold behavior.
Today's exact `/studio` and `/portal` roots pass only after public host bootstrap;
no sign-in/session helper is implemented. Phase 02b must explicitly admit its
login/callback namespace before adding authenticated routes.

For those future `(studio)` and `(portal)` routes:

- Unauthenticated requests redirect through the accepted BFF/Keycloak sign-in flow.
- A page-level permission check uses the future `auth()` helper:

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

The hook/package below is a future feature-UI sketch, not a shipped SDK export.
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

**P02d-6 G39 foundation delivered — Step 1, 2026-10-10.**
[ADR-0027](../../../docs/decisions/0027-frontend-i18n.md) selects exact `next-intl`
4.14.9, now installed with complete English/Turkish catalogues. The async Server
Component pattern is:

```tsx
import { getPublicUi } from "@/server/public-ui";

export default async function CoursesPage() {
  const { t } = await getPublicUi();
  return <h1>{t("catalog.title")}</h1>;
}
```

Messages live in `frontend/apps/web/src/i18n/messages/<locale>/<namespace>.json`;
P02d-6 supplies complete `en/public.json` and `tr/public.json`. General UI keys
are dotted feature identifiers, distinct from backend `lockey_*` wire keys. The
web app owns closed page-outcome mappings; the SDK supplies no translations.
See [add-i18n-key](../add-i18n-key/SKILL.md).

`src/i18n/request.ts` uses the same server-only, request-cached verified admission
loader as document/layout/page consumers. It re-verifies the ingress envelope,
reads the canonical locale from the signed target and checks live enabled-locale
membership. The loader neither imports next-intl nor reads messages. No i18n
routing middleware, preference cookie, locale header or callsite override supplies
authority. Only whole-catalogue fallback is allowed for unauthored UI languages;
missing used keys in supported catalogues fail validation. Preserve exact content
locale, document language and actual resolved-label language.

The guarded frontend suite supplies ICU, argument and callsite checks. No
`lint:i18n` command or screenshot/axe tooling exists. The
[Step 1 delivery record](../../../docs/roadmap/phase-02d-walking-skeleton.md#p02d-6-step-1-localization-and-document-foundation)
owns foundation validation; the
[packet closeout](../../../docs/roadmap/phase-02d-walking-skeleton.md#p02d-6-packet-closeout-2026-10-10)
records the delivered product proof and scoped manual accessibility checks.

### Step 8: Public-site SSR caching

**Accepted P02d-5 G37 — 2026-10-08.** Follow
[ADR-0053](../../../docs/decisions/0053-trusted-public-server-rendering.md): dynamic
public rendering and no-store API transport; no positive `revalidate`, ISR,
`generateStaticParams`, `unstable_cache` or shared bootstrap/data/route cache.
Request-local reuse is isolated to one incoming request. Server Component HMR caching
is disabled. A new server/document request rechecks eligibility; client history is
not a revocation guarantee. Use the configured server caller; never derive tenancy
from a page header or add hop options to the injected SDK.

**Accepted P02d-6 G40 — 2026-10-09; foundation delivered in Step 1.** Use ordinary
same-host relative anchors for public navigation and pagination, without automatic
prefetch or reliance on retained client Router Cache. Request-local metadata,
layout and page share verified admission and resource loaders; each page honors
their result before emitting a shell. No loading boundary may flush before
redirect admission.

Parse owned pagination values only from the verified raw signed target, never
observed Next `searchParams`. Catalog uses `cursor`/`limit`; outline uses
`lessonCursor`/`lessonLimit`, default 20 and API bounds. Refuse duplicate, empty,
malformed or oversized owned values; never decode opaque cursors or fabricate a
previous cursor. Paginated metadata is noindex with a cursor-free canonical.
Canonical/alternate URLs use the verified live host and eligible API slugs.

### Step 9: Loading + error boundaries

Each route retains `loading.tsx` and a graceful `error.tsx`; P02d-6's accepted
status composition is distinct from the framework's thrown `notFound()` behavior:

- A missing/hidden content resource returns local HTTP 307 to the same host's
  `/{locale}/status/not-found`. The browser URL changes; the original response is
  not a direct 404.
- Middleware admits that fixed status namespace through the same live host and
  locale checks, then supplies HTTP 404. Its ordinary server-rendered document has
  localized language/direction, safe theme/chrome, noindex metadata and a catalog
  recovery link. It never queries Education or echoes the original slug/query.
- Unknown-host/provenance refusals remain direct masked responses. Fresh status
  bootstrap failure retains neutral 404/429/503; fallback UI cannot invent tenant
  admission. HEAD is bodyless throughout.
- Known content-call failures are controlled translated HTTP 200 noindex states:
  invalid cursor with reset link, retry-later for 429, unavailable for transport,
  invalid responses or unavailable API. They do not claim HTTP 400/429/503.
  Closed outcome mapping never exposes backend keys, titles, field errors or
  parameters as lookup identifiers or visible copy.
- Unexpected framework errors keep pre-stream 500 / post-stream 200 behavior;
  `error.tsx` cannot set arbitrary status.

ADR-0055's implemented single bootstrap costs two API calls per completed product
document, one per fixed status/scaffold document and three per followed missing
document. Metadata/layout/page/UI share the admitted snapshot without another
bootstrap or cross-request cache. HEAD/RSC/prefetch and Flight fallback require
separately proven counts. The
[Step 3 record](../../../docs/roadmap/phase-02d-walking-skeleton.md#adr-0055-step-3--production-admission-proof-and-closeout)
records passing production replacement proofs, completed reviews and verified fixes.

### Step 10: Tests

- Synchronous view/mapping tests use Vitest/Testing Library; async public pages use
  the real production HTML/RSC fixture, per
  [Testing Standards § Frontend Test Types](../../../docs/standards/06-testing.md#frontend-test-types).
  Automated `axe-core` runs through Playwright, owned by
  [Phase 06](../../../docs/roadmap/phase-06-renderer-admin-studio.md) per
  [Testing Standards § End-to-End Tests](../../../docs/standards/06-testing.md#end-to-end-tests);
  the manual keyboard and contrast checks
  [Accessibility Standards § Tooling](../../../docs/standards/16-accessibility.md#tooling)
  and [§ Testing](../../../docs/standards/16-accessibility.md#testing) require are
  recorded in the PR description. P02d-6 also requires actual manual screen-reader,
  keyboard, focus, 320 CSS px reflow/zoom, long-string and contrast evidence on both
  hosts/locales. If unavailable, evidence stays pending and completion is not
  claimed. HTTP/RSC assertions are not browser or assistive-technology proof.
  P02d-7 owns browser/demo and Lighthouse; Phase 11 owns web-vitals telemetry.
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
