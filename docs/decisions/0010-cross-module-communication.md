# ADR 0010: Cross-Module Communication

## Status

Accepted

## Decision

Modules in the LearnStack modular monolith communicate through exactly four sanctioned mechanisms, and no others:

1. **Application contract (Read / Command API)** — interface in `<Module>.Application.Contracts`, returning DTOs, executed in-process.
2. **Domain event** — intra-module only, in-process, in the same transaction as the aggregate change.
3. **Integration event** — written to the outbox in the same transaction as the domain change; dispatched at-least-once to subscribers with backoff and dead-lettering. See [ADR 0006: Events and Outbox](0006-events-and-outbox.md).
4. **Read-model projection** — a public read-only table owned by one module, refreshed by an integration event handler; readable by other modules but only writable by the owner.

The following are **forbidden**:

- Cross-module EF Core navigation properties.
- Cross-module SQL joins against tables owned by different modules.
- Importing another module's `Domain` or `Infrastructure` namespace.
- Sharing mutable domain entities across module boundaries.
- Vertical-specific rules inside core modules.

## Context

Modular monoliths collapse into a tangle the moment cross-module shortcuts become acceptable. A junior engineer with a deadline will reach into another module's `DbContext` if the rules are not explicit and enforced. The cost of that single edit is years of coupling.

Four mechanisms are enough to cover every realistic interaction:
- "Read another module's data" → read API (sync) or read-model projection (eventually consistent).
- "Ask another module to do something now" → command API.
- "React to a state change in another module" → integration event.
- "React to a state change in my own module" → domain event.

A fifth mechanism does not pay back. Limiting to four keeps the cognitive load manageable and the enforcement tractable.

## Consequences

- Every module ships an `<Module>.Application.Contracts` project; this is the only public surface.
- Integration events carry `tenant_id`, `event_id`, `occurred_at`, and a versioned `type`.
- The outbox is the transactional boundary; if a producer's transaction rolls back, the event is not published.
- Handlers must be idempotent — at-least-once delivery.
- Read-model projections are named `public_<module>_<concept>` and refresh via integration events.
- Architecture tests fail the build when any forbidden pattern is detected.
- When a module is later promoted to a separate service, only the transport layer changes; the contract shape stays.

## Architecture Tests

The following tests live in `LearnStack.Tests.Architecture` and run on every PR:

- Modules do not depend on other modules' `Domain` or `Infrastructure` namespaces.
- Integration event types are JSON-serialisable records.
- Read-model tables follow the `public_<module>_<concept>` naming.
- Hangfire job payloads include `tenant_id`.
- Provider SDK types are not imported in `Domain` or `Application`.

## References

- [Cross-Module Contracts](../architecture/10-cross-module-contracts.md)
- [Event and Outbox](../architecture/15-event-and-outbox.md)
- [Architecture Standards](../standards/01-architecture-standards.md)
