# 03 — Frontend Coding Standards

**Status:** Active
**Derives from:** [ADR 0009 — Frontend Single App First](../decisions/0009-frontend-single-app-first.md).
Public UI localization derives from [ADR-0027](../decisions/0027-frontend-i18n.md).

TypeScript, React, and Next.js coding conventions. Frontend *architecture* (App Router layout, tenant resolution, SDK shape) is in [07-frontend-architecture.md](07-frontend-architecture.md).

## Language Settings

- TypeScript with `"strict": true` and `"noUncheckedIndexedAccess": true`.
- Target: `ES2022` minimum.
- Module resolution: `bundler`.
- `noImplicitAny`, `strictNullChecks`, `strictFunctionTypes`, `strictPropertyInitialization` all on.
- `verbatimModuleSyntax: true` to make `type` imports explicit.

### Current toolchain and lint subjects

[ADR-0054](../decisions/0054-bounded-public-renderer-admission.md) remediation pins
Node 22.23.1 in CI and `frontend/.nvmrc`; the workspace declares the same minimum.
Web CI and staged lint share `scripts/lint-web.mjs` inside the app: source, scripts
and root JS/TS configurations, including `.mts`/`.cts`. Generated `next-env.d.ts`,
`.next` and `.server` are excluded. Native ESM/CJS TypeScript helpers use an explicit
syntax parser outside the Next typecheck program. SDK/UI changes also run their
workspace lint in the hook. Generated native output is excluded from Prettier.

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

P02d-2 accepts and implements G16's theme value grammar; P02d-4 delivers its public
projection. [Tenant Branding](07-frontend-architecture.md#tenant-branding) owns that
contract. P02d-6 Step 1 implements G42's atomic server-rendered color injection;
the [delivery record](../roadmap/phase-02d-walking-skeleton.md#p02d-6-step-1-localization-and-document-foundation)
owns verification and the remaining product/manual accessibility proof boundary.

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

Accepted G35 assigns the browser logger, Sentry and web-vitals to Phase 11;
P02d-5 provides the bounded server caller and trace propagation, with no browser
observability hook. See
[Frontend Observability](10-observability.md#frontend-observability).
P02d-6 Step 1 implements public error/loading placement and localized state views.
[Standards 07](07-frontend-architecture.md#routing)
owns the approved status-route behavior.

## UI Messages

**P02d-6 G39 foundation delivered — Step 1, 2026-10-10.**
[ADR-0027](../decisions/0027-frontend-i18n.md) selects server-first `next-intl`
4.14.9 and `apps/web/src/i18n/messages/{en,tr}/public.json`. General UI identifiers
use lowercase dotted feature namespaces with snake_case segments, for example
`public.catalog.course_count`. Backend `lockey_*` keys retain their separate wire
contract; the SDK owns no translated resources.

Use ICU MessageFormat and plain text parameters. Do not use rich-text callbacks,
HTML messages, authored URL attributes or client input as a lookup identifier.
Require nonempty catalogues, equal key sets, valid ICU and matching argument
names/types, plus checked callsite coverage. Missing required keys fail the build;
formatter failures select a bounded translated unavailable state. Planted controls
must prove these checks can fail. See
[Localization Standards](08-localization.md#strings-in-code) for request integration
and UI/content-language separation.

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

**P02d-6 placement — Accepted 2026-10-09.** Step 1 implements G39 in
`apps/web/src/i18n/request.ts` and the app-local message home above; no
`packages/i18n` is created. G41 uses synchronous public views under
`apps/web/src/components/public/` for ordered plain-string `default-card` fields
only. Richer primitives remain Phase 04/05 and Phase 06. G31's delivered SDK
contract remains governed by [Standards 07](07-frontend-architecture.md#sdk).

## Comments

- Comment the *why* of non-obvious code, not the *what*.
- Don't restate JSX.
- Public component APIs have JSDoc.
