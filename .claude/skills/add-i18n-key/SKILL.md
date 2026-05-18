---
name: add-i18n-key
description: >
  Add a translation key under `frontend/apps/web/src/i18n/<locale>/<namespace>.json`
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

## When to use

- A new screen / component renders user-facing English (or any language) text.
- An existing key is being renamed (with the deprecation window).
- A key is being removed after the deprecation window.

## When not to use

- Backend `LocalizedMessage` keys returned by the API. Those have their own
  `lockey_*` namespace (the SDK maps API codes to client-side resources).
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
| Other locales | If translations exist | Each locale's translation. |
| Pluralisation / interpolation | If applicable | Use ICU MessageFormat. |

## Workflow

### Step 1: Pick the namespace

Translation files are split by **feature**:

```
frontend/apps/web/src/i18n/
  en/
    auth.json
    catalog.json
    classroom.json
    common.json
    enrollment.json
    notifications.json
    portal.json
    studio.json
  tr/
    ...
  ...
```

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
- API error codes are **not** repeated here — the SDK has its own resource map
  for `LocalizedMessage` keys.

### Step 2: Add the key in every locale

The convention: a key MUST exist in the default locale (en) before any non-default
locale. Don't ship a key with translations missing from `en`.

```jsonc
// frontend/apps/web/src/i18n/en/enrollment.json
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
// frontend/apps/web/src/i18n/tr/enrollment.json
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

ICU placeholders: `{name}`, `{count}`, `{date, date, short}`. The frontend i18n
library (next-intl / react-intl — see ADR-pending) handles ICU natively.

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
3. Mark the old key in a tracking file (`frontend/apps/web/src/i18n/_deprecated.json`)
   with the planned removal date (≥ 1 release window).
4. After the window, remove the old key from every locale file.

### Step 7: Remove a key

1. Confirm zero call sites: `grep -rn "<old.key>" frontend/apps/web/src/`.
2. Remove the key from every locale's JSON file.
3. Remove the tracking entry from `_deprecated.json`.

### Step 8: Trailer

If the change adds / renames / removes user-facing keys, the commit carries the
`I18n:` trailer per [14-git-workflow.md § Trailers](../../../docs/standards/14-git-workflow.md):

```
I18n: enrollment.list.empty_state.title, enrollment.list.empty_state.cta_label
```

## Validation

- `pnpm lint:i18n` (custom lint task) flags missing keys per locale.
- `pnpm test` is green; tests that depend on a key surface a clear failure if
  it's missing.
- Visual / screenshot tests show the key resolving in every locale, not the
  raw key string.
- `axe-core` accessibility test passes (translations don't break ARIA labels).

## Common pitfalls

- **Hardcoded English in JSX.** Lint rule `no-literal-strings` will reject. Move
  to a translation key.
- **Per-locale branching.** If you find yourself doing
  `if (locale === "tr") ...`, encode the behaviour as data via ICU.
- **Missing translation for non-default locale.** The build falls back to the
  default locale — and the user sees mixed languages. Tests catch this.
- **Renaming without deprecation window.** Stale references break the build for
  every consumer.
- **Putting backend `LocalizedMessage` keys here.** Those have their own
  `lockey_*` prefix and live with the API contract, not the i18n bundle.
- **Long keys.** A key over ~80 chars is a sign the namespace is wrong. Split.
- **`I18n:` trailer missing.** Without it, `git log --grep` for translation
  changes is broken.
