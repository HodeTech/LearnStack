# ADR 0013: Page Block Schema Versioning

## Status

Accepted

## Decision

Page blocks in LearnStack are versioned by `(key, schemaVersion)` tuple. Schema versions are immutable; breaking changes ship as a new `schemaVersion` rather than mutating an existing one. The runtime renders known versions, falls back to a placeholder for unknown versions, and supports a platform-admin bulk-migration path.

The full mechanism is described in [17-page-builder.md](../architecture/17-page-builder.md) § Block Lifecycle Rules; this ADR records the decision.

## Context

Page blocks are the primary CMS surface and the primary extension surface. Verticals register their own blocks (`english.vocabulary-list`, ...); core ships baseline blocks (`hero`, `rich-text`, `course-list`, ...). Once a block is used by even one published page in one tenant, its payload schema becomes load-bearing — changing the shape silently breaks rendering and corrupts content.

Three problems must be avoided:

1. **Silent breakage** — a developer renames a block field, deploys, every page using the block renders empty or throws.
2. **Cross-tenant migration cost** — different tenants are on different schema versions because they edited at different times; one-shot migration scripts are brittle.
3. **Vertical / core decoupling** — a vertical's block evolving doesn't break a tenant that has the vertical disabled; a core block evolving doesn't force every vertical to redeploy.

Options considered:

1. **Single schema per block key, edited in place.** Simple, fast iteration. Breaks instantly when a tenant has stored payloads that no longer match.
2. **Schema-per-block-version (selected).** Stored block instances carry `(key, schemaVersion)`. Renderer picks the matching component or shows a placeholder. Lazy migration on save in the studio. Bulk migration available as a platform operation.
3. **Strict synchronous migration.** Force every stored instance to migrate at deploy time. Operationally heavy; fails open on partial migrations.

Option 2 was chosen because it preserves both forward and backward safety: old pages keep rendering during a rollout, and new pages immediately use the new version.

## Consequences

- Each block ships a `JsonSchema` per `(key, schemaVersion)`. The schema is registered at startup and immutable after first publish to any tenant.
- A breaking change (renamed field, type change, semantics change) creates a new `schemaVersion` with its own schema and renderer component. The previous version remains supported as long as any instance references it.
- A non-breaking change (added optional field with default, additional facet) updates the existing schema; the bumped version still carries the same major number.
- The renderer's resolver:
  - Known `(key, version)` → renders the registered component.
  - Known `key`, unknown `version` → `UnknownVersionBlock` placeholder with a warning log; one block does not crash the page (each block has an error boundary).
  - Unknown `key` → `UnknownBlock` placeholder; triggered when the vertical providing the block is disabled for the tenant.
- Lazy migration: when an editor opens a page in the studio, any block whose schema version is below the registered latest is migrated on save through the block's `migrate(prev, prevVersion)` function. The published version is untouched until the editor explicitly republishes.
- Bulk migration: platform admin can run `POST /v1/platform/blocks/{key}/migrate?from=1&to=2` with a dry-run mode; the operation is audited as `platform-admin` and is rate-limited.
- Removing a `(key, schemaVersion)` is allowed only after a count query confirms zero remaining instances across all tenants. CI integration test prevents accidental removal.
- Verticals namespace their block keys (`english.vocabulary-list`); core block keys are unprefixed. A vertical's block schema is owned by the vertical; the core does not migrate vertical-owned blocks.
- Architecture test asserts that every registered block has a corresponding `JsonSchema` and a renderer component, and that the renderer maps the `(key, version)` tuple exactly.

## References

- [17-page-builder.md](../architecture/17-page-builder.md) — lifecycle rules and resolver safety.
- [11-extension-points.md](../architecture/11-extension-points.md) — how a vertical registers a block.
- [06-extension-model.md](../architecture/06-extension-model.md) — extension surfaces overview.
- [01-architecture-standards.md](../standards/01-architecture-standards.md) — architecture test requirements.
