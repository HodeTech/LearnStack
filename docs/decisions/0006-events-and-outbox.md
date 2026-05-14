# ADR 0006: Events and Outbox

## Status

Accepted

## Decision

LearnStack uses domain events inside module boundaries and integration events across module boundaries.

Integration events are written to an outbox in the same transaction as the state change that produced them. A worker dispatches outbox events to internal handlers, projections, or external integrations.

## Context

The modular monolith must avoid direct cross-module database coupling and should remain extractable into services later.

## Consequences

- Domain events are internal to a module.
- Integration events are versioned contracts.
- Outbox dispatch is idempotent.
- Background workers preserve tenant context.
- Event schema changes require compatibility review.

