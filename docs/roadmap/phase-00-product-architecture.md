# Phase 00: Product Strategy and Architecture Definition

> **Status: completed (historical).** The language below predates the
> 2026-05-18 redesign and uses "vertical product" in places where the current
> vocabulary is "tenant" — concretely, every "vertical product" question was answered
> by [ADR-0018 Tenant-Driven Customization Model](../decisions/0018-tenant-driven-customization-model.md):
> there are no vertical *products* in code; per-domain shapes are
> **tenant customization data**. Read "vertical product" as "tenant" throughout this
> document. The deliverables list is accurate; the rationale is preserved for
> historical context.
>
> **Two later corrections (2026-08-08).** Neither reopens this phase.
>
> - § Key Questions says a second non-English tenant "runs in parallel" with Phase 10.
>   It does not — it moved forward. The second tenant (a **yoga studio**) is seeded in
>   [Phase 02a Packet 7](phase-02a-kernel-tenancy.md) and rendered in a browser from
>   [Phase 02d](phase-02d-walking-skeleton.md), so the substrate-genericity claim is
>   tested continuously rather than at MVP exit.
>   [Phase 10](phase-10-english-learning-mvp.md) is now a **depth** showcase: it
>   exercises every customization aggregate against one real tenant.
> - § Risks lists "code-challenge" among the domain shapes that must not be hardcoded.
>   That instinct was right and the boundary is now explicit: per
>   [ADR-0018's genericity-boundary amendment](../decisions/0018-tenant-driven-customization-model.md),
>   running a learner's submitted code is **not** expressible as tenant data at all —
>   external capability invocation is a plan-gated platform feature LearnStack writes.
>   See [Platform Vision § Genericity boundary](../architecture/01-platform-vision.md).

## Goal

Clarify what LearnStack is, what it is not, and which architectural boundaries should
guide its development.

This phase is mostly about decision quality. Concepts that are expensive to change
later should be made explicit before implementation begins.

## Key Questions

- Is LearnStack an LMS, a CMS, or an education platform engine?
- Which capabilities belong to the core platform?
- Which per-domain shapes belong to **tenant customization data** rather than core
  code? (Answered by [ADR-0018](../decisions/0018-tenant-driven-customization-model.md).)
- How should tenant, organization, brand, product, and course concepts be separated?
  (Answered by [ADR-0017](../decisions/0017-tenant-organization-hierarchy.md).)
- How much identity and billing should exist in the core platform vs the separate
  `learnstack-hub` repository? (Answered by
  [ADR-0019](../decisions/0019-learnstack-hub.md).)
- Should live online education happen inside the application or through external
  meeting links? (Answered by [ADR-0005](../decisions/0005-live-classroom-media-stack.md).)
- Why is online English education the **first tenant customization showcase**?
  (Answered by Phase 10: it exercises every customization aggregate; a second non-
  English tenant runs in parallel to prove the substrate is generic.)

## Scope

### Product Definition

- Define the platform vision.
- Define the core platform and tenant-customization boundary (what is code vs what is
  tenant data).
- Decide whether the MVP is an education CMS, an LMS, or a combination of both
  (answer: both — same code paths, different `TenantPageBlock` / `TenantContentType`
  data).
- Identify first user roles:
  - Platform admin / Hub operator
  - Tenant admin
  - Org admin
  - Content editor
  - Instructor
  - Learner
  - Visitor

### Domain Discovery

- Tenant model.
- Identity model.
- CMS and page builder model.
- Education catalog model.
- Learning content model.
- Enrollment model.
- Assessment model.
- Scheduling and live classroom model.
- Billing model.
- Analytics event model.

### Architecture Decisions

- .NET 10 backend.
- EF Core and PostgreSQL.
- Valkey and SeaweedFS.
- Next.js frontend.
- Modular monolith.
- Shared-database multi-tenancy for the initial implementation.
- Provider adapter pattern.
- In-app WebRTC classroom with LiveKit as the preferred long-term provider.

### Documentation

- Platform vision.
- Domain model.
- Module boundaries.
- Technical architecture.
- MVP scope.
- Extension model.
- In-app live classroom architecture.
- ADR records.
- Roadmap.

## Deliverables

- Architecture documentation set.
- Initial ADRs.
- Roadmap documentation.
- Clear MVP definition.
- Clear core platform ↔ tenant customization data boundary.
- Initial live classroom strategy.

## Completion Criteria

- The team agrees that LearnStack is not a single English-learning website.
- Core capabilities and **tenant customization** surfaces are documented (no "vertical
  product modules" in code).
- The initial technical stack is clear.
- The first roadmap phases are documented.
- Module boundaries are explicit enough to guide implementation.
- The live classroom direction is documented as in-app first, external-link fallback
  only.

## Risks

- Designing the core platform too broadly and delaying the MVP.
- Hardcoding any tenant's domain shape (CEFR, asana, kyu/dan, code-challenge, …) into
  the core too early.
- Designing CMS and LMS capabilities as disconnected systems.
- Treating identity, billing, tenancy, or live classroom as features that can be
  safely bolted on later.

## Phase Exit Decision

Implementation can begin when the core product definition, technology choices, and high-level roadmap are accepted.

