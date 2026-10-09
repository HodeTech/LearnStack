---
name: add-i18n-key
description: >
  Add a translation key under
  `frontend/apps/web/src/i18n/messages/<locale>/<namespace>.json`
  with the project's dotted-feature namespace + ICU MessageFormat conventions.
  USE FOR: adding a user-facing string, renaming a key (with deprecation), removing
  a key. DO NOT USE FOR: backend `LocalizedMessage` keys (those follow the
  `lockey_*` prefix invariant; see `LocalizedMessage` glossary entry), notification
  template content (lives in `TenantTemplateLibrary` as tenant data), or admin
  Studio-only debug strings.
---

# Adding an i18n key

## Purpose

Manage user-facing translations in `apps/web` consistently per
[12-localization.md](../../../docs/architecture/12-localization.md) +
[08-localization.md](../../../docs/standards/08-localization.md) +
[ADR-0008 Localization Schema](../../../docs/decisions/0008-localization-schema.md).

> **G39 foundation delivered — P02d-6 Step 1, 2026-10-10.**
> [ADR-0027](../../../docs/decisions/0027-frontend-i18n.md) selects exact
> `next-intl` 4.14.9 and the catalogue home below. Step 1 installs the runtime,
> complete English/Turkish catalogues and guarded ICU/callsite checks. Its
> [delivery record](../../../docs/roadmap/phase-02d-walking-skeleton.md#p02d-6-step-1-localization-and-document-foundation)
> owns validation and the remaining product/accessibility proof boundary. No
> `pnpm lint:i18n`, `no-literal-strings`, `_deprecated.json`, screenshot or
> `axe-core` task exists today; do not claim those checks run.

## When to use

- A new screen / component renders user-facing English (or any language) text.
- An existing key is being renamed (with the deprecation window).
- A key is being removed after the deprecation window.

## When not to use

- Backend `LocalizedMessage` keys returned by the API. Those have their own
  `lockey_*` namespace. The SDK normalizes API codes to `AppError`; it has no
  translation resource map. A UI feature owns any supported backend-message
  resources separately from general UI copy.
- Notification template content. Lives in `TenantTemplateLibrary` rows (data, not
  code).
- Debug-only strings or developer error messages.
- Tenant-customized content. Tenant content is rendered through
  `TenantContentType` entries, not the i18n key system.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Key | Yes | Dotted-feature namespace: `enrollment.list.empty_state.title`. |
| Default locale value | Yes | The English (or product-default) translation. |
| Other locales | Yes for supported UI languages | Complete `en` and `tr` initially. |
| Pluralisation / interpolation | If applicable | Use ICU MessageFormat. |

## Workflow

### Step 1: Pick the namespace

The accepted app-local catalogue layout is:

```text
frontend/apps/web/src/i18n/
  request.ts
  messages/
    en/public.json
    tr/public.json
```

P02d-6 supplies only `public`; later features add a namespace beneath each locale.
Request configuration mounts each file beneath its namespace. JSON nesting
supplies dotted identifiers: `public.catalog.course_count` is
`catalog.course_count` within the `public` translator.

Keys are dotted: `<namespace>.<feature>.<descriptor>`. Examples:

```
auth.signin.heading
auth.signin.email_label
auth.signin.password_label
auth.signin.submit
auth.signin.errors.invalid_credentials

enrollment.list.empty_state.title
enrollment.list.empty_state.cta_label

classroom.join.recording_warning
classroom.join.consent_prompt
```

Rules:

- Lowercase, dotted, snake_case for multi-word segments.
- Feature-namespaced — `auth.signin.errors.invalid_credentials`, not
  `errors.invalid_credentials` (no global error namespace).
- General UI copy does not copy the backend `lockey_*` namespace. The SDK returns
  normalized outcomes and message keys as data, not translations.
  [ADR-0027](../../../docs/decisions/0027-frontend-i18n.md#message-and-test-contract)
  assigns P02d-6 page-outcome-to-UI-key mapping to the web app; arbitrary backend
  message keys never become general UI lookup identifiers.

### Step 2: Add the key in every locale

Every used key MUST exist in each supported bundled UI catalogue. P02d-6 requires
complete `en` and `tr` `public` messages. These later enrollment examples show the
same layout; they do not introduce an enrollment catalogue in P02d-6.

```jsonc
// frontend/apps/web/src/i18n/messages/en/enrollment.json
{
  "list": {
    "empty_state": {
      "title": "No enrollments yet",
      "cta_label": "Enroll a learner",
      "description": "Once you enroll learners, they will appear here."
    }
  }
}
```

```jsonc
// frontend/apps/web/src/i18n/messages/tr/enrollment.json
{
  "list": {
    "empty_state": {
      "title": "Henüz kayıt yok",
      "cta_label": "Öğrenci kaydet",
      "description": "Öğrencileri kaydettiğinizde burada görünür."
    }
  }
}
```

### Step 3: ICU MessageFormat for plural / select / number

```jsonc
{
  "list": {
    "count": "{count, plural, =0 {No learners} one {# learner} other {# learners}}",
    "status": "{status, select, active {Active} suspended {Suspended} other {Unknown}}"
  }
}
```

Usage:

```tsx
const t = useTranslations("enrollment.list");
return <p>{t("count", { count: learners.length })}</p>;
```

### Step 4: Variable interpolation

ICU placeholders: `{name}`, `{count}`, `{date, date, short}`. The accepted
`next-intl` 4.14.9 runtime handles ICU using the selected UI catalogue's locale.
Dynamic values enter as plain text parameters, never HTML or rich-text callbacks.

`src/i18n/request.ts` uses the same server-only request-cached verified admission
loader as document/layout/page consumers. That loader neither imports next-intl
nor reads messages. The canonical locale comes from the signed target and live
site membership, never next-intl middleware `requestLocale`, callsite overrides,
query data, cookies or `Accept-Language`. Do not install i18n routing middleware.

Select one whole UI catalogue by exact canonical tag, successive rightmost-subtag
removal, then platform `en`. An unauthored UI language does not refuse or redirect
an enabled content locale. API content locale and document language remain the
admitted locale; fallback UI groups carry their actual language and direction.
A missing used key within a supported catalogue fails validation; it never
triggers per-key fallback. See
[ADR-0027's contract](../../../docs/decisions/0027-frontend-i18n.md#message-and-test-contract).

### Step 5: Don't branch on locale

This is bad:

```ts
if (locale === "tr") { /* Turkish-specific logic */ }
```

It's a violation of [08-localization.md § Locale-Independence](../../../docs/standards/08-localization.md).
Locale is data, not control flow. If you need locale-dependent behaviour, encode
it as data (date formats, currency, plural rules ICU already knows).

### Step 6: Rename a key (deprecation)

1. Add the new key with the same value as the old key.
2. Update all call sites to use the new key.
3. Record the planned removal date (≥ 1 release window). The sketched
   `_deprecated.json` is not implemented; do not assume a tracking file exists.
4. After the window, remove the old key from every locale file.

### Step 7: Remove a key

1. Confirm zero call sites: `rg "<old.key>" frontend/apps/web/src/`.
2. Remove the key from every locale's JSON file.
3. Remove the deprecation tracking entry, if present.

### Step 8: Trailer

If the change adds / renames / removes user-facing keys, the commit carries the
`I18n:` trailer per [14-git-workflow.md § Trailers](../../../docs/standards/14-git-workflow.md):

```
I18n: enrollment.list.empty_state.title, enrollment.list.empty_state.cta_label
```

## Validation

P02d-6 implements these accepted obligations in the guarded frontend suite; the
acceptance record alone is not passing evidence:

- Nonempty catalogues with equal key sets and valid ICU messages, including
  matching argument names/types across supported languages.
- Typed callsite checking or a checked census rejects absent-from-all keys and
  misspelled identifiers. Planted missing-key, unknown-callsite, malformed-ICU
  and mismatched-argument controls must fail.
- Formatter failures select the bounded translated unavailable state without raw
  keys, parameters or library diagnostics in the document or logs.
- Production HTML/RSC proofs cover locale fallback, language/direction and
  concurrent hosts/locales without unnecessary client catalogue payloads.
- Record actual implemented commands and results. Screenshot/axe tooling is not
  supplied by acceptance; Phase 06 owns the full Playwright/axe suite.

## Common pitfalls

- **Hardcoded English in JSX.** Move platform copy to a translation key. No
  `no-literal-strings` rule is installed by the acceptance record.
- **Per-locale branching.** If you find yourself doing
  `if (locale === "tr") ...`, encode the behaviour as data via ICU.
- **Confusing language fallback with a missing key.** ADR-0027 selects a
  whole fallback catalogue only for an unauthored UI language; missing keys in
  supported bundled catalogues must fail validation, not mix languages per key.
- **Renaming without deprecation window.** Stale references break the build for
  every consumer.
- **Mixing backend keys with general UI copy.** `LocalizedMessage` keys retain
  their `lockey_*` API contract. A feature rendering them owns explicit supported
  error resources; the SDK does not supply translations.
- **Long keys.** A key over ~80 chars is a sign the namespace is wrong. Split.
- **`I18n:` trailer missing.** Without it, `git log --grep` for translation
  changes is broken.
