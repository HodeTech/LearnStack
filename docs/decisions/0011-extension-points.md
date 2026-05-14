# ADR 0011: Vertical Extension Points

## Status

Accepted

## Decision

Vertical products (English-learning, exam prep, corporate academy, …) extend LearnStack only through a fixed, typed registry of extension points exposed by core. A vertical may **not** modify core modules to add behaviour.

The current extension surfaces are:

| Surface | Registry |
|---|---|
| Level taxonomies | `IExtensionRegistry.Levels` |
| Assessment scoring strategies | `IExtensionRegistry.Assessments` |
| Completion rule providers | `IExtensionRegistry.CompletionRules` |
| Entitlement sources | `IExtensionRegistry.EntitlementSources` |
| Page blocks | `IExtensionRegistry.PageBlocks` |
| Content types | `IExtensionRegistry.ContentTypes` |
| Lesson item types | `IExtensionRegistry.LessonItemTypes` |
| Payment providers | `IExtensionRegistry.PaymentProviders` |
| Live class providers | `IExtensionRegistry.LiveClassProviders` |
| Notification channels | `IExtensionRegistry.NotificationChannels` |
| Frontend block components | `IExtensionRegistry.FrontendBlocks` |
| Portal widgets | `IExtensionRegistry.PortalWidgets` |
| Integration event subscriptions | `IExtensionRegistry.EventSubscriptions` |

Each vertical implements `IModuleExtension` and registers contributions at startup. A vertical's tables live in its own PostgreSQL schema; core tables remain untouched.

Adding a new extension surface requires a code change in core. This is intentional — core controls what verticals can hook into.

## Context

The first vertical (English learning) creates pressure to add CEFR levels, placement-test scoring, vocabulary banks, and speaking-session metadata. Every one of these is a candidate to leak into core if no explicit boundary exists.

The risk is not the first vertical — it is the second. If the English vertical bends core to fit CEFR, the second vertical (exam prep) cannot land cleanly. The whole "platform for multiple education products" thesis fails.

Typed extension surfaces are the discipline that makes verticals composable. They turn "where do I put this?" into a one-answer question.

## Consequences

- Verticals ship as their own assemblies with their own EF schema.
- Verticals declare a key (`english`, `exam_prep`, ...) used for tenant enablement and namespacing.
- Page-block keys, scoring-strategy keys, content-type keys, and event-handler keys are namespaced by vertical.
- Tenants enable verticals via `tenant_extensions`; a vertical loaded but not enabled for a tenant remains inactive for that tenant.
- A vertical failing to register or migrate cleanly **fails application startup**.
- Architecture tests forbid verticals from referencing core `Domain` or `Infrastructure` namespaces and from depending on other verticals.
- Removing an extension surface from core is a deprecation cycle, not a sudden change.

## Validation

Phase 10 (English Learning Vertical MVP) is the architectural test: if the English vertical can land without modifying core modules, the extension model works. Any pressure to change core during Phase 10 must either reveal a missing extension surface (and adding it is the answer) or be redesigned as a vertical-internal solution.

## References

- [Extension Model](../architecture/06-extension-model.md)
- [Extension Points](../architecture/11-extension-points.md)
- [Architecture Standards — Vertical Modules](../standards/01-architecture-standards.md)
