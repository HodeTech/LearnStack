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
- Translatable fields use Pattern A or Pattern B storage below. There is no
  `isLocalized` keyword in the content-type schema profile. Education's inline lesson
  body is a JSON object per translation locale, including repeated non-translatable
  values; the exact content-type revision pin belongs to its lesson root
  ([Education data model](../modules/education/README.md#data-model-and-invariants)).
- Slug is unique per `(tenant_id, locale)`, enforced on the translation table and flat
  across organizations — see [§ Pattern A](#pattern-a--side-translation-table-default-for-content-shaped-entities).
- Fallback chain: requested → tenant default → field-level fallback (if allowed) → render-safe missing-content state.

> **Open in Phase 02d.** Whether a read resolves under a disabled locale and what a tenant with no locale rows
> serves (G13), and which document owns the display fallback chain — this list and
> [Localization § Fallback Rules](../architecture/12-localization.md#fallback-rules)
> state different ones (G24) — are open in
> [Phase 02d's decision register](../roadmap/phase-02d-walking-skeleton.md#the-decision-register).

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
   key to the parent is composite on `tenant_id`. The factory derives this scope;
   the independent INSERT/UPDATE
   [parent-scope trigger](05-database.md#parent-organization-mirrors) verifies it,
   alongside the organization immutability guard. Neither RLS nor the tenant-only
   composite foreign key can prove a correct organization mirror alone.
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
    locale          varchar(35) NOT NULL,
    title           text NOT NULL,
    summary         text NULL,
    slug            varchar(160) NOT NULL,
    PRIMARY KEY (course_id, locale),
    CONSTRAINT ux_course_translations_tenant_id_locale_slug
        UNIQUE (tenant_id, locale, slug),
    CONSTRAINT fk_course_translations_course
        FOREIGN KEY (tenant_id, course_id) REFERENCES courses (tenant_id, id)
        ON DELETE CASCADE
);
```

This sketch shows where the translatable columns and the slug key live. The table's
complete DDL — its foreign-key index, policy set and triggers — is
[Database Standards § Translation satellite tables](05-database.md#translation-satellite-tables),
the canonical artefact.

The accepted P02d-1 Education satellites have no independent `id`, audit timestamps,
concurrency token or `deleted_at`. They belong to their own aggregate root's lifecycle
and audit capture. A draft translation reserves its slug on insertion; the flat key
keeps that reservation when the parent is soft-deleted. Parent eligibility excludes
the content from public reads. Phase 05 decides any future release together with
Phase 04's redirect/slug registry.

#### Education slug grammar

Education's routable slugs and non-routable course `slug_key` use 1–160 characters:
lowercase ASCII letters and digits, separated by single interior hyphens. UUIDs in
32-hex (`N`) or hyphenated (`D`) form are refused. Invalid case, whitespace or native
script is rejected with no implicit lowercasing, trimming or transliteration; the
content itself remains Unicode. The same predicate is an application check and a
named database check in the canonical DDL. The Education width is separate from
Tenancy's 63-character hostname slug limit. This accepted storage grammar does not
choose P02d-4's public route template or parameter handling.

Slug lookup is **exact** on `(tenant_id, locale, slug)`. The fallback chain resolves
display fields after the entity is found; it never resolves a slug. An entity with no
translation in the requested locale has no URL in that locale, and a link to it is
omitted rather than rendered dead.

A slug collision is refused when the translation is inserted; its writing command
returns `Result.Fail(business_rule_violation, …)`. P02d-2
[G11](../roadmap/phase-02d-walking-skeleton.md#the-decision-register) remains open;
the pass that resolves it names the selected command and its concrete error mapping
here and in [Phase 04's collision criterion](../roadmap/phase-04-cms-media-pages.md#completion-criteria).
The refusal names the conflicting entity when the caller may read it — tenant-wide rows
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
| Rich content with version history | A (side table; the entity's lifecycle owns publication and versioning) |
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

- IETF BCP 47 identity uses the shipped `LocaleTag` validator and canonicalizer:
  lowercase language, Title-cased script and uppercase region (`tr`, `en-GB`,
  `tr-TR`, `zh-Hans`).
- Education translation keys store the full canonical code in `varchar(35)`, never a
  truncated or fully lowercased form. This is G6 (a)'s accepted storage decision.
- Tenant locale membership is validated through a Tenancy application contract when
  the P02d-2 writer lands, not through a cross-chain Education foreign key.

> **Open in Phase 02d.** Request-parameter canonicalization remains G6 (b), and
> whether a platform registry bounds a tenant's enabled set remains G13. No
> `LearnStack.SharedKernel.Locales` namespace or platform registry exists today. These
> remaining parts are in
> [Phase 02d's decision register](../roadmap/phase-02d-walking-skeleton.md#the-decision-register).

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
