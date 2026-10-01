# ADR-0051: Ordered Text Card Presentation

## Status

Accepted — 2026-10-02. The maintainer approved the exact presentation contract
and P02d-2 decision package. Extends ADR-0043's schema profile through its dated
Amendment 5; profile and renderer implementation have not started.

**Date:** 2026-10-02
**Deciders:** Cemil (repository maintainer)

## Decision Drivers

- JSONB property order cannot define lesson presentation order.
- The two demo domains need authored field labels and deterministic presentation
  without production branches on tenant, content-type key or taxonomy key.
- The schema profile must validate an extension rather than accepting an unknown
  keyword that a later renderer interprets differently.
- Existing immutable `card` revisions lack presentation metadata. A compatible
  extension must not rewrite them or make fresh built-in seeding invalid.
- First public rendering needs a small safe subset, not speculative Markdown,
  URL/media or HTML execution and sanitization rules.

## Considered Options

1. **Root `x-fields` ordered descriptors** (chosen). One array carries order
   and localized labels; direct correspondence with root properties is validated.
2. **Per-property `x-order` and `x-label`** (rejected). Requires tie, missing-order
   and fallback rules in addition to label validation.
3. **A new presentation column** (rejected here). Adds a second schema contract and
   migration when a bounded extension can preserve the existing exact revision pin.
4. **JSON object order or property-name labels** (rejected). JSONB loses the former;
   the latter cannot deliver authored bilingual labels.
5. **A rich role/layout and markup grammar now** (not selected). Phase 05 owns richer
   learning content; Phase 06 owns additional renderer coverage before its consumers.

## Decision

LearnStack represents ordered text-card fields with an optional root-level
`x-fields` array in a content type's JSON Schema. This extends
[ADR-0043](0043-customization-payload-validation.md) without changing its four gates.
It adds no renderer key, presentation column, live-key binding or compiled cache.

```json
"x-fields": [
  { "name": "concept", "label": { "en": "Concept", "tr-TR": "Kavram" } },
  { "name": "example", "label": { "en": "Example", "tr-TR": "Örnek" } }
]
```

### Profile and semantic validation

- `x-fields` is optional and root-only. If present, it is a nonempty array and each
  descriptor has exactly `name` and `label`. Names exactly match and cover every
  root `properties` key once; duplicate, unknown or omitted properties fail.
- The array's order is the sole field order. Names are ordinal and never normalized
  into a different JSON property. Labels are nonempty Pattern-B `LocalizedText` maps,
  using its existing canonical-locale, length, count and storage-safety bounds.
- Label resolution uses `LocalizedText` fallback, never the raw property name.
  Generic validation does not require every enabled tenant locale: adding a locale
  does not invalidate an immutable schema. Both seed types cover their seed locales.
- Presence selects the text-card profile: root type `object`, direct property
  schemas of type `string`, `additionalProperties: false`, and no alternative
  property shape through references/combinators, enum/const, array or nested object.
  Existing validation constraints such as `required`, `minLength` and `maxLength`
  still apply. No field carries `format`, another `x-*` rendering extension or markup.
- Only the existing `default-card` composite is admitted for a content type carrying
  this profile in P02d-2. Richer profiles require an owning-phase decision before use.
- Keep the four gates in their existing order. Gate 2 recognizes root-only syntax;
  Gate 4 remains provider schema compilation. Customization explicitly resolves
  descriptor/property/label and composite compatibility after all four gates and
  before persistence; the generic validator never reads module registries.
  Failures carry `validation_failed`, existing localized messages and JSON Pointers
  to the offending descriptor, label, property or misplaced keyword.
- Extension traversal and the reference-graph walker treat `x-fields` descriptors
  as metadata, not subschemas. A nested `x-fields` in a real subschema is refused;
  metadata keys do not hide references or unsupported declarations in real schemas.
  A recognized extension without a resolver cannot report semantic success;
  unknown inert schema annotations retain ADR-0043's existing dialect behavior.

### Compatibility and Education writes

Schemas without `x-fields` remain governed by ADR-0043 unchanged. In particular,
built-in `card` stays Active at its original version, without enrichment or an
identity-specific exemption. Built-in `plain` is also unchanged. Neither is selected
by the new demo lessons.

Education validates every submitted body against its exact eligible schema revision,
not against the renderer subset. Other valid schemas can be stored; they do not gain
rendering support merely by passing schema validation. The two seed types deliberately
choose the text-card profile. Missing or unsupported presentation later produces a
bounded placeholder under P02d-6's G41, never raw JSON or inferred field labels.

### Public text boundary

The P02d-6 text-card renders present string values and labels as escaped React text
nodes in descriptor order. Absent optional fields are omitted. A mismatched stored
value is a bounded invalid-content state, never coercion into HTML or a raw dump.
There is no linkification, Markdown parsing, `dangerouslySetInnerHTML`, embed,
URL/media attribute or remote fetch. URL-looking text stays inert text.

This closes G19's P02d-2 scope by admitting no active URL or markup sink in the
seeded profile. It does not choose a URL scheme/origin policy for future sinks.
Before their first producers/consumers, Phase 04's media/CMS and Phase 05's richer
learning content settle a shared URL/markup policy with Security Standards. Phase 06
adds only coverage backed by that contract. Passing `format: uri` alone never
authorizes navigation, fetching or media loading.

## Context

The existing profile recognizes `x-renderer`, `x-taxonomy` and `x-language`.
`x-fields` changes admission and semantic interpretation, so it needs an approved
ADR rather than silently extending a validator. Root array parsing preserves order
through JSONB without depending on object order.

The existing built-in card is valid but has no authored field order/labels. Making
the extension mandatory for all `default-card` definitions would break immutable
data or require a tenant/type-specific exception. Optional presence provides an
explicit compatible opt-in. Full label coverage is a seed obligation, not a new
cross-module locale-membership invariant.

## Consequences

- One descriptor contract drives validated authored order and labels in both domains.
- No migration, new primitive or renderer-registry entry is necessary.
- Text-only rendering avoids an active-content security policy before a real sink.
- Old revisions retain validity but do not automatically acquire rich presentation.
- Additional shapes and markup need decisions in their named owning phases.

## Implementation Notes

- P02d-2 Step 1 adds profile parsing/resolution and exact-definition DTOs; Step 4
  publishes two explicit text-card types and verifies their order/labels on reruns.
- P02d-4 resolves public descriptors only after content-access eligibility.
- P02d-6 implements this subset; G41 still decides component placement and fallbacks.
- ADR-0043 Amendment 5 links this extension without rewriting its original decision.
  The phase register records G18/G19's accepted P02d-2 parts; later sink and renderer
  decisions remain with their named owners.

## Architecture Tests

Accepted obligations, not implemented tests:

- Reject malformed, nested, duplicate, missing and unknown descriptors with pointers.
- Reject invalid labels, unsupported shape/annotations and incompatible composites.
- Preserve all four gates and legacy schemas without `x-fields`.
- Array order survives storage; both seeded definitions cover their enabled locales.
- Unsafe-looking strings remain text; unsupported data never reaches an active sink.
- Changed seed schemas/labels under an existing exact pin fail convergence.

Reserve agreed rule names as Registered before code; mark them Implemented only
when their tests exist and run.

## References

- [P02d-2 decision package](../roadmap/phase-02d-walking-skeleton.md#p02d-2-decision-package-2026-10-02)
- [Customization module](../modules/customization/README.md)
- [Tenant customization model](../architecture/32-tenant-customization-model.md)
- [Localization](../standards/08-localization.md)
- [Security](../standards/11-security.md#xss--output-encoding)
- [ADR-0018](0018-tenant-driven-customization-model.md)
