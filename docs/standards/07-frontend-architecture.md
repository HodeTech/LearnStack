# 07 — Frontend Architecture Standards

**Status:** Active
**Derives from:** [ADR 0009 — Frontend Single App First](../decisions/0009-frontend-single-app-first.md), [ADR 0004 — Authentication Strategy](../decisions/0004-authentication-strategy.md).

Next.js App Router layout, tenant resolution, SDK shape, and runtime concerns. See [03-frontend-coding.md](03-frontend-coding.md) for code-level style.

## Apps and Packages

LearnStack starts with a **single** Next.js app using route groups. Splitting into separate apps is deferred until coordination cost demands it.

```
frontend/
  app/
    (public)/         # tenant public site
      [...slug]/
      layout.tsx
    (studio)/         # admin studio
      layout.tsx
    (portal)/         # learner + instructor
      layout.tsx
    api/              # route handlers
    middleware.ts     # tenant resolution edge middleware
    layout.tsx        # root layout

  packages/
    ui/               # design system primitives
    sdk/              # generated API client + types
    config/           # eslint, tsconfig, tailwind shared configs
    i18n/             # locale messages + helpers
    auth/             # Auth.js wiring + OIDC client config
```

Migration to multiple apps later is feasible because route groups isolate concerns at the layout level.

## Server Components by Default

- **Server Components (RSC)** are the default for every page and component.
- Add `"use client"` only for interactivity, hooks, browser APIs, third-party client-only libraries.
- RSCs fetch through the SDK directly; they access server env vars and cookies.
- Pass typed primitives across the RSC → Client boundary; never pass class instances or closures.

## Tenant Resolution

```mermaid
flowchart TD
  req[Request lands at edge]
  mw[middleware.ts]
  host[Read Host header]
  reg{Host in tenant registry?}
  studio{Studio/Portal route?}
  reject[Return 404]
  ctx[Set tenant + locale headers + cookies]
  route[Continue to route handler / RSC]

  req --> mw --> host --> reg
  reg -- yes --> ctx --> route
  reg -- no --> studio
  studio -- yes --> ctx
  studio -- no --> reject
```

Rules:
- Edge middleware is the **only** place that resolves a tenant from a host.
- Resolved tenant attached as `X-Tenant-Id` header and a server-readable cookie.
- SDK reads tenant from request headers (server) or request-scoped context (client). Client never invents the tenant.
- Studio and Portal users pick a tenant via a switcher; choice persisted in JWT claim and validated cookie.
- Public-site URLs do **not** carry the tenant in the path; locale yes, tenant no.

## Locale Resolution

- Public-site URL: `/{locale}/...`.
- Default locale from tenant settings.
- Locale propagated as `X-Locale` to downstream API calls.
- Client-side locale switching triggers `router.push` to the new locale path.

## SDK

The SDK is the only allowed way to talk to the API from frontend code.

- **Generated** from OpenAPI (`@learnstack/sdk`) on CI.
- Typed end to end.
- Reads tenant + locale from request headers (server) or context (client).
- Maps Problem Details → typed `AppError`.
- Handles auth tokens (refresh, expiry) transparently via Auth.js.

```ts
import { sdk } from "@learnstack/sdk/server";

export default async function CourseListPage() {
  const courses = await sdk.education.listPublishedCourses({ limit: 20 });
  return <CourseList courses={courses.items} />;
}
```

## Auth

- Auth.js for session management.
- OIDC provider: Keycloak.
- Sessions in `HttpOnly`, `Secure`, `SameSite=Lax` cookies.
- Server Components and Server Actions read the session via `auth()`; never read tokens in Client Components.

## Routing

- File-based App Router.
- Route groups: `(public)`, `(studio)`, `(portal)`.
- Dynamic segments use `[slug]`, catch-all `[...slug]`.
- `params` and `searchParams` server-side; thread through carefully.
- Each route group has its own `layout.tsx`, `loading.tsx`, `error.tsx`.

## Public Site Renderer

- Renders **published** pages, courses, blog content.
- Server-side rendering with `revalidate` based on tenant + content type.
- Block rendering pulls from a block registry (`packages/blocks`); blocks register a React component plus a JSON schema.
- Preview tokens enable draft rendering for editors.

## Admin Studio

- Server-rendered shell; data-heavy screens use Client Components with optimistic UI.
- Tenant switcher in the top bar (platform admin sees all tenants; tenant admin sees only their tenants).
- Permission-aware UI hides unavailable actions but never replaces server-side authorization.
- Drafts and publishing flows explicit; published state visible.

## Portal (Learner + Instructor)

- Membership-gated routes.
- Lesson player: Server Component shell + Client Component for media playback.
- Classroom join: client requests a join token from the backend, then connects via the LiveKit web SDK.
- Reconnection states visible to the user.

## Tenant Branding

- Tenant theme tokens loaded at the layout level via RSC.
- Tokens map to CSS variables; Tailwind reads them via `--ls-primary`, `--ls-bg`, etc.
- Theme JSON shape part of tenant settings; tenant-admin editor surfaces it.

## Live Classroom UI

- Join token requested **only when entering the session**, never on page load.
- Token TTL ≤ 1 hour; refresh requires server-side re-authorization.
- Device permission prompts explicit: separate screens for "allow microphone", "allow camera".
- Reconnect indicator visible.
- Recording indicator visible **whenever** recording is active, regardless of who started it.

## State

- Local state for view-only.
- Server state cached via TanStack Query in Client Components.
- URL state (search params) for filterable lists.
- Avoid global Zustand/Redux stores unless multiple unrelated routes share the same mutable client state.

## Error UI

- Route-level `error.tsx` shows a graceful boundary.
- Inline form errors at field level.
- Toasts for transient feedback; modals for action-required errors.
- 404 page renders the tenant's brand if a tenant is resolved.

## Loading UI

- Route-level `loading.tsx` provides a skeleton shell, not a blank page.
- Suspense boundaries scope streaming to meaningful units.
- No "loading…" spinners for resources expected to take < 250 ms.

## Performance

- Image: `next/image` with `priority` for above-the-fold heroes.
- Font: `next/font` self-hosted.
- Streaming with `<Suspense>` to ship hero content first.
- Lazy load below-the-fold blocks.
- Lighthouse budgets: see [15-performance.md](15-performance.md).

## Security

- Strict CSP with nonces.
- No `dangerouslySetInnerHTML` outside a sanitization wrapper.
- Form CSRF: Server Actions verify the Auth.js session; explicit CSRF tokens for non-Action mutating routes.
- Outbound URLs validated against an allow-list before rendering.

## Tooling

- ESLint with `@learnstack/config/eslint`.
- TypeScript strict mode.
- Prettier formatted on commit.
- Storybook for the design system in `packages/ui`.
- Visual regression for the public renderer.

## Future

- Mobile-native portal out of scope until web is stable.
- Offline support out of scope.
- Splitting into multiple Next.js apps is a Phase-9+ consideration.
