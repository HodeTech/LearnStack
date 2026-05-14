# ADR 0008: Localization Schema

## Status

Accepted

## Decision

LearnStack uses field-level localization with tenant-configurable locales. The persistence shape is chosen per entity:

- **Side translation table** for entities with multiple translatable fields, SEO metadata, or per-locale slugs (e.g. `Course`, `Lesson`, `Page`, `ContentEntry`). The parent table holds non-translatable columns; a `<entity>_translations` table holds the translatable fields keyed by `(<entity>_id, locale)`.
- **JSONB localized value** for short, atomic fields with few translatable strings (e.g. `Level.display_name`, `Tag.label`, `Category.name`).

Slugs are always per locale and unique within `(tenant_id, locale)`.

## Context

LearnStack is multi-tenant and many tenants will serve multilingual audiences. The English-learning vertical alone needs at minimum Turkish and English for the same content. Retrofitting localization after tenant tables ship is expensive — every translatable column would need a migration plus a translation-table backfill.

Two patterns are needed because they have different cost profiles:
- Side tables enable per-locale slugs, SEO metadata, and clear "untranslated" detection but add a join per query.
- JSONB localized fields keep small lookups cheap and avoid joins for taxonomy-style data.

Choosing the wrong pattern for a given entity is a localised cost (extra joins or N+1 reads); choosing no pattern at all and adding `title_tr` / `title_en` columns is a schema disaster that takes a global migration to fix.

## Consequences

- Tenancy module owns `tenant_locales` (default locale + enabled locales).
- New tenant-owned tables containing user-facing text must declare which pattern they use; the migration linter rejects ad-hoc per-locale columns.
- Slugs are stored on the translation row, not on the parent.
- Search indexes are per-locale (Meilisearch index per locale or a `locale` filter applied uniformly).
- Fallback chain is computed once per request and applied consistently across content reads.
- Notification templates ship translations via the same side-table pattern.
- SEO metadata (`hreflang`, canonical, `<html lang>`) is emitted per locale.

## References

- [Localization Architecture](../architecture/12-localization.md)
- [Localization Standards](../standards/08-localization.md)
- Superseded ADR file: [_redirects/0005-i18n-schema.md](_redirects/0005-i18n-schema.md) (kept for old links).
