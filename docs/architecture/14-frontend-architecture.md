# Frontend Architecture

LearnStack ships **two independent Next.js applications**:

- **`apps/web`** in *this* repository — the tenant-facing surface. One Next.js app
  (App Router) with `(public)`, `(studio)`, and `(portal)` route segments separating
  marketing/CMS rendering, admin Studio, and learner/instructor portals. The multi-app
  split is deferred until concrete need
  ([ADR 0009 — Frontend Single App First](../decisions/0009-frontend-single-app-first.md)).
- **`operator-portal`** in the **separate `learnstack-hub` repository** — the
  **operator portal** at `hub.learnstack.dev`. Operators authenticate against the
  `learnstack-hub` Keycloak realm (ADR-0004 Amendment 1); different realm, different
  user pool, different domain. The two apps **do not share code at runtime**. If a
  shared design-system package is later extracted (`packages/ui`), it is a build-time-only
  dependency. The operator portal scope lives in
  [24-learnstack-hub.md §6](24-learnstack-hub.md) and is not duplicated here.

This document covers `apps/web`: app shape, tenant resolution at the edge, theming with
optional per-organization override, rendering strategies, data fetching, the tenant-driven
block resolver, entitlement-aware UI, custom-domain handling, and the path to extracting
independent apps when warranted.

## App Shape

```text
frontend/
  apps/
    web/                                  # the only tenant-facing Next.js app
      src/
        app/
          (public)/
            [tenant-by-host]/             # virtual segment, resolved in middleware
            page.tsx
            courses/
            blog/
          (studio)/
            login/
            dashboard/
            content/
            pages/
            courses/
            media/
          (portal)/
            my-courses/
            lesson/[id]/
            sessions/
          api/                            # only thin BFF proxies, see "Data Fetching"
        components/
          blocks/                         # built-in primitive page blocks
          ui/                             # design-system primitives
        lib/
          api/
          auth/
          tenant/
          i18n/
        middleware.ts                     # tenant + organization + locale resolution
        extensions/                       # client-side block resolver, see Page Builder
  packages/
    ui/                                   # extracted only once duplication is real
    sdk/                                  # generated typed API client
    config/                               # eslint, tsconfig, tailwind shared bits
```

The operator portal (`operator-portal`) is a **separate Next.js application in the
separate `learnstack-hub` repository**; nothing about it lives under this `frontend/`
tree.

Two boundaries inside one app:

- **Route segments** (`(public)`, `(studio)`, `(portal)`) keep code physically separated.
- **Layouts** in each segment apply different shells (public marketing layout vs admin chrome vs portal chrome).

Splitting into separate apps is governed by [ADR 0009 — Frontend Single App First](../decisions/0009-frontend-single-app-first.md); the split triggers (independent deploy cadence, build-time becomes a bottleneck, separate teams) are listed there.

## Tenant + Organization Resolution at the Edge

Next.js middleware resolves the tenant (and optionally the organization) before any
route handler runs — for **rendering**. It is not the backend's source of truth and
never was one: the API resolves the host itself, independently and authoritatively,
and the edge's answer is a render-time convenience
([ADR-0036](../decisions/0036-tenant-resolution-trusted-inputs.md);
[Standards 07 § Tenant Context](../standards/07-frontend-architecture.md) owns the
rules). What the server-side SDK sends the API is the **visitor's host** over the
trusted hop — `X-LearnStack-Host` with `X-LearnStack-Hop-Secret` — and the tenant
header travels only as an assertion the API compares against its own answer.

```ts
// src/middleware.ts
export async function middleware(req: NextRequest) {
  const host = normaliseHost(req.headers.get('host') ?? '');
  const resolved = await resolveHost(host);   // calls /v1/tenants/resolve-host
  if (!resolved) return new NextResponse(null, { status: 404 });

  const locale = resolveLocaleFromPathOrTenant(req.nextUrl, resolved.tenant);

  // Headers MUST be written to the request (not the response) — only request
  // headers reach downstream Server Components / route handlers via
  // `next/headers`. Writing to `res.headers` only surfaces them to the browser.
  const requestHeaders = new Headers(req.headers);

  // Carried for RENDERING — branding, locale, which nav to draw. The API does
  // not read these to decide anything; it resolves the host itself and treats
  // x-tenant-id as an assertion to compare against that answer.
  requestHeaders.set('x-tenant-id', resolved.tenant.id);
  if (resolved.organizationId) requestHeaders.set('x-organization-id', resolved.organizationId);
  requestHeaders.set('x-locale', locale);

  // The visitor's host, carried INWARD so the server-side SDK can state it to
  // the API. The host — not the tenant — is the input the API resolves from.
  // The hop SECRET is deliberately not here: it is server configuration, and a
  // secret written into a forwarded request header travels further than the one
  // hop it authenticates.
  requestHeaders.set('x-learnstack-host', host);

  return NextResponse.next({ request: { headers: requestHeaders } });
}

export const config = {
  matcher: ['/((?!_next/static|_next/image|favicon.ico).*)'],
};
```

The SDK is what states the hop, on the way **out**. It reads the host the
middleware carried inward and pairs it with the secret from server configuration
— which is why `createServerSdk` is a server-only entry point and why the secret
never appears in the middleware above:

```ts
// packages/sdk/src/server.ts — the shape, once the first operation exists.
const response = await fetch(new URL(path, options.apiBaseUrl), {
  headers: {
    // The trusted hop: both halves, or the API ignores the host header
    // entirely and resolves from its own Host (ADR-0036).
    'X-LearnStack-Host': options.host,
    'X-LearnStack-Hop-Secret': process.env.LEARNSTACK_HOP_SECRET!,

    // An assertion, not a selector. The API compares it against the host it
    // resolved for itself; a mismatch is a 404.
    'X-Tenant-Id': options.tenantId,
    'Accept-Language': options.locale,
  },
});
```

`resolveHost` is backed by a short-TTL edge cache (~60 s) that calls the LearnStack API,
which in turn reads the `platform_host_to_tenant` projection (populated by the Hub —
[27-custom-domain-tls.md](27-custom-domain-tls.md)). Custom-domain activations and
deactivations on the Hub side publish a `learnstack.hub.custom-domain.activated`
(and `.deactivated`) Dapr pub/sub event; LearnStack listens, invalidates the
`ICacheService` entry, and the next edge fetch picks up the change. Tenant deletions
similarly invalidate via Dapr pub/sub.

A request to an org-scoped subdomain (`branch-istanbul.example.edu`) resolves to the
parent tenant id **plus** an organization id; the rest of the page render gets
appropriate filtering for free because the API resolves that same host itself and scopes
the request to the organization on its mapping row. The scope comes from the host
lookup, never from a header — headers are assertions the API compares against its own
answer ([ADR-0036](../decisions/0036-tenant-resolution-trusted-inputs.md)).

**One page load, from the browser to a tenant-scoped render.** The edge resolves
for rendering; the API resolves again, for itself.

```mermaid
sequenceDiagram
    Browser->>Edge: GET https://english.example.com/tr/courses
    Edge->>Edge: cache lookup host -> {tenantId, organizationId?}
    alt cache miss
        Edge->>API: GET /v1/tenants/resolve-host?host=english.example.com
        API->>API: SELECT FROM platform_host_to_tenant WHERE host = $1
        API-->>Edge: { tenantId, organizationId?, branding }
        Edge->>Edge: cache (60s SWR)
    end
    Edge->>Next: forward with x-tenant-id (assertion), x-organization-id?, x-locale
    Next->>API: fetches state X-LearnStack-Host + X-LearnStack-Hop-Secret
    API->>API: resolve the host independently; compare any assertion
    API-->>Next: tenant- + org-scoped responses
    Next-->>Browser: rendered HTML
```

In text, for a reader whose renderer does not draw it:

1. The browser requests a page on a tenant's host.
2. The edge looks the host up in a short-TTL cache.
3. On a miss it asks the API, which reads `platform_host_to_tenant`, and caches
   the answer for 60 seconds.
4. The edge forwards to Next.js with `x-tenant-id` as an **assertion**, plus the
   organization and locale it resolved for rendering.
5. Next.js calls the API stating the **visitor's host** over the trusted hop —
   `X-LearnStack-Host` with `X-LearnStack-Hop-Secret`.
6. The API resolves that host itself and compares any assertion against its own
   answer; a mismatch is a 404.
7. The API returns tenant- and organization-scoped data, and Next.js renders.

## Rendering Strategies

Per segment:

| Segment | Strategy | Notes |
|---|---|---|
| `(public)` | SSR with cache (ISR-like) | Pages built on demand, cached by tenant+slug+locale. Revalidated by webhook on publish. |
| `(studio)` | SSR, no cache | Always fresh; authentication required at the edge. |
| `(portal)` | SSR for lesson shell, CSR for player | Player benefits from client-side state; shell needs SEO/auth. |

Static export is not used; tenants are resolved at request time and the renderer needs per-request context.

## Theming

A tenant's branding flows from the API as design tokens. The renderer applies them as
CSS variables on the document root. When the resolved request carries an organization
id and that organization has a `BrandingOverride`, the override merges on top of the
tenant defaults before injection — the merged token set is the source of truth for the
SSR'd page.

```html
<html style="--brand-primary: #1f3a8a; --brand-on-primary: #ffffff; --brand-font: 'Inter';">
```

Tailwind reads these variables via
`theme.extend.colors.brand.primary = 'rgb(var(--brand-primary) / <alpha-value>)'`.
The first paint is themed; there is no FOUC because tokens are injected into the SSR'd
HTML.

Logo and font assets are URLs (served from CDN). Custom fonts are validated and
rate-limited at upload to prevent unbounded font payloads.

A `ThemeProvider` is **not** introduced unless dynamic theme switching is needed; the
CSS-variable approach handles the static-per-request case (one render = one theme = one
merge of tenant + optional org) more cheaply.

## Data Fetching

Two paths:

- **Server-side** — React Server Components and route handlers call the .NET API directly using the typed SDK in `packages/sdk`. The SDK is generated from the backend's OpenAPI document; [Standards 07 § SDK](../standards/07-frontend-architecture.md) owns when and how.
- **Client-side** — interactive components fetch through a thin BFF endpoint under `/api/...` that forwards the request with the user's JWT and the tenant header. The BFF exists to keep API base URLs and CORS off the public web origin, not to wrap business logic.

The SDK is the only sanctioned way to call the API. Hand-rolled `fetch('/v1/...')` calls are blocked by lint — `no-restricted-globals`, because `fetch` is a global and an import rule could never have caught a single call.

## Authentication on the Frontend

Auth is delegated to the identity provider (Keycloak/Authentik, see [Identity and Authentication](13-identity-and-auth.md)). The frontend uses OIDC Authorization Code with PKCE.

- Session is held in HTTP-only cookies set by the BFF after callback.
- Refresh is handled silently by the BFF; the frontend never sees the refresh token.
- Studio and Portal segments require an authenticated session at the edge; unauthenticated requests redirect to the identity provider.
- Public segment is anonymous-by-default; authenticated users get personalised hero blocks etc.

## Page-Block Resolver

The CMS stores pages as ordered lists of blocks. The renderer resolves each block to a
component using a two-tier registry:

```ts
type BlockResolver = {
  register(key: string, component: BlockComponent): void;
  resolveAsync(tenantId: string, key: string): Promise<BlockComponent>;
};

// Tier 1 — at startup, code-registered built-in primitives:
resolver.register('hero', HeroBlock);
resolver.register('rich-text', RichTextBlock);
resolver.register('image', ImageBlock);
resolver.register('content-list', ContentListBlock);
resolver.register('card-grid', CardGridBlock);

// Tier 2 — tenant-defined blocks via TenantPageBlock (ADR-0018):
// the renderer fetches the tenant's TenantPageBlock catalog from the API and dynamically
// resolves keys against a JSON-Schema-driven composite renderer that knows how to read
// the block's data shape and dispatch to a registered renderer-key (e.g. 'default-card').
```

There is **no `english.vocabulary-list` block in code**. A tenant that wants vocabulary
cards declares a `TenantContentType` (`VocabularyCard`) plus a `TenantPageBlock`
(`vocabulary-list` → renderer-key `content-list`); the renderer reads the schema, queries
the content entries, and renders the list with the chosen `content-list` composite.
Different tenants get different blocks **without code changes** — this is the runtime
counterpart to [Page Builder](17-page-builder.md) and the
[Tenant Customization Model](32-tenant-customization-model.md).

Unknown keys render a placeholder (a small "block unavailable" notice in studio
preview, an empty fragment in production). Schema-version mismatches between a
block's stored data and its current `TenantPageBlock` schema fall back to the placeholder
plus a console warning in studio.

Server Components are preferred for blocks; Client Components are used only for blocks
with interactivity (forms, video players, the live classroom panel).

## Entitlement-Aware UI

The frontend reads the tenant's entitlement projection through a thin API endpoint
backed by `platform_entitlement_cache`. Two hooks expose the data:

```ts
const recordingEnabled = useFeatureFlag(FeatureKeys.ClassroomRecording);
const { current, limit, soft } = useLimit(LimitKeys.ConcurrentLiveSessions);
```

Three UI patterns flow from these:

1. **Feature gating.** Tabs / nav items / buttons whose feature key is not in the
   entitlement projection are hidden, not greyed out. Example: the *Custom Domain* tab
   in Studio is absent for tenants whose plan doesn't include `FeatureKeys.CustomDomain`.
2. **Limit visualization.** Studio shows `current / limit` for limit keys
   (`100/500 users`, `12,000/50,000 classroom minutes`) with a colour ramp at 80% / 95%.
   Limits projected as `soft` show a banner but allow the action; `hard` block at the
   API layer (the frontend re-renders the blocking error from RFC 7807).
3. **Upgrade nudges.** A blocked action surfaces a link to the tenant's plan management
   page in the **Hub** (or, on Self-Hosted, a `mailto:` to the LearnStack vendor) —
   never an in-app "upgrade now" form, because the storefront and billing for the
   tenant's *own* LearnStack subscription live on Hub-side, not in `apps/web`.

The entitlement projection is invalidated eagerly on the
`learnstack.hub.entitlement` Dapr pub/sub event (15-min TTL is the upper bound, not the
typical refresh window).

## Custom Domains

Custom-domain registration, DNS validation, and TLS issuance are **Hub-owned admin
actions**. The tenant-facing surface in `apps/web` is read-only:

- Studio shows the current custom-domain status (`pending-dns`, `pending-tls`,
  `active`, `failed`) by reading the entitlement projection (which mirrors Hub's
  `CustomDomain` aggregate).
- Studio renders a banner with the DNS records the tenant must add and a "Recheck now"
  button that **proxies to a Hub admin endpoint** through the internal API.
- Registering a *new* custom domain happens in the **operator portal**
  (`operator-portal`), not in `apps/web`. Tenant admins request a domain via a form
  in Studio that creates a support ticket / Hub-side request — the actual create is an
  operator action.

Full flow: [27-custom-domain-tls.md](27-custom-domain-tls.md). Auth-cookie scoping for
custom domains uses SameSite-Lax with explicit `Domain=` per active host; no cookies
shared across tenants.

## Live Classroom Integration

The classroom screen is a Client Component under `(portal)/sessions/[id]/room`. It:

- Calls the API to obtain a short-lived LiveKit join token.
- Connects to the configured LiveKit server (URL provided by the API, **not** hardcoded — supports both self-hosted and Cloud configurations).
- Uses `@livekit/components-react` for the UI shell (participants, controls, screen share).
- Renders the lesson-context panel from the API (lesson plan, vocabulary, instructor notes).

The classroom screen is the only place that knows the LiveKit URL; the rest of the application is provider-agnostic.

## Performance Budgets

The public renderer has hard budgets:

- Time to First Byte: < 200 ms at the origin under steady state.
- Largest Contentful Paint: < 2.5 s on a mid-tier mobile device on 4G.
- JavaScript shipped on the public segment: < 150 KB gzipped initial route bundle.

Studio and Portal have higher budgets because they are authenticated apps and benefit from client-side state.

CI runs Lighthouse on representative public pages on every PR; budgets failing the threshold fail the build.

## Accessibility

- WCAG 2.2 AA is the target.
- `axe-core` runs in component tests; violations fail the test.
- Keyboard navigation and focus order are reviewed before any block ships.
- Color contrast is verified for every branded theme — tenant brand tokens that violate contrast cannot be saved.

## Splitting into Multiple Apps Later

The operator portal split has already happened: `operator-portal` is a *separate
repository*, not a separate app within this repo. Within `apps/web`, if and when the
single-app model breaks down (rebuild times, deploy cadence conflicts, separate teams
owning different surfaces), the split path is:

1. Extract `packages/ui` first — duplicated primitives become a shared package. (This
   is the same `packages/ui` candidate that, post-extraction, could be a build-time
   dependency for `operator-portal` as well.)
2. Extract `packages/sdk` — already generated, easy lift.
3. Move `(studio)` into `apps/studio`. Keep `(public)` and `(portal)` together
   initially.
4. Move `(portal)` into `apps/portal` only when its needs diverge from `(public)`.

The route-segment structure today is deliberately shaped to make this extraction
mechanical.

## Risks

- **Per-tenant SSR cost** — caching is per `(tenantId, organizationId?, locale, slug)`.
  Cardinality is bounded; budget memory headroom.
- **Cookie domain scoping** — tenants on custom domains complicate auth cookies. Use
  SameSite-Lax + explicit `Domain=` per host; do not share auth cookies across tenants.
  Domain registration and TLS flow:
  [27-custom-domain-tls.md](27-custom-domain-tls.md).
- **Brand-token contrast failures** — surface a warning at save time, not a render-time
  surprise. The contrast check also runs against the merged tenant+org token set, not
  only the tenant defaults.
- **Block schema drift** — tenants editing their `TenantPageBlock` schema while pages
  have stored content against the older shape. The renderer's placeholder path keeps
  this safe; the customization editor surfaces the drift at save time and offers a
  migration hint.
- **Entitlement projection staleness** — the 15-min TTL is a fallback; eager
  invalidation via the Dapr event is the typical path. A tenant whose plan was just
  upgraded but whose UI hasn't refreshed sees the new features within seconds of the
  Hub publishing the event, not 15 minutes.
