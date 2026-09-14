# 03 — Frontend Coding Standards

**Status:** Active
**Derives from:** [ADR 0009 — Frontend Single App First](../decisions/0009-frontend-single-app-first.md).

TypeScript, React, and Next.js coding conventions. Frontend *architecture* (App Router layout, tenant resolution, SDK shape) is in [07-frontend-architecture.md](07-frontend-architecture.md).

## Language Settings

- TypeScript with `"strict": true` and `"noUncheckedIndexedAccess": true`.
- Target: `ES2022` minimum.
- Module resolution: `bundler`.
- `noImplicitAny`, `strictNullChecks`, `strictFunctionTypes`, `strictPropertyInitialization` all on.
- `verbatimModuleSyntax: true` to make `type` imports explicit.

## Naming

| Element | Convention |
|---------|------------|
| Files | `kebab-case.ts(x)` |
| React components | `PascalCase` |
| Component files | `PascalCase.tsx` |
| Hooks | `useCamelCase` |
| Types & interfaces | `PascalCase` (prefer `type` over `interface`) |
| Constants | `UPPER_SNAKE_CASE` for true constants; `camelCase` for derived |
| Enums | Avoid TS enums; prefer string literal unions or `as const` objects |
| Variables / functions | `camelCase` |
| Test files | `*.test.ts(x)` colocated |

## Imports

- Use `import type` for type-only imports.
- Group: 1) standard, 2) external, 3) `@learnstack/*` packages, 4) local. Blank line between groups.
- Absolute imports across features; relative within a feature.
- No circular imports — tested in CI.

## Types

- Prefer `type` aliases over `interface` (except when extending library interfaces).
- Avoid `any`. Use `unknown` and narrow.
- Discriminated unions for state machines:

```ts
type AsyncState<T> =
  | { status: "idle" }
  | { status: "loading" }
  | { status: "success"; data: T }
  | { status: "error"; error: AppError };
```

- `as const` for tuples and literal arrays.
- `satisfies` for shape checks without widening:

```ts
const ROLES = ["learner", "instructor", "tenant-admin"] as const satisfies readonly Role[];
```

## React Components

- Functional components only.
- Default to **Server Components**; mark `"use client"` only when needed (interactivity, hooks, browser APIs).
- Named exports; default re-export only when the route handler requires it.
- Props typed via a dedicated type, not inline.

```tsx
type CourseCardProps = {
  readonly course: PublicCourseSummary;
  readonly onEnroll?: (courseId: CourseId) => void;
};

export function CourseCard({ course, onEnroll }: CourseCardProps) {
  // ...
}
```

## Hooks

- Custom hooks start with `use`.
- One hook per file under `src/.../hooks/`.
- Rules-of-hooks enforced by ESLint.
- Effect dependency overrides include a comment.

## State Management

- Local state for view-only concerns.
- Server Components + URL search params for filterable lists.
- `useReducer` for complex client state.
- TanStack Query for client-side server-state caching.
- Avoid global client stores unless multiple unrelated routes share the same mutable client state.

## Data Fetching

- Server Components call the typed SDK directly.
- Client Components route through server actions or RSC props; never call the API with bearer tokens directly.
- Cache keys carry the tenant, the organization where applicable, and the locale; the
  rule lives in
  [Security Standards § Multi-Tenant + Organization Isolation Review Checklist](11-security.md#multi-tenant--organization-isolation-review-checklist).
- API errors mapped to typed `AppError` before reaching UI code (see [09-error-handling.md](09-error-handling.md)).

## Forms

- React Hook Form for non-trivial forms; controlled inputs for simple ones.
- Zod schemas for client-side validation; reuse on the server when possible.
- Inline error rendering; submit button stays enabled but the failure surface is accessible.

## Styling

- Tailwind CSS for utility-first styling.
- Design tokens are the `--ls-*` CSS custom properties that
  [Frontend Architecture Standards § Tenant Branding](07-frontend-architecture.md#tenant-branding)
  names. Their defaults are declared in `apps/web/src/app/globals.css`, and the shared
  Tailwind preset in `packages/config/tailwind` reads them. `packages/ui` holds no
  tokens, because [ADR-0009](../decisions/0009-frontend-single-app-first.md) extracts a
  shared package only when duplication is real. Tenant theme overrides are applied at
  layout level.
- No inline `style={{}}` except for runtime-computed values (e.g. progress bar width).
- `clsx` / `tailwind-merge` for conditional class composition.

> **Open in Phase 02d.** Which tenant tokens exist and the value each accepts is G16;
> how they reach the server-rendered HTML is G42. Both are in
> [Phase 02d's decision register](../roadmap/phase-02d-walking-skeleton.md#the-decision-register),
> and the pass that closes each gate edits this section with its answer.

## Server Actions

- Live in `app/.../actions.ts`.
- Each action validates input with Zod before doing work.
- Each action returns a typed `Result<T, AppError>`.
- Server actions never read secrets from the client.

## Async

- Async functions return `Promise<T>` and accept an `AbortSignal` when cancellable.
- No `.then()` chains in app code; use `await`.
- Handle errors with try/catch at boundaries; do not swallow.

## React Strict Patterns

- No `useEffect` for derived state; compute inline.
- No `useEffect` for one-shot fetches in Server Components territory (the RSC does the fetch).
- Effects pure: setup → cleanup; no side effects on every render.

## Performance

- `dynamic(() => import(...), { ssr: false })` for heavy, client-only components.
- Image: `next/image` with explicit `width`/`height`; never raw `<img>` for content images.
- Font: `next/font` self-hosted.
- Parallel fetches in RSC via `Promise.all`; no waterfalls.
- Memo (`useMemo`, `React.memo`) only when profiled and proven to help.

## Accessibility

- All interactive elements keyboard-reachable.
- Form inputs have associated labels.
- Headings follow document outline (no skipped levels).
- Color contrast meets WCAG 2.2 AA.
- See [16-accessibility.md](16-accessibility.md).

## Logging and Errors (Client)

- `console.error` only via a centralized `logger` wrapper that ships to Sentry.
- Never `alert()`. Use toast or modal system.
- Error boundaries at route-group level for graceful fallbacks.

> **Open in Phase 02d.** `apps/web` has no `logger` wrapper and no Sentry client yet.
> Which parts of
> [Observability Standards § Frontend Observability](10-observability.md#frontend-observability)
> ship in Phase 02d, the wrapper's Sentry capture among them, and which phase owns the
> rest are G35. Whether the `(public)` route group ships its own error, loading and
> not-found files in Phase 02d is G40. Both are in
> [Phase 02d's decision register](../roadmap/phase-02d-walking-skeleton.md#the-decision-register),
> and the pass that closes each gate edits this section with its answer.

## Forbidden

- `any` without a comment explaining why.
- `// @ts-ignore` — use `@ts-expect-error` with a comment if absolutely necessary.
- Direct `fetch` from Client Components.
- Direct `localStorage` / `sessionStorage` outside a `clientStorage` wrapper.
- React class components.
- Default exports for components consumed across modules.
- Mutating props.
- `dangerouslySetInnerHTML` without a sanitization wrapper.

## File Organization

```
app/
  (public)/
    page.tsx
    components/
    hooks/
    lib/
  (studio)/
    layout.tsx
    page.tsx
    ...
  (portal)/
    ...

packages/
  ui/                # design system primitives
  sdk/               # generated API client
  config/            # shared configs
  i18n/              # locale messages + helpers
```

Feature folder layout:

```
features/<feature>/
  components/      # presentational
  hooks/           # behavior
  lib/             # pure helpers
  actions.ts       # server actions
  schemas.ts       # zod schemas
  types.ts
```

> **Open in Phase 02d.** Three questions these trees answer are open for the public
> renderer. Where the UI string catalogue lives is G39: `packages/i18n/` is one of
> several homes the corpus names, and `frontend/packages` holds no such package. What
> `packages/sdk` becomes once regeneration fills `paths` is G31; today it holds
> generated types beside hand-written factories. Where the lesson page's composite and
> primitive field components live — `packages/ui/`, a route group's or a feature's
> `components/`, or elsewhere — is G41. Each is a row in
> [Phase 02d's decision register](../roadmap/phase-02d-walking-skeleton.md#the-decision-register),
> and the pass that closes it edits this section with its answer.

## Comments

- Comment the *why* of non-obvious code, not the *what*.
- Don't restate JSX.
- Public component APIs have JSDoc.
