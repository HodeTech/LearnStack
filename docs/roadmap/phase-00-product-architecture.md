# Phase 00: Product Strategy and Architecture Definition

## Goal

Clarify what LearnStack is, what it is not, and which architectural boundaries should guide its development.

This phase is mostly about decision quality. Concepts that are expensive to change later should be made explicit before implementation begins.

## Key Questions

- Is LearnStack an LMS, a CMS, or an education platform engine?
- Which capabilities belong to the core platform?
- Which capabilities belong to vertical products?
- How should tenant, brand, organization, product, and course concepts be separated?
- How much identity and billing should exist in the core platform?
- Should live online education happen inside the application or through external meeting links?
- Why is online English education the first real vertical product?

## Scope

### Product Definition

- Define the platform vision.
- Define the core platform and vertical product boundary.
- Decide whether the MVP is an education CMS, an LMS, or a combination of both.
- Identify first user roles:
  - Platform admin
  - Tenant admin
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
- Redis and MinIO.
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
- Clear core and vertical product boundary.
- Initial live classroom strategy.

## Completion Criteria

- The team agrees that LearnStack is not a single English learning website.
- Core capabilities and vertical product capabilities are documented.
- The initial technical stack is clear.
- The first roadmap phases are documented.
- Module boundaries are explicit enough to guide implementation.
- The live classroom direction is documented as in-app first, external-link fallback only.

## Risks

- Designing the core platform too broadly and delaying the MVP.
- Hardcoding English learning requirements into the core too early.
- Designing CMS and LMS capabilities as disconnected systems.
- Treating identity, billing, tenancy, or live classroom as features that can be safely bolted on later.

## Phase Exit Decision

Implementation can begin when the core product definition, technology choices, and high-level roadmap are accepted.

