# 08 — Localization Standards

**Status:** Active
**Derives from:** [ADR 0008 — Localization Schema](../decisions/0008-localization-schema.md).

i18n is a platform-level concern. It affects translatable content, slugs, URLs, SEO, dates, numbers, currencies, formats, and the Admin Studio UI itself. See [docs/architecture/12-localization.md](../architecture/12-localization.md) for the strategy.

## Scope

Localization covers:

- Public content (pages, blocks, course metadata).
- Slugs.
- SEO metadata (title, description, OG tags).
- Notification templates.
- Admin Studio UI.
- Learner portal UI.
- Validation error messages.
- Date, time, number, currency formatting.

## Locale Model

- A tenant has a **default locale**.
- A tenant has a set of **enabled locales**.
- Translatable fields are explicitly marked at the schema level (`isLocalized: true`).
- Slug is unique per `(tenant_id, locale)`.
- Fallback chain: requested → tenant default → field-level fallback (if allowed) → render-safe missing-content state.

## URL Strategy

Public URLs:

```
/{locale}/{slug}                # primary
/{locale}/{section}/{slug}      # nested
```

Rules:
- Locale is **always** in the path on public URLs.
- The default locale does **not** get a special slot (no `/default/...`); requests without a locale prefix redirect to `/{tenant-default-locale}/...`.
- Custom domains resolve tenant first; locale resolves from the path.

## Translatable Storage

The storage shape is encapsulated behind the application contract; consumers see structured values.

Initial implementation: per-locale JSON object on the field.

```json
{
  "title": {
    "tr": "İngilizce Kursları",
    "en": "English Courses"
  }
}
```

Application contract returns a resolved string for the requested locale, applying the fallback chain.

## SEO

- `<html lang="{locale}">` set per page.
- `hreflang` annotations for every translated public page.
- Canonical URL is the requested locale.
- `og:locale` and `og:locale:alternate` set.

## Formatting

- Dates / times: `Intl.DateTimeFormat` on the frontend, `IFormatProvider` on the backend.
- Numbers: `Intl.NumberFormat` / `CultureInfo`.
- Currency: never store amounts as strings; store integer minor units + ISO currency code; format at presentation.
- Pluralization: use ICU MessageFormat (`{count, plural, one {# lesson} other {# lessons}}`).

## Strings in Code

- Frontend: `next-intl` (or equivalent) loaded from `packages/i18n/locales/{locale}.json`.
- Backend: localized strings live in resource files under each module.
- Strings are referenced by key, never duplicated:

```tsx
const t = useTranslations("CourseCard");
return <button>{t("enroll")}</button>;
```

```csharp
var msg = _stringLocalizer["course.publish.success"];
```

## Locale Codes

- IETF BCP 47: `tr`, `en`, `en-GB`, `de`. Lowercase.
- Always store the full code, not a truncated form.
- An enum-like registry of supported locales lives in `LearnStack.SharedKernel.Locales`.

## Right-to-Left

- The platform supports RTL languages from the start.
- Layout uses logical CSS properties (`padding-inline-start`, not `padding-left`).
- Components flip via `dir="rtl"` on the document root.

## Admin Studio UI

Admin Studio separates:

- **Platform UI language** — what the editor sees (Turkish or English).
- **Tenant content language** — what the editor edits.
- **Learner-facing course language** — the locale the learner experiences.

These three are independent. An editor may use Admin Studio in English while editing Turkish public pages and English course content.

## Notification Templates

- Templates are per-locale.
- A tenant can override the platform default template per locale.
- The dispatch system picks the recipient's locale, with fallback to tenant default.

## Testing

Localized content requires tests for:

- Requested locale render.
- Fallback locale render.
- Missing-translation render.
- Locale-specific slug uniqueness.
- Notification template selection per recipient locale.

## Forbidden

- Hardcoded user-facing strings in code (every visible string goes through the i18n layer).
- Concatenating sentences across translation keys (use ICU placeholders).
- Storing localized text in a non-localized field then "interpreting" it.
- Using locale-derived `if`s (`if (locale === "tr")`); branch on capabilities, not on locale identity.
- Truncating BCP 47 codes (`en-GB` ≠ `en`).
