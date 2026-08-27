# Localization

LearnStack is multi-tenant and many tenants will serve multilingual audiences. The English-learning vertical alone needs at minimum Turkish and English for the same content. Localisation cannot be a retrofit.

The schema choice is made before any tenant-owned table ships. See [ADR 0008 — Localization Schema](../decisions/0008-localization-schema.md).

## Scope

Localisation covers:

- **Content** — translatable fields on tenant-owned entities: course title/description, lesson title, page block content, content entry fields, navigation labels, level display names.
- **UI** — frontend strings in the public site, learner portal, instructor portal, admin studio.
- **Communications** — email/SMS notification templates per locale.
- **System messages** — Problem Details `title`/`detail` for human-facing errors.
- **Slugs and routes** — locale-specific URLs (`/tr/kurslar/...`, `/en/courses/...`).

Out of scope for the initial implementation:

- RTL languages (Arabic, Hebrew). The schema supports them but layout-level RTL is deferred until a tenant requires it.
- Plural forms beyond ICU MessageFormat defaults (Turkish has different rules than English; ICU handles both).

## Locale Identifiers

- Format: BCP 47 (`en`, `tr`, `en-US`, `tr-TR`). LearnStack stores locale codes as canonical lowercase BCP 47 strings.
- A tenant declares its **available locales** and one **default locale**.
- A user can have a **preferred locale**; if absent, the tenant default is used; if the requested resource doesn't have content in that locale, fallback rules apply (see below).

## Tenant Locale Configuration

```sql
-- in tenancy module
CREATE TABLE tenant_locales (
    tenant_id UUID NOT NULL,
    locale TEXT NOT NULL,
    is_default BOOLEAN NOT NULL,
    is_enabled BOOLEAN NOT NULL DEFAULT TRUE,
    sort SMALLINT NOT NULL DEFAULT 0,
    PRIMARY KEY (tenant_id, locale)
);
```

A tenant with no `tenant_locales` row falls back to the platform default (`en`).

## Storage Schema: Translatable Fields

Two patterns are used; the choice depends on the entity shape.

### Pattern A: Side Table (for content with many translatable fields)

For entities like `Course`, `Lesson`, `Page`, `ContentEntry`:

```sql
CREATE TABLE courses (
    id              uuid PRIMARY KEY,
    tenant_id       uuid NOT NULL,
    organization_id uuid NULL,          -- null = tenant-wide, per ADR-0017
    slug_key        text NOT NULL,      -- stable authoring handle; NOT routable
    visibility      text NOT NULL,
    -- non-translatable columns only: no title, no description, no slug
    created_at      timestamptz NOT NULL,
    -- ...
    CONSTRAINT ux_courses_tenant_id_slug_key UNIQUE (tenant_id, slug_key),
    CONSTRAINT ux_courses_tenant_id_id       UNIQUE (tenant_id, id)
);

CREATE TABLE course_translations (
    course_id       uuid NOT NULL,
    tenant_id       uuid NOT NULL,      -- a real column: RLS is per table, never inherited
    organization_id uuid NULL,          -- mirrors the parent; for RLS, never for uniqueness
    locale          text NOT NULL,
    title           text NOT NULL,
    description     text NULL,
    slug            text NOT NULL,      -- locale-specific URL slug
    seo_title       text NULL,
    seo_description text NULL,
    PRIMARY KEY (course_id, locale),
    -- The routing constraint. Every column in it is NOT NULL, so PostgreSQL's
    -- nulls-are-distinct rule cannot apply and it rejects every duplicate.
    CONSTRAINT ux_course_translations_tenant_id_locale_slug
        UNIQUE (tenant_id, locale, slug),
    CONSTRAINT fk_course_translations_course
        FOREIGN KEY (tenant_id, course_id) REFERENCES courses (tenant_id, id)
        ON DELETE CASCADE
);
```

> **Corrected 2026-08-09.** The block above previously declared
> `CREATE UNIQUE INDEX course_translations_slug_unique ON
> course_translations (course_id, locale, slug)`. Those index columns are a proper
> superset of the primary key `(course_id, locale)`, so the primary key already
> guaranteed it and the index could reject no row the table would otherwise accept — two
> different courses in one tenant could both hold `/en/courses/beginner`. The block also
> claimed translation tables inherit tenant ownership through a parent check constraint,
> which PostgreSQL does not do. Both are fixed here; the rule is published in
> [Localization Standards § Pattern A](../standards/08-localization.md).

- A row in the parent table represents the entity. A row in the translation table
  represents the entity *in a specific locale*.
- Slug is per-locale; the same course has `/tr/kurslar/baslangic-ingilizce` and
  `/en/courses/beginner-english`.
- `slug_key` is an authoring convenience — a stable handle for translators and for
  import / export. **Nothing routes on it.** The routable identifier is
  `course_translations.slug`.
- Translation tables carry `tenant_id` as a real column and declare their own
  `ENABLE` + `FORCE ROW LEVEL SECURITY` and the full policy set from the canonical
  template in [Database Standards](../standards/05-database.md). Row Level Security is
  per table; it is not inherited from a parent through a check constraint, and a
  satellite carrying `title` and `slug` carries the content.
- `organization_id` mirrors the parent and exists **only** so the satellite can carry the
  same isolation predicate. It is deliberately absent from the slug constraint — see
  [§ Slugs and URLs](#slugs-and-urls). Denormalizing it is safe because
  `organization_id` on a tenant-owned row is immutable after insert; see
  [Database Standards § Translation satellite tables](../standards/05-database.md).
- The foreign key is composite on `tenant_id` for the reason in
  [Database Standards § Foreign keys between tenant-owned tables](../standards/05-database.md):
  referential-integrity checks run with Row Level Security bypassed, so a single-column
  key would let one tenant's translation row reference another tenant's course.

### Pattern B: JSONB Field (for compact, optional translations)

For value-style fields like `Level.display_name`, `Tag.label`, `Category.name`:

```sql
CREATE TABLE levels (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL,
    key TEXT NOT NULL,
    taxonomy_key TEXT NOT NULL,
    sort SMALLINT NOT NULL,
    display_name JSONB NOT NULL,    -- { "en": "Beginner", "tr": "Başlangıç" }
    UNIQUE (tenant_id, taxonomy_key, key)
);
```

The application reads with a helper that performs fallback:

```csharp
public static string Resolve(JsonElement localizedField, string requestedLocale, IReadOnlyList<string> fallbackChain)
{
    if (localizedField.TryGetProperty(requestedLocale, out var direct) && direct.ValueKind == JsonValueKind.String)
        return direct.GetString()!;
    foreach (var fb in fallbackChain)
        if (localizedField.TryGetProperty(fb, out var fbVal) && fbVal.ValueKind == JsonValueKind.String)
            return fbVal.GetString()!;
    return string.Empty;
}
```

Pattern B is cheaper for short fields where joining a translation table is overkill, and avoids N+1 issues when listing many rows. Use it for short, atomic, mostly-required strings.

### Choosing Between Patterns

| Field shape | Pattern |
|---|---|
| Long text, multiple fields per entity, SEO metadata | A (side table) |
| Short atomic string, few fields | B (JSONB) |
| Rich content with version history | A (side table, with `is_published`, `version`) |
| Taxonomy display names | B |

## Fallback Rules

When the requested locale is unavailable:

1. Try the requested locale (e.g. `tr-TR`).
2. Try the language part of the requested locale (`tr`).
3. Try the tenant's default locale.
4. Try the platform default (`en`).
5. Return an empty string or a marked placeholder (e.g. `[Untranslated]` in development, empty in production).

The fallback chain is computed once per request and reused.

## Slugs and URLs

Slugs are **per locale**. Two patterns:

- **Locale-prefixed paths**: `/tr/kurslar/baslangic`, `/en/courses/beginner`. Default.
- **Locale-by-host**: `english.learnstack.io` always English, `ingilizce.learnstack.io` always Turkish. Available as a tenant-level configuration.

The Next.js renderer reads tenant locale config at the edge and produces locale-aware routes.

Slug uniqueness is `UNIQUE (tenant_id, locale, slug)`, declared on the translation table,
and **flat across organizations**. The same entity can have completely different slugs
per locale; two different entities in one tenant cannot share one slug in one locale.

`organization_id` is deliberately not part of that key. A host resolves to
`(tenant_id, organization_id?)`, and the canonical isolation policy admits
`organization_id IS NULL OR organization_id = <caller's org>` — so an organization's host
serves tenant-wide rows *and* its own, both tiers compete for one URL, and a key that
partitioned them would force the renderer to pick a winner. Preferring the
organization-scoped row means publishing a branch course silently changes what an
already-published tenant URL serves, with no redirect and no signal to the author who
owns the tenant-wide row — the "URL changes are breaking" risk below, arrived at without
anyone editing a slug. Preferring the tenant-wide row is worse: it lets an organization
author create a row no URL can reach. One flat namespace per `(tenant_id, locale)`
removes the question. An organization that wants its own variant of a shared course gives
it its own slug, and the publish command rejects the collision with a business-rule
failure rather than resolving it at render time.

The routing consequences follow directly, and are behaviour rather than defects:

- A host resolving to `(tenant_id, NULL)` serves tenant-wide rows only. Organization-scoped
  rows are filtered out by the isolation policy, so their slugs 404 there even though the
  slug is reserved tenant-wide.
- A host resolving to `(tenant_id, organization_id)` serves both tiers, and the flat key
  guarantees at most one match.
- Slug lookup is **exact**. The fallback chain below resolves display fields after the
  entity is found; it never resolves a slug. An entity with no translation in the
  requested locale has no URL in that locale, and a link to it is omitted rather than
  rendered dead.

## UI String Catalogue

UI strings (button labels, validation messages, empty-state copy) live in JSON catalogues under the frontend.

```text
apps/web/locales/
  en/
    common.json
    portal.json
    studio.json
  tr/
    common.json
    portal.json
    studio.json
```

Keys are dotted, namespaced by feature, ICU MessageFormat for plural/select. The frontend uses a lightweight i18n library (e.g. `next-intl` or `react-intl`); the choice is captured in [Frontend Architecture](14-frontend-architecture.md).

API responses do **not** localise system-level identifiers, only human-facing strings. Error codes are stable English strings; human-readable messages are localised by the consumer when needed, using the locale from the JWT or request.

## Notification Template Localisation

Notification templates are a **tenant customization aggregate** owned by
`LearnStack.Modules.Customization` per
[ADR-0018](../decisions/0018-tenant-driven-customization-model.md) — see
[32-tenant-customization-model.md](32-tenant-customization-model.md). The
`TenantTemplateLibrary` aggregate stores per-channel, per-locale, optionally
per-organization template bodies authored as Liquid / Handlebars:

```sql
CREATE TABLE tenant_template_library (
    id              uuid PRIMARY KEY,
    tenant_id       uuid NOT NULL,
    organization_id uuid NULL,                     -- optional org override
    key             text NOT NULL,                 -- "enrollment.created", "live.session.reminder"
    channel         text NOT NULL,                 -- "email" | "sms" | "whatsapp" | "in_app"
    locale          text NOT NULL,                 -- "tr-TR", "en-US", ...
    subject         text NULL,                     -- channels with a subject (email)
    body            text NOT NULL,
    schema_version  int  NOT NULL DEFAULT 1,
    -- The AuditableEntity<TId> set, verbatim from Database Standards § Audit
    -- Columns. updated_* are NULL because MarkCreated stamps created_* only, so
    -- NOT NULL here would reject every INSERT; deleted_* are unconditional
    -- because AuditableEntity implements ISoftDelete for every aggregate.
    created_at      timestamptz NOT NULL DEFAULT now(),
    created_by      uuid NOT NULL,
    updated_at      timestamptz NULL,
    updated_by      uuid NULL,
    deleted_at      timestamptz NULL,
    deleted_by      uuid NULL,
    row_version     bigint NOT NULL DEFAULT 0,
    -- NULLS NOT DISTINCT (PostgreSQL 15+; LearnStack pins 18 per ADR-0031) is
    -- load-bearing here. organization_id is null on every tenant-wide template, and a
    -- standard UNIQUE treats nulls as distinct, so without it a tenant could hold
    -- unlimited duplicate tenant-wide rows for one (key, channel, locale) and dispatch
    -- would pick one arbitrarily. organization_id genuinely belongs in this key: an org
    -- override is a distinct row that dispatch resolves through the org -> tenant
    -- fallback chain described below. Contrast a routable slug, where the two tiers
    -- compete for one URL and the column is dropped from the key instead -- see
    -- Database Standards section Constraints.
    CONSTRAINT ux_tenant_template_library
        UNIQUE NULLS NOT DISTINCT (tenant_id, organization_id, key, channel, locale)
);


-- Row Level Security: apply the canonical template from
-- docs/standards/05-database.md § Tenant-Owned and Organization-Scoped Tables
-- to this table verbatim, substituting tenant_template_library. It is org-scoped,
-- so it takes the full set: ENABLE + FORCE, one permissive policy with the
-- organization term AND-ed in, and both AS RESTRICTIVE write guards. The SQL is
-- deliberately not repeated here — it lives in exactly one file, because the
-- version that lived in four was wrong in all four (ADR-0003 Amendment 3).
```

This table is org-scoped (`organization_id` is nullable and participates in the
uniqueness constraint), so its organization term lives **inside** the single policy —
per the canonical template in
[Database Standards](../standards/05-database.md) and
[ADR-0003 Amendment 3](../decisions/0003-tenant-isolation-defense-in-depth.md). A
separate organization policy would be `PERMISSIVE` and `OR`-ed with this one, which
would expose every tenant-wide template to every tenant.

Dispatch (in the Notifications module) resolves the recipient's preferred locale,
applies the organization → tenant → tenant-default fallback chain, then renders the
chosen row with the dispatch context. The two-table `notification_templates` +
`notification_template_translations` shape used in pre-2026-05-18 drafts is
superseded by this single aggregate.

## SEO Considerations

- `<html lang="...">` is set per locale.
- `<link rel="alternate" hreflang="...">` is emitted for every locale variant of a page.
- Canonical URLs include the locale prefix.
- Sitemaps are emitted per locale or with `<xhtml:link>` alternates.

## Working with Translators

- All translatable content has stable keys (slug_key, taxonomy key, template key).
- Export/import endpoints provide CSV or JSON for translation vendors (out of scope for MVP, planned for Phase 09 or later).
- Untranslated content is visible to admins as a clear gap, not silently rendered in another language.

## Risks

- **Schema retrofit** — adding translation tables later is expensive. The schema must be locale-aware from day 1; this is why the pattern is decided before any tenant table ships.
- **Search across locales** — a search query for "ingilizce başlangıç" should not match "beginner English." Search indexes are split per locale (`<env>-<kind>-<locale>`). See [Search](20-search.md) and [ADR 0012](../decisions/0012-search-strategy.md) for the indexing model.
- **Default-locale drift** — content authored in `en` and partially translated to `tr` is the normal state. Tools (admin UI) must show the gap clearly to avoid accidentally publishing untranslated pages.
- **URL changes** — changing a published slug is breaking. Redirects must follow; the CMS auto-creates a redirect on slug change.
