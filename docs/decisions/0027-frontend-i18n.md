# ADR-0027: Frontend UI Localization with next-intl

## Status

Proposed — maintainer approval pending; implementation has not started.

**Date:** 2026-10-09
**Deciders:** @cemil

This uses the reserved frontend-i18n number. It proposes closing P02d-6 G39;
it closes no gate until accepted. Phase 04's existing reservation remains binding
until acceptance moves the first-consumer commitment to P02d-6. The
[P02d-6 decision package](../roadmap/phase-02d-walking-skeleton.md#p02d-6-decision-package-2026-10-09)
owns the page plan and approval boundary.

## Decision Drivers

- P02d-6 introduces platform-authored navigation, empty/error states, attribution
  and accessibility labels; English literals cannot serve the bilingual tenant.
- Async Server Components need server-side messages without shipping the entire
  catalogue or a provider to every browser.
- Tenant-enabled content locales and the smaller set of translated UI catalogues
  have different owners. UI support must not narrow content admission.
- Three catalogue paths are currently documented. The first consumer needs one
  location, key grammar and mechanically checked translation contract.
- Host/locale admission, exact content locale and trusted transport are already
  accepted in ADR-0052/0053/0054. An i18n library must not replace those boundaries.
- Air-gapped rendering must use bundled messages and runtime locale data, without
  translation services, remote downloads or credentials.

## Considered Options

1. **next-intl, server-first, without its routing middleware** (recommended).
   Direct async App Router APIs fit the first consumers. Routing remains owned by
   the verified public entry, with one app-local request configuration.
2. **react-intl / FormatJS**. A viable ICU and React alternative, including its
   documented App Router support. Not selected because next-intl directly supplies
   the async server message APIs needed here; no broader formatting requirement
   currently outweighs that fit.
3. **Lingui**. A viable RSC alternative with extraction and compile-time tooling.
   Not selected because the first explicit JSON catalogues do not require its
   additional macro/extraction pipeline.
4. **A library-neutral lookup dictionary until Phase 04**. Not selected because
   the first plural/select messages, error states and translation checks would
   create a temporary parallel runtime at the first real consumer.

These are alternatives to the proposed decision, not assertions that the other
libraries cannot support Server Components. Reconsider the selection if a pinned
production-build proof fails, its licence changes incompatibly, or actual Studio
requirements demonstrate a material gap in extraction or formatting.

## Decision

LearnStack uses `next-intl` for platform-authored frontend UI messages, with
app-local JSON catalogues and server-first formatting. The existing verified
public entry owns host and content-locale admission; the library neither resolves
tenants nor redirects requests. UI catalogue fallback never changes the admitted
content locale.

This is the proposed decision. Acceptance authorizes the contracts below;
it does not claim implementation or test delivery.

## Context

ADR-0008 decides content storage, not the frontend UI runtime. P02d-4 delivers
exact enabled-locale content reads and locale-bearing fallback labels. P02d-5
delivers membership-first entry through mandatory native ingress. P02d-6 is the
first consumer that needs translated platform copy, earlier than the original
Phase 04 reservation.

The documentation currently names `packages/i18n`, `apps/web/locales` and
`apps/web/src/i18n/<locale>/<namespace>.json`. None exists as an implemented
catalogue. The proposed single home is:

```text
frontend/apps/web/src/i18n/
  request.ts
  messages/
    en/public.json
    tr/public.json
```

Only the `public` namespace ships in P02d-6. A later feature adds its own namespace
under the same locale directory. Shared-package extraction follows ADR-0009's
measured duplication trigger; an empty `packages/i18n` is not created.

### Dependency and execution boundary

Pin `next-intl` **4.14.9** exactly in the web app and commit the lockfile. The npm
metadata checked on 2026-10-09 declares **MIT**, Next 15 and React 19 peer support.
That is compatibility evidence, not a passing build; installation, licence-file
inspection, type checking and a production build are implementation obligations.
No paid service or network runtime is introduced.

Use the App Router plugin and request configuration without `next-intl` routing
middleware. Keep catalogue loading and server formatting behind `server-only`.
Async Server Components use the server APIs. A Client Component receives only the
translated labels its interaction needs, never the full catalogue by default.
The error boundary's retry control is such a bounded consumer; a provider is not
required merely to render static translated text.

### Content locale, UI messages and document language

The verified route locale remains the exact locale sent to the Education API.
Neither `Accept-Language`, a cookie nor a message-catalogue lookup selects it.
The route is admitted only through the live site's enabled-locale membership.

For platform UI copy, select an authored catalogue by exact canonical tag, then
remove one rightmost subtag at a time, then use the platform `en` catalogue.
P02d-6 supplies complete `en` and `tr` catalogues. A tenant-enabled `ar` or `tr-TR`
route remains admissible even when only fallback UI copy exists. Do not redirect
it or substitute the content language. Missing required keys in a bundled
catalogue are a build failure, not a request-time fallback to raw keys.

ICU formatting uses the selected catalogue's locale: English fallback copy uses
English plural rules even on a `zh` content route. The API locale and document
language remain `zh`.

The document's `lang` is the admitted content locale and its direction follows
that locale using runtime internationalization data. If that data cannot describe
an admitted tag, the deterministic direction fallback is `ltr`; it never refuses
the content locale. Platform UI groups whose catalogue language differs carry
their actual `lang` and direction. Pattern B labels use the API's resolved locale;
Pattern A titles, summaries and bodies do not gain cross-locale fallback.

### Message and test contract

Platform message identifiers retain the `lockey_` prefix, grouped under the
feature namespace. For example, `en/public.json` contains:

```json
{
  "lockey_catalog": {
    "course_count": "{count, plural, one {# course} other {# courses}}"
  }
}
```

The request configuration mounts that file under `public`. Its translator
resolves `lockey_catalog.course_count` with `{count}`. JSON nesting supplies the
dot separator; leaves use snake_case.
Values use ICU MessageFormat; dynamic content enters as plain
text parameters. Do not use rich-text translation callbacks, HTML messages,
authored URL attributes or client input as a message identifier.

The frontend suite checks a nonempty catalogue, equal key sets across supported
UI languages and valid ICU messages, including matching argument names/types.
Typed callsite-to-catalogue checking or a checked callsite census also rejects a
used key missing from every catalogue and a misspelled callsite identifier;
catalogue equality alone cannot prove coverage. Planted per-locale missing-key,
absent-from-all, unknown-callsite, malformed-ICU and mismatched-argument controls
must fail. Override library error/fallback behavior so a formatter failure yields
a bounded translated unavailable state, never a raw identifier, namespace,
parameter value or library diagnostic in the document or logs; exercise that path.
Request tests distinguish route locale, UI fallback and resolved label locale,
including region/script tags, unsupported-but-enabled languages and RTL. The
production fixture proves document/visible-text language and that catalogues and
server configuration do not cross into an unnecessary client payload.

## Consequences

### Positive

- The first public pages have translated copy and accessible labels from day one.
- One catalogue home and runtime replace the competing unimplemented sketches.
- UI translation coverage cannot accidentally become tenant content eligibility.
- Bundled messages preserve the offline deployment boundary.

### Negative

- The web app acquires a pinned runtime dependency and an App Router integration.
- Each platform-authored message needs both initial translations and checked ICU
  arguments. Falling back to English for an untranslated UI language remains a
  visible limitation, labelled with its actual language.
- Server and interactive error-state translation need separate bounded interfaces.

### Neutral

- Content satellites, `LocalizedText`, public DTOs, admission and API error codes
  retain their accepted contracts.
- Tenant-authored translations/editors remain Phase 04; full Studio/portal UI
  consumers and renderer expansion remain Phase 06.
- This chooses no date/number/currency product policy and adds no preference cookie.

## Implementation Notes

Acceptance moves ADR-0027 to Active ADRs with P02d-6 as its first-consumer gate.
The same decision commit reconciles Localization Standards, Localization
architecture, the frontend trees, the standards index, Phase 04 and the
`add-i18n-key` / `add-frontend-route` skills. Phase 04 consumes the installed
foundation and still owns its CMS/Studio message coverage.

P02d-6 implements catalogues, server configuration, bounded interactive labels and
the checks above. Each implementation step follows the packet's commit and two
independent review rounds. This Proposed file changes no Accepted ADR body.

## Architecture Tests

The obligations above are proposed proof requirements, not registered or passing
test names. Implementation registers the actual non-skippable rule names in
Standards 21 with planted controls. Existing public source-boundary and guarded
frontend-runner checks remain mandatory; no second skip-refusal rule is created.

## References

- [ADR-0008 — Localization Schema](0008-localization-schema.md)
- [ADR-0009 — Single Next.js App First](0009-frontend-single-app-first.md)
- [ADR-0052 — Anonymous Public Read Boundary](0052-anonymous-public-read-boundary.md)
- [ADR-0053 — Trusted Public Server Rendering](0053-trusted-public-server-rendering.md)
- [ADR-0054 — Bounded Public Renderer Admission](0054-bounded-public-renderer-admission.md)
- [Localization architecture](../architecture/12-localization.md)
- [Localization Standards](../standards/08-localization.md)
- [Frontend Architecture Standards](../standards/07-frontend-architecture.md)
- [next-intl — App Router setup](https://next-intl.dev/docs/getting-started/app-router)
- [next-intl — Server and Client Components](https://next-intl.dev/docs/environments/server-client-components)
- [next-intl — request configuration](https://next-intl.dev/docs/usage/configuration)
- [next-intl — error files](https://next-intl.dev/docs/environments/error-files)
- [next-intl package metadata](https://www.npmjs.com/package/next-intl/v/4.14.9)
- [FormatJS — React Intl](https://formatjs.github.io/docs/react-intl/)
- [Lingui — React Server Components](https://lingui.dev/tutorials/react-rsc)
