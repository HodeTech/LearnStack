---
name: add-tenant-content-type
description: >
  Author a `TenantContentType` JSON Schema (data, not code) for a tenant —
  `VocabularyCard`, `AsanaPose`, `CodeChallenge`, etc. — and seed it into the
  customization module so the tenant's UI editor and runtime can render entries.
  USE FOR: a new content type a specific tenant needs, evolving a content type's
  schema (with versioning), seeding a content type for an integration test or
  showcase tenant. DO NOT USE FOR: built-in primitive field types (those already
  exist), generic "this content type might be useful to everyone" — that's a
  marketplace candidate, not core code, or domain-specific code in any module
  (forbidden by ADR-0018).
---

# Adding a `TenantContentType`

## Purpose

Tenant-defined content types live as **data** in
`tenant_content_types` ([ADR-0018](../../../docs/decisions/0018-tenant-driven-customization-model.md),
[32-tenant-customization-model.md](../../../docs/architecture/32-tenant-customization-model.md)).
This skill walks the schema + seed + runtime validation workflow.

## When to use

- A tenant needs a domain-shaped content type — `VocabularyCard`, `AsanaPose`,
  `CodeChallenge`, `MusicPiece`, `SpeakingPrompt`.
- A content type's schema needs a non-breaking addition (new optional field).
- A content type's schema needs a breaking change — ship a new `schemaVersion`.

## When not to use

- A field that fits the primitive field set already exposed by the editor (text,
  rich text, number, boolean, date, media reference, entry reference, select,
  JSON). Don't reinvent the wheel.
- A "tenant content type" that you want to compile into LearnStack core. Forbidden
  by ADR-0018 (architecture test
  `Core_Modules_HaveNo_DomainSpecific_Names` rejects).
- Cross-tenant sharing of a content type. Phase 12 Marketplace covers that;
  Phase 10 ships per-tenant.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Tenant id | Yes | Owner of the content type. |
| Key | Yes | PascalCase, unique per tenant: `VocabularyCard`. |
| Schema version | Yes | Starts at 1. |
| JSON Schema | Yes | The payload shape. |
| Org scope | No | If the type is org-specific, set `organization_id`; else tenant-wide. |

## Workflow

### Step 1: Author the schema

Use [JSON Schema 2020-12](https://json-schema.org/draft/2020-12/schema) syntax:

```jsonc
// docs/customization/<tenant-slug>/content-types/vocabulary-card/v1.json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "title": "VocabularyCard",
  "type": "object",
  "required": ["term", "definition"],
  "properties": {
    "term": { "type": "string", "minLength": 1, "maxLength": 80 },
    "termLanguage": { "type": "string", "pattern": "^[a-z]{2}(-[A-Z]{2})?$" },
    "definition": { "type": "string", "minLength": 1, "maxLength": 1000 },
    "partOfSpeech": {
      "type": "string",
      "enum": ["noun", "verb", "adjective", "adverb", "preposition", "phrase"]
    },
    "examples": {
      "type": "array",
      "items": { "type": "string", "maxLength": 200 },
      "maxItems": 5
    },
    "audioAssetId": { "type": "string", "format": "uuid" },
    "levelKey": {
      "type": "string",
      "description": "References TenantLevelTaxonomy item key (e.g. 'a1', 'b2')."
    }
  },
  "additionalProperties": false
}
```

Rules:

- `additionalProperties: false` — keep payloads strict.
- Reference other tenant artefacts by **key**, not embedded JSON
  (`levelKey: "a1"` instead of inlining a level taxonomy item).
- Limit string lengths and array lengths — protects the storage column.
- Use `format: uuid` for asset references; the validator can enforce existence
  at write time.

### Step 2: Seed the `TenantContentType` row

The seed runs as part of tenant provisioning (or for integration tests, as part
of the test fixture):

```csharp
await mediator.Send(new RegisterTenantContentTypeCommand(
    TenantId: tenantId,
    OrganizationId: null,                          // tenant-wide
    Key: "VocabularyCard",
    SchemaVersion: 1,
    SchemaJson: File.ReadAllText("v1.json"),
    DisplayName: "Vocabulary Card",
    Description: "A vocabulary item for English learners.",
    SampleEntries: vocabSampleEntries));
```

The seed:

1. Validates the JSON Schema itself (well-formedness via Json.Net schema parser).
2. Validates `SampleEntries` against the schema.
3. Inserts the `tenant_content_types` row.
4. Emits `learnstack.customization.content-type` integration event so downstream
   consumers (search index, page-block resolver cache) refresh.

### Step 3: Schema versioning

A breaking change → new `(key, schemaVersion+1)` row. The old row stays until all
entries are migrated.

```csharp
await mediator.Send(new RegisterTenantContentTypeCommand(
    Key: "VocabularyCard",
    SchemaVersion: 2,
    SchemaJson: File.ReadAllText("v2.json"),
    // ...
));
```

Existing entries against `v1` keep validating against `v1`; new entries against
`v2`. The Studio's bulk-migration tool walks `v1` entries on demand.

Backward-compatible additions (an optional new field) still increment the version
— immutability is the rule.

### Step 4: Runtime read

The renderer (and the Studio editor) reads via `ITenantContentTypeService`:

```csharp
var contentType = await contentTypes.GetAsync(
    tenantId, "VocabularyCard", schemaVersion: null /* latest */, ct);

var validator = JsonSchema.FromText(contentType.SchemaJson);
var validation = validator.Evaluate(entryPayload);
if (!validation.IsValid)
    return Result.Fail<ContentEntryDto>(
        LocalizedMessage.Of("content_type.payload_invalid"));
```

The reader is per-tenant; the schema is **never** loaded from a global registry.
Architecture test `ContentEntry_Payload_ValidatesAgainst_TenantContentType_Schema`
enforces validation at write time.

### Step 5: Studio editor integration

The Studio's content-type editor surfaces:

- A JSON Schema editor (with live validation against draft 2020-12).
- A sample-entry preview (the operator can paste a JSON entry and see the
  validator's response).
- A "Where used" panel listing pages / blocks / lessons that reference this type.

You don't write any new editor code; the customization-module editor handles every
`TenantContentType` uniformly.

### Step 6: Tenant-side tests

For a showcase tenant (e.g. the English tenant in Phase 10), the fixture should:

1. Provision the tenant.
2. Seed every `TenantContentType` row.
3. Create at least one `ContentEntry` per type.
4. Confirm round-trip through the renderer.

```csharp
[Fact]
public async Task EnglishTenant_VocabularyCard_validates_against_v1_schema()
{
    var entry = new {
        term = "ephemeral",
        termLanguage = "en",
        definition = "Lasting for a very short time.",
        partOfSpeech = "adjective",
        examples = new[] { "The ephemeral nature of cherry blossoms." },
        levelKey = "c1"
    };

    var result = await mediator.Send(new CreateContentEntryCommand(
        tenantId,
        contentTypeKey: "VocabularyCard",
        payload: JsonSerializer.Serialize(entry)));

    Assert.True(result.IsSuccess);
}

[Fact]
public async Task EnglishTenant_VocabularyCard_rejects_invalid_payload()
{
    var bad = new { term = "x" /* missing definition */ };
    var result = await mediator.Send(new CreateContentEntryCommand(...));
    Assert.False(result.IsSuccess);
    Assert.Contains("content_type.payload_invalid", result.Errors);
}
```

## Validation

- The JSON Schema file is well-formed (`pnpm validate:schemas` or equivalent).
- The seed registers without conflict.
- A valid entry round-trips; an invalid entry is rejected with the right
  `LocalizedMessage` key.
- A `v2` schema seeded alongside `v1` lets both versions of entries coexist.
- Architecture test
  `ContentEntry_Payload_ValidatesAgainst_TenantContentType_Schema` is green for
  any new entry path.

## Common pitfalls

- **Compiling the content type into a module.** Forbidden by ADR-0018. The
  schema is data; the registration is per-tenant.
- **Re-using the same `key` for two distinct shapes.** A tenant's `VocabularyCard`
  is one shape; renaming or repurposing it without a `v+1` breaks stored entries.
- **Embedding referenced data inline.** Use keys (`levelKey: "a1"`), not embedded
  level definitions. The level taxonomy is its own aggregate; embedding creates
  drift.
- **Skipping `additionalProperties: false`.** Strict schemas catch typos
  immediately; loose schemas accept garbage that surfaces months later in
  rendering.
- **Cross-tenant content-type sharing in code.** That's Phase 12 Marketplace
  territory (still data-only), not a `learnstack-core` PR.
- **JSON Schema features the runtime doesn't support.** Stick to draft 2020-12
  basics; `$ref` to other tenant artefacts is not supported (the renderer would
  need to resolve cross-row, which complicates caching).
