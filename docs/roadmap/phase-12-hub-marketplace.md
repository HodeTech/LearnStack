# Phase 12: Hub Marketplace (pointer, optional, post-MVP)

> **Pointer document.** The authoritative plan for the marketplace lives in the
> **`learnstack-hub`** repository at `docs/roadmap/hub-marketplace.md`. The listing
> aggregates, bundle format, sandbox validator, publisher and consumer flows, and the
> operator review queue are Hub concerns and are described there. This file records why
> the slot exists, where the LearnStack boundary falls, and what would start the work.

## Goal

Let tenants publish and install **reusable tenant customization data** — content types,
page-block schemas, lesson-item types, level taxonomies, scoring and completion rules,
custom field definitions, template libraries. A yoga studio that has authored a good
pose content type publishes it; a new yoga tenant installs it at provisioning and skips
the design step.

The marketplace is the natural commercial consequence of
[ADR-0018](../decisions/0018-tenant-driven-customization-model.md): if a tenant's
product really is data, then that data is transferable. It only sells anything the
customization model can express — the [ADR-0018 genericity
boundary](../decisions/0018-tenant-driven-customization-model.md) applies unchanged, so
a listing never carries executable capability, only shape, presentation and pure rules.

## Standing

**Strictly optional and strictly post-MVP.** The platform is complete without it; if the
marketplace never ships, no LearnStack feature breaks. Nothing on the roadmap spine
depends on this phase.

## Scope on the LearnStack side

- A marketplace browser and an install action inside Studio, both reading through a
  named Hub adapter.
- An install path that writes into the tenant's own customization aggregates, with
  conflict handling when a `key` already exists for that tenant.
- A new Hub endpoint for bundle install. That requires its own ADR — not because the
  contract surface has a fixed size, but because it is a cross-repository contract that
  both repositories must agree on
  ([ADR-0034](../decisions/0034-hub-contract-surface-invariant.md)).
- **Whether a published bundle counts as tenant content under ADR-0034's first
  invariant is unresolved, and it blocks the track.** A listing is authored by a
  tenant, describes that tenant's product, and may carry sample fixtures drawn from
  that tenant's data. The Hub repository's `hub-marketplace.md` § The unresolved
  collision with ADR-0034 states the three candidate answers — metadata-in-Hub with
  the body outside, a separate service, or a narrow amendment — and requires an
  accepted ADR before any code. That is the same ADR named in the bullet above; it
  answers where listings live, not merely how they are installed.

## Pricing and revenue

The first iteration is **free-only** — no listing fees, no per-install pricing. Paid
listings, revenue splits and payout mechanics are **not on this roadmap at all**. They
enter it only if production usage produces evidence that tenants want to sell
customization bundles, at which point they are scoped in the Hub repository's
`docs/roadmap/hub-marketplace.md` alongside the rest of the marketplace design.

## Trigger

Phase 12 starts when the customization-as-data model in production demonstrates demand
for cross-tenant sharing — tenants asking for another tenant's content type, or
operators repeatedly hand-copying customization data between tenants at provisioning.
Absent that signal, the roadmap reserves the slot and nothing else.

## Phase Exit Decision

Phase 12 has no fixed entry or exit criteria, because it has no fixed commitment. The
gate is the trigger above: without production evidence of demand the phase does not
start, and starting it means adopting the Hub repository's plan and its exit criteria as
written there.
