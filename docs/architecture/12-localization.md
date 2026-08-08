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
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL,
    slug_key TEXT NOT NULL,        -- locale-independent stable identifier
    visibility TEXT NOT NULL,
    -- non-translatable fields only
    created_at TIMESTAMPTZ NOT NULL,
    -- ...
    UNIQUE (tenant_id, slug_key)
);

CREATE TABLE course_translations (
    course_id UUID NOT NULL REFERENCES courses(id) ON DELETE CASCADE,
    locale TEXT NOT NULL,
    title TEXT NOT NULL,
    description TEXT,
    slug TEXT NOT NULL,            -- locale-specific URL slug
    seo_title TEXT,
    seo_description TEXT,
    PRIMARY KEY (course_id, locale)
);

CREATE UNIQUE INDEX course_translations_slug_unique
    ON course_translations (course_id, locale, slug);
```

- A row in the parent table represents the entity.
- A row in the translation table represents the entity *in a specific locale*.
- Slug is per-locale; the same course has `/tr/kurslar/baslangic-ingilizce` and `/en/courses/beginner-english`.
- Both tables include or join via `tenant_id` for RLS purposes (translation tables inherit tenant ownership through the parent via a check constraint).

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

Slug uniqueness is `(tenant_id, locale, slug)`. The same entity can have completely different slugs per locale.

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
    created_at      timestamptz NOT NULL DEFAULT now(),
    created_by      uuid NOT NULL,
    updated_at      timestamptz NOT NULL DEFAULT now(),
    updated_by      uuid NOT NULL,
    CONSTRAINT ux_tenant_template_library
        UNIQUE (tenant_id, organization_id, key, channel, locale)
);

ALTER TABLE tenant_template_library ENABLE ROW LEVEL SECURITY;
ALTER TABLE tenant_template_library FORCE  ROW LEVEL SECURITY;

CREATE POLICY tenant_template_library_isolation ON tenant_template_library
    USING (
        tenant_id = current_setting('app.tenant_id', true)::uuid
        AND (
            organization_id IS NULL
            OR organization_id = current_setting('app.organization_id', true)::uuid
            OR current_setting('app.scope', true) = 'tenant'
        )
    )
    WITH CHECK (
        tenant_id = current_setting('app.tenant_id', true)::uuid
        AND (
            organization_id IS NULL
            OR organization_id = current_setting('app.organization_id', true)::uuid
        )
    );
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
