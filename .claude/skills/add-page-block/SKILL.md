---
name: add-page-block
description: >
  Add a page block to LearnStack's two-tier block registry — either a **built-in
  primitive** (code; closed set; ships with the platform) or a **tenant-defined
  block** (data; `TenantPageBlock` row pointing at a composite renderer key).
  USE FOR: adding a generic block every tenant might use (built-in primitive),
  authoring a tenant-specific block via tenant customization data, defining a new
  composite renderer key the runtime resolves to a React component.
  DO NOT USE FOR: per-domain block keys (`english.vocabulary-list` is forbidden —
  use a `TenantPageBlock` row for the English tenant), bypassing the schema versioning
  rules in ADR-0013, or registering a tenant's block in code.
---

# Adding a page block

## Purpose

Land a new page block correctly in the two-tier registry per
[ADR-0013 Page Block Schema Versioning](../../../docs/decisions/0013-page-block-schema-versioning.md)
+ [ADR-0018 Tenant-Driven Customization Model](../../../docs/decisions/0018-tenant-driven-customization-model.md)
and [17-page-builder.md](../../../docs/architecture/17-page-builder.md).

## When to use

- A new **built-in primitive** block (`hero`, `rich-text`, `card-grid`, …) that
  every tenant might use.
- A new **composite renderer key** (`default-card`, `content-list`, …) — these are
  code-registered renderers that tenant-defined blocks can dispatch to.
- A **tenant-defined block** authored as a `TenantPageBlock` row pointing at one
  of the composite renderer keys.

## When not to use

- A domain-specific block in code (`english.vocabulary-list`, `yoga.asana-card`).
  Forbidden. The English tenant ships `TenantPageBlock(key="vocabulary-list")`
  pointing at the built-in `content-list` composite; same for any other tenant.
- A breaking schema change to an existing block's `(key, version)`. Ship a new
  version (`v2`); never edit `v1` in place.
- A block whose schema can't be expressed as JSON Schema — that's a sign the
  primitive set needs a new composite, not a one-off block.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Block key | Yes | Dotted lowercase: `hero`, `rich-text`, `course-list`. No domain prefixes. |
| Block tier | Yes | **Primitive** (code) / **Composite renderer** (code) / **Tenant block** (data). |
| Schema version | Yes | New primitive starts at v1; tenant block also at v1. |
| JSON Schema | Yes | The block's payload shape. |
| Renderer component | Primitive / Composite only | The React component (Server / Client). |

## Workflow

### Step 1: Pick the tier

| Tier | Lives where | Authorship | When |
|------|-------------|------------|------|
| **Primitive** | C# block registry + React renderer in `frontend/apps/web/src/components/blocks/`. | LearnStack engineering. | Every tenant might use it (`hero`, `rich-text`, `image`, `cta`). |
| **Composite renderer** | C# composite registry + React renderer (`default-card`, `content-list`, `card-grid`). | LearnStack engineering. | Tenants compose this in `TenantPageBlock` rows. |
| **Tenant block** | `tenant_page_blocks` row (data only). | Tenant admin via Studio editor. | Tenant-specific shape (`vocabulary-list` for English, `asana-card` for yoga). |

### Step 2: Primitive block (path A)

#### A.1 — Author the JSON Schema

```jsonc
// backend/src/Modules/Content/Application/PageBlocks/Hero/v1/schema.json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "type": "object",
  "required": ["heading"],
  "properties": {
    "heading": { "type": "string", "maxLength": 200 },
    "subheading": { "type": "string", "maxLength": 400 },
    "imageAssetId": { "type": "string", "format": "uuid" },
    "cta": {
      "type": "object",
      "properties": {
        "label": { "type": "string", "maxLength": 60 },
        "href": { "type": "string", "format": "uri" }
      }
    }
  }
}
```

Schemas are **immutable** after publish. A breaking change ships `v2/schema.json`;
`v1` stays supported during the migration window.

#### A.2 — Register in code

```csharp
// backend/src/Modules/Content/Application/PageBlocks/PageBlockRegistry.cs
registry.RegisterPrimitive(
    key: "hero",
    schemaVersion: 1,
    schemaPath: "PageBlocks/Hero/v1/schema.json");
```

#### A.3 — React renderer

```tsx
// frontend/apps/web/src/components/blocks/hero/v1.tsx
export const HeroBlockV1: BlockRenderer<HeroPayloadV1> = ({ data }) => (
  <section className="hero">
    <h1>{data.heading}</h1>
    {data.subheading && <p>{data.subheading}</p>}
    {data.cta && <a href={data.cta.href}>{data.cta.label}</a>}
  </section>
);
```

Register the renderer:

```ts
// frontend/apps/web/src/components/blocks/registry.ts
resolver.registerPrimitive("hero", 1, HeroBlockV1);
```

### Step 3: Composite renderer (path B)

A composite renderer is a generic primitive that tenant-defined blocks dispatch
to. Examples: `default-card`, `content-list`, `card-grid`.

Composites accept:

- A `TenantContentType` reference (which content type to fetch).
- Layout knobs (columns, sort, filter).
- Style knobs (theme overrides).

```tsx
// frontend/apps/web/src/components/blocks/composites/content-list.tsx
export const ContentListComposite: CompositeRenderer = ({ payload }) => {
  const items = useContentEntries(payload.contentTypeKey, payload.filter);
  return (
    <ul className={`columns-${payload.columns ?? 1}`}>
      {items.map((item) => <li key={item.id}>{renderEntry(item)}</li>)}
    </ul>
  );
};

resolver.registerComposite("content-list", ContentListComposite);
```

Composite renderer keys are **closed in code**; tenants cannot bring new
composites. Adding a composite is a LearnStack release decision.

### Step 4: Tenant block (path C)

#### C.1 — Tenant authors via Studio

In Admin Studio's customization editor:

1. Create a `TenantPageBlock` row.
2. Provide a key (`vocabulary-list`).
3. Provide a JSON Schema for the payload.
4. Pick a composite renderer key from the dropdown (e.g. `content-list`).
5. Map schema fields → composite's payload knobs (e.g.
   `payload.contentTypeKey = "VocabularyCard"`).

No code change.

#### C.2 — Runtime resolution

At render time, the resolver:

1. Looks up the `TenantPageBlock` row by `(tenant_id, key, schemaVersion)`.
2. Validates the block instance's payload against the schema.
3. Resolves the composite renderer via the row's `composite_renderer_key`.
4. Dispatches.

### Step 5: Schema versioning

`(key, schemaVersion)` is the identity of a block payload shape. Rules:

- Immutable after publish.
- Breaking change → new `(key, v+1)`. `v1` stays supported until all stored
  instances are migrated (lazy on save in Studio; bulk migration is a platform-
  admin operation).
- Backward-compatible additions (optional new field) — increment `v` anyway; the
  old `v` keeps working.

### Step 6: Renderer-safety placeholders

Unknown `(key, version)`:

- Known `key`, unknown `version` → `UnknownVersionBlock` placeholder + warning log.
- Unknown `key` → `UnknownBlock` placeholder + warning log.

Each block renders inside a React error boundary; one block crashing doesn't crash
the page.

### Step 7: Tests

- JSON Schema validation test for the new primitive / tenant schema.
- Renderer snapshot test.
- Accessibility test (`axe-core` violations fail).
- Lighthouse budget check for representative pages embedding the block.

## Validation

- `dotnet build` and `pnpm build` pass.
- Architecture test `Block_Schemas_Are_Immutable_After_Publish` is green.
- For a primitive: the block appears in the Studio block picker.
- For a composite: tenant admins can reference it from their `TenantPageBlock`
  editor.
- For a tenant block: a page can be authored that uses the new block; the
  renderer dispatches correctly.
- Accessibility (`axe-core`) and contrast checks pass.

## Common pitfalls

- **Per-domain block key in code.** `english.vocabulary-list` is forbidden. Use a
  `TenantPageBlock(key="vocabulary-list")` row in the English tenant's data.
- **Editing a published schema in place.** Bump to `v2`. The lazy + bulk migration
  path expects `(key, v)` to be immutable.
- **Closing the composite registry too tightly.** Composites are the
  generalisation point. Adding a composite is normal; adding a *primitive* is
  rarer.
- **Renderer reading the tenant context.** Block renderers receive their payload;
  they don't read tenant id directly. Avoid coupling to context.
- **Forgetting the placeholder path.** Schema drift WILL happen; the placeholder
  is the safety net.
- **Client Component for a static block.** Server Component first; only use
  Client when interactivity (forms, video, classroom panel) demands it.
