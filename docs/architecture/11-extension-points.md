# Extension Points — Superseded

> **Status: superseded on 2026-05-18.**
>
> This document described the concrete mechanism behind the typed vertical-pack
> extension registry from ADR-0011. That ADR was superseded by
> [ADR-0018: Tenant-Driven Customization Model](../decisions/0018-tenant-driven-customization-model.md)
> on the same date.
>
> The original mechanism (`IModuleExtension`, `IExtensionRegistry`, `LearnStack.Verticals.*`
> assemblies, `tenant_extensions` table) is **not part of the LearnStack design**. It was
> never implemented; this document existed only to elaborate on ADR-0011's plan.

## Where to go instead

LearnStack core is now 100% domain-agnostic. Tenants customise the platform by declaring
**data** in their tenant database — JSON Schemas for content types, page block
definitions, lesson item types, scoring rules, level taxonomies, completion rules, and
custom fields. They do **not** ship code.

- **The decision:** [ADR-0018 — Tenant-Driven Customization Model](../decisions/0018-tenant-driven-customization-model.md).
- **The conceptual model:** [06-extension-model.md](06-extension-model.md) (rewritten
  2026-05-18 to reflect the new model).
- **The deep dive:** [32-tenant-customization-model.md](32-tenant-customization-model.md)
  — data model, worked examples per tenant domain, primitive renderer pipeline, sandbox
  engine for scoring / completion rules, schema versioning, Admin Studio surface.

## What survived from this document

- **Provider adapters.** External providers (payment, email, SMS, storage, search, live
  classroom transport, recording egress) remain interface-based; concrete implementations
  live in `LearnStack.Infrastructure.<Concern>.<Provider>` projects. Architecture tests
  forbid provider SDK types in domain or application layers.
- **Frontend extension points** for page blocks: still real, but now registered by data
  (`tenant_page_blocks`) rather than by code (`IPageBlockRegistration`). The renderer
  composes a fixed set of primitives based on the block's JSON Schema.

## What is gone

- **`IModuleExtension`** — no longer in `LearnStack.SharedKernel`.
- **`IExtensionRegistry`** — no longer in `LearnStack.SharedKernel`.
- **`LearnStack.Verticals.*`** source folder — does not exist.
- **`tenant_extensions`** table — does not exist.
- **"Vertical loaded but not enabled for a tenant"** semantics — does not exist; every
  customisation is declarative tenant data.

This stub is retained only so that older links continue to land somewhere useful. The
file is intentionally short.
