# ADR-0027: Frontend UI Localization with next-intl

## Status

Accepted — 2026-10-09.

**Date:** 2026-10-09
**Deciders:** @cemil

The maintainer accepted this reserved frontend-i18n decision and P02d-6 G39 before
implementation. At acceptance, no implementation is delivered; later delivery is
recorded in dated amendments. P02d-6 owns the first consumer, and Phase 04 consumes
that foundation. The
[P02d-6 decision package](../roadmap/phase-02d-walking-skeleton.md#p02d-6-decision-package-2026-10-09)
owns the page plan and approval boundary.

## Decision Drivers

- P02d-6 introduces platform-authored navigation, empty/error states, attribution
  and accessibility labels; English literals cannot serve the bilingual tenant.
- Async Server Components need server-side messages without shipping the entire
  catalogue or a provider to every browser.
- Tenant-enabled content locales and the smaller set of translated UI catalogues
  have different owners. UI support must not narrow content admission.
- Competing unimplemented catalogue sketches need one location, key grammar and
  mechanically checked translation contract at the first consumer.
- Host/locale admission, exact content locale and trusted transport are already
  accepted in ADR-0052/0053/0054. An i18n library must not replace those boundaries.
- Air-gapped rendering must use bundled messages and runtime locale data, without
  translation services, remote downloads or credentials.

## Considered Options

1. **next-intl, server-first, without its routing middleware** (chosen).
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

These are alternatives to the decision, not assertions that the other
libraries cannot support Server Components. Reconsider the selection if a pinned
production-build proof fails, its licence changes incompatibly, or actual Studio
requirements demonstrate a material gap in extraction or formatting.

## Decision

LearnStack uses `next-intl` for platform-authored frontend UI messages, with
app-local JSON catalogues and server-first formatting. The existing verified
public entry owns host and content-locale admission; the library neither resolves
tenants nor redirects requests. UI catalogue fallback never changes the admitted
content locale.

Acceptance authorizes the contracts below; it does not claim implementation or
test delivery.

## Context

ADR-0008 decides content storage, not the frontend UI runtime. P02d-4 delivers
exact enabled-locale content reads and locale-bearing fallback labels. P02d-5
delivers membership-first entry through mandatory native ingress. P02d-6 is the
first consumer that needs translated platform copy, earlier than the original
Phase 04 reservation.

The documentation currently sketches `packages/i18n`, `apps/web/locales` and
`apps/web/src/i18n/<locale>/<namespace>.json`, with differing namespace layouts in
the frontend skills. None exists as an implemented catalogue. The single
home is:

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
Those ranges include the repository's exact **Next 15.5.18 / React 19.0.0** pins.
That is compatibility evidence, not a passing build; installation, licence-file
inspection, type checking and a production build are implementation obligations.
No paid service or network runtime is introduced.

Use the App Router plugin and request configuration without `next-intl` routing
middleware. Keep catalogue loading and server formatting behind `server-only`.
Async Server Components use the server APIs. A Client Component receives only the
translated labels its interaction needs, never the full catalogue by default.
The error boundary's retry control is such a bounded consumer; a provider is not
required merely to render static translated text.

`src/i18n/request.ts` calls a server-only, request-cached admission loader. That
loader re-verifies the existing ingress envelope, takes the canonical route locale
from its signed target, and checks live site enabled-locale membership. It is the
same loader used by document/layout/page consumers; it neither imports next-intl
nor reads messages, avoiding a configuration cycle or another bootstrap call.
After admission, a pure selector chooses the UI catalogue and the configuration
returns its locale and `{public: messages}`. The callback does not read next-intl's
middleware-derived `requestLocale` or use a callsite locale override as authority.
It adds no locale header, i18n middleware, rewrite, cookie or `Accept-Language`
selection. Route params and observed query data cannot replace the signed source.

The accepted `/{locale}/status/not-found` namespace uses that same live admission;
it never loads Education content. Admitted `/studio` and `/portal` scaffolds have
no content locale and retain platform English until Phase 06 supplies their UI.
A failed admission cannot select a tenant document through English fallback.
Production tests prove this integration without next-intl routing middleware,
including concurrent hosts/locales and configuration use before page rendering.

### Content locale, UI messages and document language

The verified route locale remains the exact locale sent to the Education API.
Neither `Accept-Language`, a cookie nor a message-catalogue lookup selects it.
The route is admitted only through the live site's enabled-locale membership.

For platform UI copy, select an authored catalogue by exact canonical tag, then
remove one rightmost subtag at a time, then use the platform `en` catalogue.
P02d-6 supplies complete `en` and `tr` catalogues. A tenant-enabled `ar` or `tr-TR`
route remains admissible even when only fallback UI copy exists. Do not redirect
it or substitute the content language. Fallback selects a whole UI catalogue when
that UI language is absent; a missing required key within any supported bundled
catalogue is a build failure, never per-key fallback or a raw-key response.

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

General platform UI identifiers use lowercase dotted feature namespaces and
snake_case segments, without `lockey_`. For example, `en/public.json` contains:

```json
{
  "catalog": {
    "course_count": "{count, plural, one {# course} other {# courses}}"
  }
}
```

The request configuration mounts that file under `public`. The `public`
translator resolves `catalog.course_count` with `{count}`; the full identifier is
`public.catalog.course_count`. JSON nesting supplies the dot separators.
Values use ICU MessageFormat; dynamic content enters as plain
text parameters. Do not use rich-text translation callbacks, HTML messages,
authored URL attributes or client input as a message identifier.

Backend `LocalizedMessage.Key` and Problem Details `messageKey` retain their
`lockey_` wire contract, including backend error-localization resources. They are
distinct from general UI identifiers. The SDK validates those payloads and maps
known machine codes to `AppError`; it owns no translated resources. P02d-6's web
app maps supported page outcomes to explicitly owned feature UI keys. It never
uses arbitrary backend message keys, titles, field errors or parameters as lookup
identifiers or visible copy; unknown outcomes select the bounded unavailable
state. This does not remove the backend-message localization contract for future
form consumers, whose UI owns its supported error resources.

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
including region/script tags, unsupported-but-enabled languages and RTL. A
test-owned enabled `ar` locale and eligible content exercise `lang="ar"` /
`dir="rtl"`, with English fallback UI labelled `lang="en"` / `dir="ltr"`.
Do not rewrite the historical seed or claim an Arabic UI catalogue. The production
fixture proves document/visible-text language and that catalogues and server
configuration do not cross into an unnecessary client payload.

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

The acceptance commit moves ADR-0027 to Active ADRs with P02d-6 as its first-consumer
gate and reconciles these carriers:

- [Standards 03](../standards/03-frontend-coding.md) for UI keys/checks,
  [07](../standards/07-frontend-architecture.md) for the `packages/i18n` sketch and
  [08](../standards/08-localization.md#strings-in-code) for its
  `packages/i18n/locales/{locale}.json` sketch.
- [Standards 09](../standards/09-error-handling.md#mapping-problem-details--ui)
  and the [glossary](../glossary.md#cross-cutting-concerns) for backend wire keys,
  SDK normalization and UI-owned message resolution.
- [Localization architecture](../architecture/12-localization.md),
  [Frontend architecture](../architecture/14-frontend-architecture.md),
  the [standards index](../standards/README.md) and
  [Phase 04](../roadmap/phase-04-cms-media-pages.md) for location and ownership.
- The [add-i18n-key](../../.claude/skills/add-i18n-key/SKILL.md) and
  [add-frontend-route](../../.claude/skills/add-frontend-route/SKILL.md) skills
  for the same catalogue home, namespace grammar and server request integration.

Phase 04 consumes the installed foundation and still owns its CMS/Studio message
coverage. The decision commit makes no installation or implementation claim.

P02d-6 implements catalogues, server configuration, bounded interactive labels and
the checks above. Each implementation step follows the packet's commit and two
independent review rounds. This acceptance changes no previously Accepted ADR.

## Architecture Tests

The acceptance commit registers these non-skippable rules with planted controls
in [Standards 21](../standards/21-architecture-tests-catalogue.md#p02d-6-public-ui-localization-controls),
before implementation:

- [Ui_Catalogues_Cover_Public_Call_Sites](../standards/21-architecture-tests-catalogue.md#ui_catalogues_cover_public_call_sites)
- [Public_Ui_Locale_Does_Not_Change_Content_Admission](../standards/21-architecture-tests-catalogue.md#public_ui_locale_does_not_change_content_admission)
- [Public_Theme_Emits_Only_Validated_Color_Tokens](../standards/21-architecture-tests-catalogue.md#public_theme_emits_only_validated_color_tokens)
- [Public_Pages_Preserve_Approved_Response_States](../standards/21-architecture-tests-catalogue.md#public_pages_preserve_approved_response_states)
- [Public_Pages_Expose_Localized_Accessible_Semantics](../standards/21-architecture-tests-catalogue.md#public_pages_expose_localized_accessible_semantics)

All five are Registered at acceptance, not passing implementation claims. Status
becomes Implemented only with the actual checks.
Existing [public source-boundary controls](../standards/21-architecture-tests-catalogue.md#p02d-5-public-server-rendering-controls)
and [No_Architecture_Test_Is_Skippable](../standards/21-architecture-tests-catalogue.md#no_architecture_test_is_skippable)
remain mandatory; no second skip-refusal rule is created.

## Amendments

### Amendment 1 — P02d-6 document foundation delivery (2026-10-10)

P02d-6 Step 1 implements the exact next-intl 4.14.9 dependency/lockfile and App
Router plugin, complete app-local English/Turkish `public` catalogues, server-only
request configuration and key/ICU/argument/callsite controls with planted failures.
UI fallback preserves content locale; formatter failures select bounded copy.

Admission/content loaders use a private weak identity memo keyed only by Next's
exact request-store headers object to retain same-request work across framework
error rendering. Header values, host, locale and the ingress envelope are not
cache keys; no representation crosses incoming requests or gains a new authority
carrier. `getPublicUi` uses React cache for selected messages/translators within
an RSC render, separately from admission/content-read deduplication.

Step 1 also supplies document language/direction, atomic four-color style-element
injection, tenant chrome, bounded interactive error labels, loading/state views
and the live-admitted fixed status page, which performs no Education read.
Catalog/course pages remain Step 2; lesson presentation remains Step 3. The
[Step 1 delivery record](../roadmap/phase-02d-walking-skeleton.md#p02d-6-step-1-localization-and-document-foundation)
owns current validation, review and product/accessibility proof status. The
Decision, Status, Date and Deciders remain unchanged.

### Amendment 2 — P02d-6 pages and automated product proof (2026-10-10)

Steps 2–3 implement localized catalog, course and ordered plain-text lesson pages,
independent opaque pagination and eligible metadata from shared request-local
loaders. Known content failures use bounded translated HTTP 200/noindex views;
missing or hidden details use the approved local 307 to a fixed localized 404.
Resolved API label language remains distinct from UI fallback language.

Step 4 adds separate real API/production Next product modes for concurrent
host/locale isolation, authored RTL content, atomic theme fallback, publication
freshness and content-failure recovery. The
[delivery record](../roadmap/phase-02d-walking-skeleton.md#p02d-6-step-4-product-proof-and-accessibility-closeout)
owns execution and review evidence. Actual manual keyboard, focus, reflow,
contrast and screen-reader evidence remains pending; P6 completion and
Accessibility promotion are not claimed. This delivery note changes no decision
or historical amendment. Phase 04 consumes the implemented UI foundation;
Phase 06 owns the full renderer/Studio and Playwright/axe expansion, and P02d-7
owns the browser demo and Lighthouse activation.

### Amendment 3 — P02d-6 manual accessibility closeout (2026-10-10)

P02d-6 completes its four implementation steps and two independent review rounds
per step. The [packet closeout](../roadmap/phase-02d-walking-skeleton.md#p02d-6-packet-closeout-2026-10-10)
records keyboard, focus, 320 CSS px desktop reflow and calculated contrast
observations, followed by the maintainer's passing real VoiceOver checks on the
English and Turkish catalog → course → lesson flows and missing/invalid-link
recovery views. The earlier pending amendments remain historical.

Accessibility Standards becomes Active for delivered public-page enforcement;
the five P6 catalogue entries become Implemented with named existing tests.
Implementation is complete; maintainer PR review and merge remain separate.
This delivery note changes no accepted decision and claims no full WCAG audit,
Arabic screen-reader check or zoom-conformance result. P02d-7 still owns the
browser demo/Lighthouse and Phase 06 owns full Playwright/axe expansion.

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
