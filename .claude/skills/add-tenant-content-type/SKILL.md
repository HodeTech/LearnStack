---
name: add-tenant-content-type
description: >
  Author a `TenantContentType` JSON Schema (data, not code) for a tenant —
  `vocabulary-card`, `asana-pose`, `code-challenge`, etc. — and register it through
  the customization module's command so the tenant's editor and runtime can render
  entries. USE FOR: a new content type a specific tenant needs, revising a content
  type's schema (additively or breakingly), seeding a content type for an
  integration test or a demo tenant. DO NOT USE FOR: built-in primitive field types
  (those already exist), a content type you intend to compile into a module
  (forbidden by ADR-0018), or a "useful to everyone" type — that is a Phase 12
  marketplace candidate, still data.
---

# Adding a `TenantContentType`

## Purpose

Tenant-defined content types live as **data** in `tenant_content_types`
([ADR-0018](../../../docs/decisions/0018-tenant-driven-customization-model.md),
[32-tenant-customization-model.md](../../../docs/architecture/32-tenant-customization-model.md)).
This skill walks authoring the schema, getting it past the write-path gates, and
revising it without breaking stored entries.

## When to use

- A tenant needs a domain-shaped content type — `vocabulary-card`, `asana-pose`,
  `code-challenge`, `music-piece`, `speaking-prompt`.
- A content type needs an **additive** change (a new optional field, a widened
  enum) — a `schema_revision` bump.
- A content type needs a **breaking** change (a removed field, a narrowed type, a
  new required field, a tightened enum) — a new `schema_version`.

## When not to use

- A field that fits the primitive set the editor already exposes. Don't reinvent it.
- A "tenant content type" you want to compile into LearnStack core. Forbidden by
  ADR-0018; `Core_Modules_HaveNo_DomainSpecific_Names`
  ([Packet 10](../../../docs/roadmap/phase-02a-kernel-tenancy.md)) rejects it.
- Cross-tenant sharing. [Phase 12](../../../docs/roadmap/phase-12-hub-marketplace.md)
  covers a template marketplace; it is still data sharing, never code.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Tenant id | Yes | Owner of the content type. Taken from `ITenantContext`, never from a request field. |
| Key | Yes | **kebab-case**, unique per tenant per version: `vocabulary-card`. The corpus's worked examples are all kebab-case, and the key appears in URLs and cache keys. |
| Schema version | Yes | Starts at 1. Raised only by a breaking change. |
| JSON Schema | Yes | Draft 2020-12. Must satisfy the profile below. |
| Renderer key | Yes | One of the closed composite set in [32 § 2](../../../docs/architecture/32-tenant-customization-model.md). |
| Display name | Yes | A **localized** map (`{"en": "...", "tr": "..."}`), Pattern B per [Localization Standards](../../../docs/standards/08-localization.md). |

`TenantContentType` is **tenant-wide**. It carries no `organization_id`; the table
is in the "tenant-owned, tenant-wide" class and therefore takes no restrictive
write guards ([Database Standards § Table classes](../../../docs/standards/05-database.md)).

## Workflow

### Step 1: Author the schema against the LearnStack profile

The write path runs four gates
([ADR-0043 § 2](../../../docs/decisions/0043-customization-payload-validation.md)):
the document is JSON, it is inside the profile, it satisfies the draft 2020-12
meta-schema, and it builds. The **profile** is the one the library does not give
you, and every clause is there because the behaviour without it was measured:

| Rule | Consequence of breaking it |
|---|---|
| Root carries `"$schema": "https://json-schema.org/draft/2020-12/schema"`, exactly | Without it, the evaluator picks a different dialect and 2020-12 keywords go silently inert |
| Root is an object schema declaring `properties` | `true` / `false` / `{}` are legal schemas; the first and third switch validation off entirely |
| No `pattern`, `patternProperties`, `propertyNames` | Tenant regexes run with an infinite match timeout; one property measured at 30.2 s |
| No `$id`, `$dynamicRef`, `$dynamicAnchor` | `$id` is process-global state in the evaluator's registry |
| `$ref` is fragment-only (`#/$defs/…`) | An absolute `$ref` is a fetch attempt against an author-chosen address |
| Within [§ 8.4's declared limits](../../../docs/architecture/32-tenant-customization-model.md) | Nesting depth 5, 100 properties, 256 KB per row |

```jsonc
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "type": "object",
  "required": ["term", "definition"],
  "additionalProperties": false,
  "properties": {
    "term":         { "type": "string", "minLength": 1, "maxLength": 80 },
    "termLanguage": { "type": "string", "maxLength": 35 },
    "definition":   { "type": "string", "format": "markdown", "maxLength": 1000 },
    "partOfSpeech": {
      "type": "string",
      "enum": ["noun", "verb", "adjective", "adverb", "preposition", "phrase"]
    },
    "examples":     { "type": "array", "items": { "type": "string", "maxLength": 200 }, "maxItems": 5 },
    "audioAssetId": { "type": "string", "format": "uuid", "x-renderer": "audio" },
    "levelKey":     { "type": "string", "x-taxonomy": "cefr" }
  }
}
```

Rules of thumb:

- `additionalProperties: false` — strict payloads catch typos on save.
- Reference other tenant artefacts **by key**, never by embedding them.
  `levelKey` names a `TenantLevelTaxonomy` item; inlining the taxonomy creates drift.
- Cap every string and array. An unbounded array in a JSONB column is an unbounded
  render loop.
- `format` is an **annotation, not an assertion**, by default — `format: "uuid"`
  does not reject a non-UUID on its own. Use it to drive the renderer
  (`x-renderer`), and use `enum` / `minLength` / `maxLength` when you need the
  value constrained.
- `x-renderer` / `x-taxonomy` / `x-language` are LearnStack extensions. The
  meta-schema ignores them, and LearnStack resolves them after the gates — an
  `x-renderer` against the twelve generic primitives in
  [32 § 2](../../../docs/architecture/32-tenant-customization-model.md), an
  `x-taxonomy` against the tenant's own level taxonomies by key. An unresolvable one
  fails the save, naming the JSON pointer. `x-language` is admitted without being
  resolved because its registry does not exist yet;
  [32 § 8.1](../../../docs/architecture/32-tenant-customization-model.md) says why and
  names the phase that owns it.
- The `x-taxonomy` you name has to exist **before** the content type that references
  it — any revision of it, published or not. Register the taxonomy first.

### Step 2: Register it through the command

Registration goes through the module's command, never through a `DbContext` and
never through the library directly. `Json.Schema` types are confined to one
adapter — `JsonSchema_Net_Types_NotImportedOutsideInfrastructure`
([catalogue](../../../docs/standards/21-architecture-tests-catalogue.md)) — and
handlers reach the evaluator through `IJsonSchemaValidator`.

```csharp
var result = await mediator.Send(new RegisterTenantContentTypeCommand(
    ContentTypeId: guidFactory.NewUuidV7(),
    Key: "vocabulary-card",
    SchemaVersion: 1,
    DisplayName: new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["en"] = "Vocabulary Card",
        ["tr"] = "Kelime Kartı",
    },
    JsonSchema: schemaJson,
    RendererKey: "default-card"));
```

Three things about that call are not incidental. The id is **caller-assigned**, from
`IGuidFactory.NewUuidV7()`, so a retry conflicts on the row it wrote last time
instead of inserting a second one. `DisplayName` is a plain map rather than a
`LocalizedText`: the command contract is the cross-module surface and names only
`SharedKernel` types, so the handler builds the value object one layer in — a
contract naming `Customization.Domain` would put that assembly in the IL of every
module that sends the command. And it lands as a **`Draft`**; publishing is a
second command, because authoring a shape and making it the live answer for a key
are two decisions with different blast radii.

The tenant comes from `ITenantContext`. A command that took a tenant id from the
request would be refused by the database anyway — but it would also be the wrong
shape, and the isolation suite is not the place to discover that.

The handler runs the four gates, writes the row at `status = 'Draft'`, and bumps
the tenant's customization generation **in the same transaction**, which is what
makes every cached definition set unreachable at once
([ADR-0043 § 7](../../../docs/decisions/0043-customization-payload-validation.md)).

`PublishTenantContentTypeCommand(contentTypeId)` then makes it live, deprecating
the revision it succeeds in that same transaction — the partial index admits one
live revision per key, and an aggregate cannot see its siblings, so retiring the
incumbent is the command's work and the index is what catches the case where it
did not happen.

### Step 3: Revise it — additively or breakingly

The two paths are not interchangeable, and the editor decides which one you are on
by diffing your submission against the current revision — it refuses an additive
claim that removes or narrows anything
([Phase 04 § Customization Key Shape](../../../docs/roadmap/phase-04-cms-media-pages.md)).

| Change | Path |
|---|---|
| New optional field, widened enum, added facet | `schema_revision` within the same `schema_version` |
| Removed field, narrowed type, new required field, tightened enum | New `schema_version` |

`(tenant_id, key, schema_version)` identifies one revision; the partial index
`UNIQUE (tenant_id, key) WHERE status = 'Active' AND deleted_at IS NULL` keeps at most one live
definition per concept. Existing entries pin their `schema_version` at creation,
so a breaking revision never invalidates a stored entry — it just stops being the
one new entries are written against.

### Step 4: Read it back

The runtime reads through the module's query, which serves from the
generation-keyed cache and falls back to one indexed query per tenant per
definition set. There is **no** compiled-validator cache — compiling is measured
cheaper than evaluating, so the adapter compiles per call
([ADR-0043 § 6](../../../docs/decisions/0043-customization-payload-validation.md)).

Nothing is schema-validated on the read path. That is deliberate and it is what
makes Step 2 load-bearing: the schema is a **write-time contract**, and any path
that writes a content-entry row without going through the validating command
breaks the trust for every subsequent read.

### Step 5: Tests

For a demo or fixture tenant:

1. Provision the tenant.
2. Register each `TenantContentType`.
3. Write at least one entry per type, and one that must be rejected.
4. Assert the rejection names the offending JSON pointer in Problem Details.

Isolation tests connect as **`learnstack_app`**. A test running as
`learnstack_migration` or a `BYPASSRLS` role passes with every policy inert and
proves nothing.

## Validation

- The schema passes all four gates; a profile violation returns
  `Result.Fail(validation_failed, …)` naming the JSON pointer.
- A valid entry round-trips; an invalid entry is rejected and **no row is written**.
- A `v2` registered beside `v1` leaves `v1` entries valid.
- The generation counter advanced by exactly one per customization write.

## Common pitfalls

- **Compiling the content type into a module.** Forbidden by ADR-0018.
- **PascalCase keys.** The corpus is kebab-case throughout; the key reaches URLs
  and cache keys, and `CacheKey.EnsureValid` rejects a `:` in it.
- **Treating an additive edit as a new version, or the reverse.** The first
  strands entries needlessly; the second breaks them. The diff decides, not the
  author.
- **Embedding referenced data inline.** Use `levelKey: "a1"`, not a copy of the
  taxonomy.
- **Reaching for `pattern`.** It is not admitted; see the profile table for why
  and [ADR-0043 § 5](../../../docs/decisions/0043-customization-payload-validation.md)
  for the trigger that would admit it.
- **Assuming `format` validates.** It annotates. Constrain with `enum`,
  `minLength`, `maxLength`.
- **Importing `Json.Schema` outside the adapter.** One architecture rule exists
  specifically to stop it.
