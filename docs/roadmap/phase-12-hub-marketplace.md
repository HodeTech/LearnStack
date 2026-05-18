# Phase 12: Hub Marketplace (optional, post-MVP)

## Goal

Open a **marketplace** on the `learnstack-hub` side where tenants can publish and
consume **reusable tenant customization data** — content types, page-block schemas,
lesson-item types, level taxonomies, scoring rules, completion rules, custom field
defs, and template libraries. A yoga studio that has authored a particularly good
`AsanaPose` content type can publish it; a new yoga tenant can install it on
provisioning and skip the design step.

This phase is **strictly post-MVP** and **strictly optional**. The platform works
fully without it. If the marketplace never ships, no LearnStack feature breaks. The
phase exists in the roadmap because it is the natural next-step opportunity once the
tenant-customization-as-data model proves itself in Phase 10.

Decisions referenced:

- [ADR-0018 Tenant-Driven Customization Model](../decisions/0018-tenant-driven-customization-model.md)
- [ADR-0019 LearnStack Hub](../decisions/0019-learnstack-hub.md)

## Scope (Tentative)

### Marketplace Aggregates (Hub-side)

- `MarketplaceListing` — a publishable bundle (name, description, version, author,
  license, content).
- `MarketplaceListingVersion` — semver-versioned snapshot.
- `MarketplaceInstall` — record of a tenant installing a listing version.
- `MarketplaceReview` — optional tenant feedback (rating + free text).

### Bundle Shape

A listing is a JSON document carrying:

- One or more `TenantContentType` JSON Schemas.
- Zero or more `TenantPageBlock` definitions + their renderer-key references.
- Zero or more `TenantLessonItemType` definitions + their player-key references.
- Zero or more `TenantLevelTaxonomy` definitions.
- Zero or more `TenantScoringRule` / `TenantCompletionRule` DSL expressions.
- Zero or more `TenantCustomFieldDef` definitions.
- Zero or more `TenantTemplateLibrary` templates.
- Manifest with required LearnStack version, required feature keys, sample data.

### Publisher Flow

- A tenant admin exports a subset of their customization data as a draft listing.
- Sandbox validation runs the bundle against a clean tenant fixture; failures block
  publish.
- Operator review (Hub-side) for compliance / content sanity before public listing.
- Approved listings appear in the marketplace.

### Consumer Flow

- A tenant admin browses the marketplace from Studio.
- "Install" triggers a Hub-side flow that pushes the bundle into the tenant's
  customization aggregates via a dedicated endpoint (new ADR required — adds an
  endpoint to the Hub contract surface).
- Conflicts (same `key` already exists for the tenant) prompt for rename or skip.

### Revenue / Pricing (Out of Scope for Initial)

- The first marketplace iteration is **free-only** — no listing fees, no
  per-install pricing. Pricing models are deferred to a follow-on phase if and when
  the marketplace gains traction.

## Deliverables (Tentative)

- Hub-side marketplace aggregates + schema.
- Sandbox validator that boots a clean LearnStack tenant fixture against a candidate
  bundle.
- Operator review queue + approval flow.
- Tenant-facing marketplace browser inside Studio (LearnStack-core change).
- New Hub endpoint for bundle install — requires a new ADR (the four-endpoint surface
  is closed by default; this is one of the few candidate fifth endpoints).

## Open Questions

- Versioning semantics when a tenant has customized an installed bundle locally.
- Revenue split / pricing model for paid listings (deferred).
- Cross-tenant data leakage prevention in published sample data (the publisher's
  real data must never escape; only schemas + DSL expressions + sample fixtures
  travel).
- Operator review burden at scale.

## Phase Exit Decision

Phase 12 has no fixed entry or exit criteria — it is gated entirely on whether the
tenant-customization-as-data model in production demonstrates demand for
cross-tenant sharing. The roadmap reserves the slot; the team activates it only
when the market signals warrant.
