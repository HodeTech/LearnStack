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
- Slug is unique per `(tenant_id, locale)`, enforced on the translation table and flat
  across organizations — see [§ Pattern A](#pattern-a--side-translation-table-default-for-content-shaped-entities).
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

Per [ADR-0008](../decisions/0008-localization-schema.md) two storage patterns coexist;
the choice is per-entity, not project-global. Both are encapsulated behind the
application contract — consumers see resolved values, not the on-disk shape.

### Pattern A — Side translation table (default for content-shaped entities)

Used for `Course`, `Lesson`, `Page`, `ContentEntry`, and anything with multiple
translatable fields, per-locale slugs, or SEO metadata. The parent table holds
non-translatable columns; a `<entity>_translations` table holds translatable fields
keyed by `PRIMARY KEY (<entity>_id, locale)`.

Four rules. A migration reviewer checks all four.

1. **The parent holds no translatable column.** No `title`, no `description`, no `slug`.
   A parent may hold a `slug_key` — a stable, locale-independent authoring handle — but
   nothing routes on it.
2. **The translation table carries `tenant_id` as a real column** and declares its own
   `ENABLE` + `FORCE ROW LEVEL SECURITY` and full policy set from the canonical template
   in [Database Standards](05-database.md). Row Level Security is per table; it is not
   inherited from a parent through a check constraint, and a satellite carrying `title`
   and `slug` carries the content. It also carries a mirrored `organization_id` when the
   parent is `[OrganizationScoped]`, for the isolation predicate only, and its foreign
   key to the parent is composite on `tenant_id`.
3. **Slug uniqueness is `UNIQUE (tenant_id, locale, slug)`** on the translation table.
   `UNIQUE (<entity>_id, locale, slug)` is **forbidden**: its columns are a proper
   superset of the primary key, so it can reject no row the table would otherwise
   accept, and two courses in one tenant end up sharing `/en/courses/beginner`.
4. **`organization_id` does not belong in a slug unique key**, even when the entity is
   organization-scoped. Two reasons, and the second survives fixing the first. In a
   standard `UNIQUE` constraint PostgreSQL treats nulls as distinct, so
   `UNIQUE (tenant_id, organization_id, locale, slug)` places no constraint at all on
   tenant-wide rows — the rows a tenant authors first. And repairing that with
   `NULLS NOT DISTINCT` still leaves an organization-scoped row and a tenant-wide row
   free to claim one slug, while a host resolving to `(tenant_id, organization_id)`
   serves both tiers and would have to pick a winner at render time. One flat namespace
   per `(tenant_id, locale)` is the rule; an organization that wants its own variant of a
   shared course gives it its own slug.

```sql
CREATE TABLE course_translations (
    course_id       uuid NOT NULL,
    tenant_id       uuid NOT NULL,
    organization_id uuid NULL,        -- mirrors the parent; for RLS, never for uniqueness
    locale          text NOT NULL,
    title           text NOT NULL,
    description     text NULL,
    slug            text NOT NULL,
    seo_title       text NULL,
    seo_description text NULL,
    PRIMARY KEY (course_id, locale),
    CONSTRAINT ux_course_translations_tenant_id_locale_slug
        UNIQUE (tenant_id, locale, slug),
    CONSTRAINT fk_course_translations_course
        FOREIGN KEY (tenant_id, course_id) REFERENCES courses (tenant_id, id)
        ON DELETE CASCADE
);
```

Slug lookup is **exact** on `(tenant_id, locale, slug)`. The fallback chain resolves
display fields after the entity is found; it never resolves a slug. An entity with no
translation in the requested locale has no URL in that locale, and a link to it is
omitted rather than rendered dead.

A slug collision returns `Result.Fail(business_rule_violation, …)` from the publish
command. It names the conflicting entity when the caller may read it — tenant-wide rows
and the caller's own organization's rows both qualify under the canonical policy — and
otherwise names only the slug and the locale, because naming a row in another
organization would leak across the boundary Row Level Security exists to hold.

### Pattern B — JSONB localized field (for compact taxonomy-style fields)

Used for `Level.display_name`, `Tag.label`, `Category.name`, and similar short
atomic strings where joining a translation table would be overkill.

```json
{
  "title": {
    "tr": "İngilizce Kursları",
    "en": "English Courses"
  }
}
```

### Choosing between patterns

| Field shape | Pattern |
|---|---|
| Long text, multiple fields per entity, SEO metadata | A (side table) |
| Short atomic string, few fields | B (JSONB) |
| Rich content with version history | A (side table, with `is_published`, `version`) |
| Taxonomy display names | B |

The full table + worked examples live in
[12-localization.md § Storage Schema](../architecture/12-localization.md). In both
patterns the application contract returns a resolved string for the requested locale,
applying the fallback chain.

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
- Locale-specific slug uniqueness, including the tenant-wide-versus-organization-scoped
  collision and the same-slug-different-locale case.
- Notification template selection per recipient locale.

## Forbidden

- Hardcoded user-facing strings in code (every visible string goes through the i18n layer).
- Concatenating sentences across translation keys (use ICU placeholders).
- Storing localized text in a non-localized field then "interpreting" it.
- Using locale-derived `if`s (`if (locale === "tr")`); branch on capabilities, not on locale identity.
- Truncating BCP 47 codes (`en-GB` ≠ `en`).
- Putting a translatable field — `title`, `description`, `slug` — on the parent table
  when the entity uses Pattern A.
- Putting `organization_id` inside a slug unique key (§ Pattern A rule 4), or declaring a
  slug constraint whose columns are a superset of the translation table's primary key.
- Falling back to another locale to resolve a **slug**. Fallback applies to display
  fields after the entity is found, never to the lookup.
